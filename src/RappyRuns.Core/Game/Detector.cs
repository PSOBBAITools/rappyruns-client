using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Game;

/// <summary>Detector state (detect.lisp:22): InQuest while any tracker is still running.</summary>
public enum DetectorState
{
    Idle,
    InQuest,
}

/// <summary>
/// The quest detection state machine (detect.lisp, spec core §14) -
/// <b>parity</b>: server rankings depend on its output. Pure: it consumes
/// snapshots (null when the game is unreadable) and returns the runs that
/// completed (or were abandoned) on that frame, as Lisp-shaped plists
/// (<see cref="Plist"/>), because the run flows unchanged into the persisted
/// queue and the POST /api/runs body (<see cref="RunPayload"/>).
/// <para>
/// One loaded quest can match several definitions (the full clear plus
/// segment categories that end earlier); each gets its own tracker, so one
/// full run also yields every segment record. Not thread-safe: the poll
/// thread owns it; other threads read <see cref="State"/> and the accessors.
/// </para>
/// </summary>
public sealed class Detector(QuestCatalog catalog, IGameClock clock)
{
    /// <summary>detect.lisp:247 +abort-min-ms+: abandoned runs shorter than this are noise.</summary>
    public const long AbortMinMs = 15000;

    private readonly List<Tracker> _trackers = []; // this load's trackers, oldest first, done ones kept
    private float? _myPb;
    private bool _pbFlag;
    private readonly List<int> _seenAlive = [];
    private readonly List<int> _killedIds = [];
    private volatile int _state; // DetectorState, readable from other threads

    public DetectorState State => (DetectorState)_state;

    /// <summary>True once "no quest loaded" was seen; a mid-quest attach never starts a run.</summary>
    public bool Armed { get; private set; }

    /// <summary>Quest struct pointer of the loaded quest (reload detection).</summary>
    public long? QuestPtr { get; private set; }

    /// <summary>The submitter's own name colour kept for this load (spec core §13).</summary>
    public long? MyNameColor { get; private set; }

    /// <summary>Per-quest telemetry, created with the first tracker of the load.</summary>
    public Telemetry? Telemetry { get; private set; }

    /// <summary>The PB category flag of this load (all trackers share it).</summary>
    public bool PbFlag => _pbFlag;

    /// <summary>Monster ids confirmed killed this load (for (:monster-dead ID)).</summary>
    public IReadOnlyList<int> KilledIds => _killedIds;

    // Immutable view of the unfinished trackers, republished after every change,
    // so the UI thread can read the accessors below while the poll thread steps.
    private volatile (QuestDef Def, long StartTime)[] _activeView = [];

    /// <summary>detect.lisp:48 detector-active-def: the definition of the longest-running unfinished tracker.</summary>
    public QuestDef? ActiveDef => _activeView is [var first, ..] ? first.Def : null;

    /// <summary>detect.lisp:53 detector-active-count.</summary>
    public int ActiveCount => _activeView.Length;

    /// <summary>detect.lisp:56 detector-elapsed-ms: elapsed time of the oldest unfinished tracker, or null.</summary>
    public long? ElapsedMs => _activeView is [var first, ..]
        ? LispMath.ElapsedMs(clock.Now, first.StartTime, clock.TicksPerSecond)
        : null;

    /// <summary>The unfinished trackers (definition + start tick), oldest first.</summary>
    public IReadOnlyList<(QuestDef Def, long StartTime)> ActiveTrackers => _activeView;

    private void PublishView() => _activeView = _trackers.Where(t => !t.Done).Select(t => (t.Def, t.StartTime)).ToArray();

    private long Elapsed(Tracker tracker) => LispMath.ElapsedMs(clock.Now, tracker.StartTime, clock.TicksPerSecond);

    /// <summary>
    /// detect.lisp:258 detector-step: feed one snapshot (null when the game is
    /// unreadable or gone). Returns the runs completed this frame, aborted ones
    /// (reload) first, then completions in tracker order.
    /// </summary>
    public List<Plist> Step(Snapshot? snapshot)
    {
        if (snapshot is null)
        {
            // Game gone: abandon everything and disarm.
            var aborted = AbandonTrackers();
            Reset();
            Armed = false;
            return aborted;
        }
        if (!snapshot.QuestLoaded)
        {
            // Lobby / free field: reset and arm.
            var aborted = AbandonTrackers();
            Reset();
            Armed = true;
            return aborted;
        }

        var completed = new List<Plist>();
        var ptr = snapshot.QuestPtr!.Value;
        // Quest reloaded or a different quest: in-flight runs are void (armed stays).
        if (QuestPtr is { } previous && previous != ptr)
        {
            completed.AddRange(AbandonTrackers());
            Reset();
        }
        QuestPtr = ptr;
        // From the first loaded frame, before any tracker starts or finishes on it.
        UpdateNameColorTracking(snapshot);
        var started = new List<Tracker>();
        if (Armed)
        {
            foreach (var def in SnapshotQuestDefs(snapshot))
            {
                if (_trackers.Any(t => ReferenceEquals(t.Def, def))) continue;
                if (TriggerMet(def.Start, snapshot, null)) started.Add(StartTracker(def, snapshot));
            }
        }
        if (_trackers.Any(t => !t.Done))
        {
            UpdatePbTracking(snapshot);
            UpdateKillTracking(snapshot);
            Telemetry?.Step(snapshot, clock.Now);
        }
        // End checks skip trackers started this frame: a real end trigger
        // cannot fire on the start frame, only stale data could.
        foreach (var tracker in _trackers.ToList())
        {
            if (tracker.Done || started.Contains(tracker)) continue;
            if (TriggerMet(tracker.Def.End, snapshot, _killedIds)) completed.Add(FinishTracker(tracker, aborted: false));
        }
        _state = (int)(_trackers.Any(t => !t.Done) ? DetectorState.InQuest : DetectorState.Idle);
        PublishView();
        return completed;
    }

    /// <summary>detect.lisp:93 snapshot-quest-defs: all matching definitions, none without a quest name.</summary>
    private List<QuestDef> SnapshotQuestDefs(Snapshot snapshot) =>
        snapshot.QuestName is null ? [] : catalog.FindAll(snapshot.QuestNumber, snapshot.Episode, snapshot.QuestName);

    /// <summary>detect.lisp:100 person-p: not an NPC, or the local player (always a person).</summary>
    public static bool PersonP(PlayerState player, Snapshot snapshot) =>
        !player.Npc || (player.Index is { } i && i == snapshot.MyIndex);

    /// <summary>
    /// detect.lisp:107 party-of: the party as run plists (:name :class :level
    /// :section-id :guild-card), only players with a class; quest NPCs dropped
    /// unless <paramref name="includeNpcs"/> (the Shifta ceiling counts them).
    /// </summary>
    public static List<PlayerState> PartyOf(Snapshot snapshot, bool includeNpcs = false) =>
        snapshot.Players.Where(p => p.Class is not null && (includeNpcs || PersonP(p, snapshot))).ToList();

    /// <summary>A party member as the run plist entry.</summary>
    private static SexpNode PartyMember(PlayerState p) => SexpNode.List(
        SexpNode.Kw("name"), StrOrNil(p.Name), SexpNode.Kw("class"), StrOrNil(p.Class),
        SexpNode.Kw("level"), Telemetry.NumOrNil(p.Level), SexpNode.Kw("section-id"), StrOrNil(p.SectionId),
        SexpNode.Kw("guild-card"), StrOrNil(p.GuildCard));

    /// <summary>
    /// detect.lisp:60 trigger-met-p. <paramref name="killedIds"/> is consulted
    /// only by (:monster-dead ID). A missing trigger never fires.
    /// </summary>
    public static bool TriggerMet(Trigger? trigger, Snapshot snapshot, IReadOnlyCollection<int>? killedIds) => trigger switch
    {
        RegisterTrigger r => snapshot.RegisterSet(r.Register),
        FloorSwitchTrigger f => snapshot.FloorSwitchSet(f.Floor, f.Switch),
        // People only: a quest NPC can stand on the field before anyone warped in.
        WarpInTrigger => snapshot.Players.Any(p => PersonP(p, snapshot) && (p.Floor ?? 0) > 0 && !p.Warping),
        MonsterDeadTrigger m => killedIds is not null && killedIds.Contains(m.MonsterId),
        _ => false,
    };

    /// <summary>
    /// detect.lisp:79 update-kill-tracking: a monster counts as dead only after it
    /// was seen alive (hp&gt;0) and is then observed at 0 hp, so an enemy that
    /// spawns at 0 hp never false-fires a clear.
    /// </summary>
    private void UpdateKillTracking(Snapshot snapshot)
    {
        foreach (var monster in snapshot.Monsters ?? [])
        {
            var hp = monster.Hp ?? 0;
            if (hp > 0)
            {
                if (!_seenAlive.Contains(monster.Id)) _seenAlive.Add(monster.Id);
            }
            else if (_seenAlive.Contains(monster.Id) && !_killedIds.Contains(monster.Id))
            {
                _killedIds.Add(monster.Id);
            }
        }
    }

    /// <summary>
    /// detect.lisp:129 update-pb-tracking: a Photon Blast is a near-full gauge
    /// (&gt;= 99) collapsing by more than 50 in one frame. Warping drops the
    /// baseline instead of comparing - the game zeroes the gauge on a Pioneer 2
    /// telepipe trip (run 1717, spec core §22 #1).
    /// </summary>
    private void UpdatePbTracking(Snapshot snapshot)
    {
        var me = snapshot.MyPlayer;
        if (me?.Pb is not { } pb) return;
        if (me.Warping)
        {
            _myPb = null;
            return;
        }
        // Single-float arithmetic, compared against the Lisp's 99.0 / 50.0 literals.
        if (_myPb is { } previous && previous >= 99.0f && previous - pb > 50.0f) _pbFlag = true;
        _myPb = pb;
    }

    /// <summary>detect.lisp:150 name-color-weight: 3 sandbox, 2 normal, 1 unrecognised, 0 none.</summary>
    public static int NameColorWeight(long? color)
    {
        var mode = PsobbTables.AccountModeOfColor(color);
        if (mode == "sandbox") return 3;
        if (mode is not null) return 2;
        return PsobbTables.NameColorKnown(color) ? 1 : 0;
    }

    /// <summary>
    /// detect.lisp:159 update-name-color-tracking: the load keeps the reading that
    /// settles most; a sandbox reading is final, white stays open to correction.
    /// </summary>
    private void UpdateNameColorTracking(Snapshot snapshot)
    {
        var kept = NameColorWeight(MyNameColor);
        if (kept >= 3) return;
        var color = snapshot.MyPlayer?.NameColor;
        if (NameColorWeight(color) > kept) MyNameColor = color;
    }

    private void Reset()
    {
        _state = (int)DetectorState.Idle;
        _activeView = [];
        _trackers.Clear();
        QuestPtr = null;
        _myPb = null;
        _pbFlag = false;
        _seenAlive.Clear();
        _killedIds.Clear();
        MyNameColor = null;
        Telemetry = null;
    }

    private Tracker StartTracker(QuestDef def, Snapshot snapshot)
    {
        // PB state and telemetry belong to the quest load: take them at the first tracker.
        if (_trackers.Count == 0)
        {
            _pbFlag = false; // pb-category-at-start-p: never PB at the start (spec core §14.4)
            _myPb = snapshot.MyPlayer?.Pb;
            Telemetry = new Telemetry(clock.Now, clock.TicksPerSecond,
                PsobbTables.MaxPartyPbShifta(PartyOf(snapshot, includeNpcs: true).Select(p => p.Class)));
        }
        var me = snapshot.MyPlayer;
        var tracker = new Tracker(
            def,
            clock.Now,
            PartyOf(snapshot).Select(PartyMember).ToList(),
            me?.SectionId,
            snapshot.QuestName,
            PsobbTables.DifficultyLabel(snapshot.Difficulty, snapshot.Anguish));
        _trackers.Add(tracker);
        return tracker;
    }

    /// <summary>detect.lisp:224 finish-tracker: the run plist (spec core §14.7); key order as the Lisp builds it.</summary>
    private Plist FinishTracker(Tracker tracker, bool aborted)
    {
        var timeMs = Math.Max(1, Elapsed(tracker));
        var telemetry = Telemetry;
        tracker.Done = true;
        var items = new List<SexpNode>
        {
            SexpNode.Kw("quest-slug"), SexpNode.Str(tracker.Def.Slug),
            SexpNode.Kw("quest-name"), StrOrNil(tracker.QuestName),
            SexpNode.Kw("episode"), Telemetry.NumOrNil(tracker.Def.Episode),
            SexpNode.Kw("time-ms"), SexpNode.Int(timeMs),
            SexpNode.Kw("party-size"), SexpNode.Int(tracker.Party.Count),
            SexpNode.Kw("pb"), SexpNode.Bool(_pbFlag),
            SexpNode.Kw("players"), Telemetry.ListOrNil(tracker.Party.ToList()),
            SexpNode.Kw("submitter-section-id"), StrOrNil(tracker.MySectionId),
            SexpNode.Kw("difficulty"), StrOrNil(tracker.Difficulty),
            SexpNode.Kw("account-mode"), StrOrNil(PsobbTables.AccountModeOfColor(MyNameColor)),
            SexpNode.Kw("my-name-color"), Telemetry.NumOrNil(MyNameColor),
            SexpNode.Kw("death-count"), telemetry is null ? SexpNode.Nil : SexpNode.Int(telemetry.DeathCount),
            SexpNode.Kw("telemetry"), telemetry is null ? SexpNode.Nil : telemetry.RunData(),
            SexpNode.Kw("finished-at"), SexpNode.Int(clock.UniversalTime),
        };
        if (aborted)
        {
            items.Add(SexpNode.Kw("aborted"));
            items.Add(SexpNode.T);
        }
        return Plist.From(new SList(items))!;
    }

    /// <summary>detect.lisp:251 abandon-trackers: aborted runs for trackers running at least <see cref="AbortMinMs"/>.</summary>
    private List<Plist> AbandonTrackers() =>
        _trackers.Where(t => !t.Done).ToList()
            .Where(t => Elapsed(t) >= AbortMinMs)
            .Select(t => FinishTracker(t, aborted: true))
            .ToList();

    private static SexpNode StrOrNil(string? s) => s is null ? SexpNode.Nil : SexpNode.Str(s);

    private sealed class Tracker(QuestDef def, long startTime, List<SexpNode> party, string? mySectionId, string? questName, string? difficulty)
    {
        public QuestDef Def { get; } = def;
        public long StartTime { get; set; } = startTime;
        public List<SexpNode> Party { get; } = party;
        public string? MySectionId { get; } = mySectionId;
        public string? QuestName { get; } = questName;
        public string? Difficulty { get; } = difficulty;
        public bool Done { get; set; }
    }

    /// <summary>Test hook mirroring tests-detect's age-trackers: move every tracker's start back.</summary>
    internal void AgeTrackers(long ms)
    {
        foreach (var t in _trackers) t.StartTime -= ms * clock.TicksPerSecond / 1000;
        PublishView();
    }
}
