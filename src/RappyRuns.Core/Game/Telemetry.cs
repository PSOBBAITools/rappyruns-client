using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Game;

/// <summary>
/// Per-quest telemetry (telemetry.lisp), mirroring psostats consolidateFrame:
/// one frame row per second plus running totals. Pure like the detector: it
/// consumes snapshots. <b>Parity</b> (spec core §15): the ordering, the getf
/// defaults, single-float rounding and every alive/dead rule follow the Lisp
/// line by line, and <see cref="RunData"/> produces the same plist the Lisp
/// telemetry-run-data does, because it is persisted in queue.sexp and
/// encoded into the run JSON.
/// </summary>
public sealed class Telemetry
{
    /// <summary>telemetry.lisp:14 +frame-keys+: column names of a frame row, in order.</summary>
    public static IReadOnlyList<string> FrameKeys { get; } =
    [
        "t", "hp", "tp", "pb", "meseta", "floor", "room", "x", "z",
        "shifta", "deband", "inv", "state", "monsters", "kills",
        "map_var", "ft", "dt", "ct", "damage", "weapon", "player_locs", "monster_locs",
        "map",
    ];

    private static readonly int[] AttackStates = [5, 6, 7];
    private const int CastingState = 8;
    private const long Frame1ThresholdMs = 60;
    private static readonly long[] Frame1ExcludedUnitxt = [34, 45, 73, 68];
    private const long TrackIntervalMs = 250;
    private const int MaxTrackPoints = 86400;

    private readonly long _ticksPerSecond;

    // Lists below are kept oldest first; the Lisp pushes and reverses, which
    // yields the same order in the run data.
    private readonly List<SexpNode> _frames = [];
    private long _lastFrameSecond = -1;
    private int? _lastHp;
    private readonly List<SexpNode> _events = [];
    private int? _lastMap;
    private (int Floor, int Room)? _lastFloorRoom;
    private readonly List<SexpNode> _track = [];
    private int _trackCount;
    private long? _lastTrackMs;
    private long? _lastMeseta;
    private IReadOnlyList<(string Key, int Count)>? _lastConsumables;
    private readonly OrderedCounts<string> _itemsUsed = new();
    private (int Damage, int Freeze, int Confuse)? _lastTraps;
    private long _trapsDt, _trapsFt, _trapsCt;
    private long _tpUsed;
    private int? _lastTp;
    private readonly OrderedCounts<string> _techsCast = new();
    private int? _lastState;
    private readonly OrderedCounts<long> _timeByState = new();
    private readonly List<WeaponEntry> _weapons = [];
    private string _currentWeaponId = "Bare Handed";
    private readonly Dictionary<int, MonsterTrack> _monsters = [];
    private readonly List<MonsterTrack> _monsterOrder = []; // hash-table insertion order (SBCL maphash)
    private int _monstersAlive;
    private long _monsterHpPool;
    private readonly List<long> _monsterHpPoolSeries = [];
    private readonly OrderedCounts<long> _playerDamage = new();
    private readonly OrderedCounts<long> _lastHits = new();
    private readonly List<BossTrack> _bosses = [];
    private IReadOnlyList<MonsterState>? _lastMonsterSample;
    private bool _illegalShifta;
    private bool _fastWarps;

    public Telemetry(long startTime, long ticksPerSecond, int? maxPartyPbShifta = null)
    {
        StartTime = startTime;
        _ticksPerSecond = ticksPerSecond;
        MaxPartyPbShifta = maxPartyPbShifta;
    }

    /// <summary>Clock ticks of the quest start (the first tracker's start).</summary>
    public long StartTime { get; }

    public long? LastTick { get; private set; }

    public int DeathCount { get; private set; }

    public long Kills { get; private set; }

    public long MesetaCharged { get; private set; }

    /// <summary>Highest legitimate Shifta level for the party, null = unknown.</summary>
    public int? MaxPartyPbShifta { get; }

    public int FrameCount => _frames.Count;

    /// <summary>
    /// telemetry.lisp:398 telemetry-step: feed one frame. Cheap bookkeeping always
    /// runs; monsters/inventory when the snapshot carries them; one data frame per second.
    /// </summary>
    public void Step(Snapshot snapshot, long now)
    {
        var me = snapshot.MyPlayer;
        var elapsedMs = LispMath.ElapsedMs(now, StartTime, _ticksPerSecond);
        var second = LispMath.FloorDiv(elapsedMs, 1000);
        var deltaMs = LastTick is { } last ? Math.Max(0, LispMath.ElapsedMs(now, last, _ticksPerSecond)) : 0;
        LastTick = now;
        if (snapshot.Monsters is { Count: > 0 } monsters) UpdateMonsterTracking(monsters, elapsedMs);
        // Anyone still warping while fast burst is on (psostats FastWarps). Quest NPCs are not anyone.
        if (snapshot.FastBurst && snapshot.Players.Any(p => p.Warping && !p.Npc)) _fastWarps = true;
        if (me is null) return;
        if (MaxPartyPbShifta is { } limit && (me.Shifta ?? 0) > limit) _illegalShifta = true;
        UpdateStateTracking(me, deltaMs);
        UpdateDeathTracking(me, second);
        UpdateRoomTracking(me, second, elapsedMs);
        UpdateTrackRecording(me, snapshot, elapsedMs);
        UpdateMapTracking(snapshot, second);
        UpdateResourceTracking(me);
        if (snapshot.Inventory is { } inventory) UpdateInventoryTracking(inventory);
        if (second > _lastFrameSecond) PushFrame(me, snapshot, second);
    }

    private WeaponEntry WeaponEntryFor(string id, string? display = null, string? type = null)
    {
        var entry = _weapons.Find(w => w.Id == id);
        if (entry is null)
        {
            entry = new WeaponEntry(id, display ?? id, type ?? "weapon");
            _weapons.Add(entry);
        }
        return entry;
    }

    private void UpdateStateTracking(PlayerState me, long deltaMs)
    {
        if (me.State is not { } state) return;
        var previous = _lastState;
        _timeByState.Bump(state, deltaMs);
        var weapon = WeaponEntryFor(_currentWeaponId);
        if (AttackStates.Contains(state) && !(previous is { } p && AttackStates.Contains(p))) weapon.Attacks++;
        if (state == CastingState && previous != CastingState)
        {
            weapon.Techs++;
            if (PsobbTables.TechName(me.CurrentTech) is { } tech) _techsCast.Bump(tech, 1);
        }
        _lastState = state;
    }

    private void UpdateDeathTracking(PlayerState me, long second)
    {
        if (me.Hp is not { } hp) return;
        if (_lastHp is > 0 && hp == 0)
        {
            DeathCount++;
            _events.Add(Event(second, "death"));
        }
        _lastHp = hp;
    }

    private void UpdateRoomTracking(PlayerState me, long second, long elapsedMs)
    {
        var key = (me.Floor ?? 0, me.Room ?? 0);
        if (_lastFloorRoom == key) return;
        _lastFloorRoom = key;
        _events.Add(Event(second, "room", key.Item1, key.Item2, elapsedMs));
    }

    private void UpdateTrackRecording(PlayerState me, Snapshot snapshot, long elapsedMs)
    {
        if (_trackCount >= MaxTrackPoints) return;
        if (_lastTrackMs is { } last && elapsedMs - last < TrackIntervalMs) return;
        _lastTrackMs = elapsedMs;
        _trackCount++;
        _track.Add(SexpNode.List(
            SexpNode.Int(elapsedMs), SexpNode.Int(me.Floor ?? 0), SexpNode.Int(snapshot.Map ?? 0),
            Single(LispMath.Round1(me.X ?? 0f)), Single(LispMath.Round1(me.Z ?? 0f)), Single(LispMath.Round1(me.Y ?? 0f))));
    }

    private void UpdateMapTracking(Snapshot snapshot, long second)
    {
        if (snapshot.Map is not { } map) return;
        if (_lastMap is { } previous && previous != map)
            _events.Add(SexpNode.List(K("t"), SexpNode.Int(second), K("type"), SexpNode.Str("floor"), K("floor"), SexpNode.Int(map)));
        _lastMap = map;
    }

    private void UpdateResourceTracking(PlayerState me)
    {
        // Traps and TP only ever decrease through use (refills happen on Pioneer 2).
        if (me.DamageTraps is { } dt && me.FreezeTraps is { } ft && me.ConfuseTraps is { } ct)
        {
            if (_lastTraps is { } old)
            {
                if (old.Damage > dt) _trapsDt += old.Damage - dt;
                if (old.Freeze > ft) _trapsFt += old.Freeze - ft;
                if (old.Confuse > ct) _trapsCt += old.Confuse - ct;
            }
            _lastTraps = (dt, ft, ct);
        }
        if (me.Tp is { } tp)
        {
            if (_lastTp is { } previousTp && previousTp > tp) _tpUsed += previousTp - tp;
            _lastTp = tp;
        }
        // Meseta spent in the field = charged (Pioneer 2 shops are floor 0), following psostats.
        if (me.Meseta is { } meseta)
        {
            if (_lastMeseta is { } previousMeseta && previousMeseta > meseta && (me.Floor ?? 0) > 0)
                MesetaCharged += previousMeseta - meseta;
            _lastMeseta = meseta;
        }
    }

    private void UpdateInventoryTracking(Inventory inventory)
    {
        var consumables = inventory.Consumables;
        // An empty previous plist is NIL in Lisp: nothing to compare against.
        if (_lastConsumables is { Count: > 0 } previous)
        {
            foreach (var (key, value) in consumables)
            {
                // A drop of exactly one is a use; larger drops are drops/bank moves (psostats).
                var old = previous.FirstOrDefault(c => c.Key == key);
                if (old.Key is not null && value == old.Count - 1) _itemsUsed.Bump(key, 1);
            }
        }
        _lastConsumables = consumables;
        // Equipped gear accrues one second per sample (~1/sec).
        foreach (var item in inventory.Equipment) WeaponEntryFor(item.Id, item.Display, item.Type).Seconds++;
        if (inventory.Weapon is null) WeaponEntryFor("Bare Handed", "Bare Handed", "weapon").Seconds++;
        _currentWeaponId = inventory.Weapon?.Id ?? "Bare Handed";
    }

    private void UpdateBossTracking(MonsterTrack state, long second)
    {
        var name = PsobbTables.BossName(state.Unitxt, state.Index);
        if (name is null) return;
        var boss = _bosses.Find(b => b.Id == state.Id);
        if (boss is null)
        {
            // A later form of the same boss gets a (n) suffix.
            var form = _bosses.Count(b => b.Unitxt == state.Unitxt);
            if (form > 0) name = $"{name} ({form})";
            boss = new BossTrack(state.Id, name, state.Unitxt, second);
            _bosses.Add(boss);
        }
        if (state.KilledMs is { } killedMs && boss.KilledT is null) boss.KilledT = LispMath.FloorDiv(killedMs, 1000);
    }

    /// <summary>
    /// telemetry.lisp:273 update-monster-tracking. A monster first seen at 0 hp
    /// still spawns alive, so its death registers one sample later - a kill is
    /// an alive→dead transition (deliberately not the detector's rule, spec §22 #8).
    /// </summary>
    private void UpdateMonsterTracking(IReadOnlyList<MonsterState> monsters, long elapsedMs)
    {
        var second = LispMath.FloorDiv(elapsedMs, 1000);
        var alive = 0;
        long pool = 0;
        foreach (var monster in monsters)
        {
            var hp = monster.Hp ?? 0;
            var attacker = (long)(monster.LastAttacker ?? 0);
            if (hp > 0) alive++;
            pool += hp;
            if (!_monsters.TryGetValue(monster.Id, out var state))
            {
                state = new MonsterTrack(monster.Id, monster.Unitxt, monster.Index, monster.Name, hp, elapsedMs);
                _monsters[monster.Id] = state;
                _monsterOrder.Add(state);
            }
            else if (state.Alive && hp == 0)
            {
                state.Alive = false;
                state.KilledMs = elapsedMs;
                Kills++;
                if (!(state.Unitxt is { } u && Frame1ExcludedUnitxt.Contains(u)))
                    state.Frame1 = elapsedMs - state.SpawnMs < Frame1ThresholdMs;
                _lastHits.Bump(attacker, 1);
                // The killing blow is credited with the remaining hp.
                if (state.Hp > 0) _playerDamage.Bump(attacker, state.Hp);
                state.Hp = 0;
            }
            else if (state.Alive)
            {
                if (hp < state.Hp) _playerDamage.Bump(attacker, state.Hp - hp);
                state.Hp = hp;
            }
            UpdateBossTracking(state, second);
        }
        _monstersAlive = alive;
        _monsterHpPool = pool;
        _lastMonsterSample = monsters;
    }

    /// <summary>telemetry.lisp:329 player-location-key: guild card, else name, else "".</summary>
    private static string PlayerLocationKey(PlayerState p) => p.GuildCard ?? p.Name ?? "";

    private void PushFrame(PlayerState me, Snapshot snapshot, long second)
    {
        var playerLocs = snapshot.Players.Select(p => (SexpNode)SexpNode.List(
            SexpNode.Str(PlayerLocationKey(p)), SexpNode.Int(p.Floor ?? 0), SexpNode.Int(p.Room ?? 0),
            Single(LispMath.Round1(p.X ?? 0f)), Single(LispMath.Round1(p.Y ?? 0f)), Single(LispMath.Round1(p.Z ?? 0f)),
            SexpNode.Int(p.Facing ?? 0), SexpNode.Int(p.Warping ? 1 : 0))).ToList();
        var monsterLocs = (_lastMonsterSample ?? []).Where(m => (m.Hp ?? 0) > 0).Select(m => (SexpNode)SexpNode.List(
            SexpNode.Int(m.Id), Single(LispMath.Round1(m.X ?? 0f)), Single(LispMath.Round1(m.Y ?? 0f)),
            Single(LispMath.Round1(m.Z ?? 0f)), SexpNode.Int(m.Facing ?? 0), SexpNode.Int(m.Hp ?? 0),
            SexpNode.Int(m.Frozen ? 1 : 0), SexpNode.Int(m.Paralyzed ? 1 : 0), SexpNode.Int(m.Confused ? 1 : 0))).ToList();
        var myDamage = snapshot.MyIndex is { } my ? _playerDamage.Get(my) ?? 0 : 0;
        _frames.Add(SexpNode.List(
            SexpNode.Int(second),
            SexpNode.Int(me.Hp ?? 0),
            SexpNode.Int(me.Tp ?? 0),
            SexpNode.Int(LispMath.Round(me.Pb ?? 0f)),
            SexpNode.Int(MesetaCharged),
            SexpNode.Int(me.Floor ?? 0),
            SexpNode.Int(me.Room ?? 0),
            Single(LispMath.Round1(me.X ?? 0f)),
            Single(LispMath.Round1(me.Z ?? 0f)),
            SexpNode.Int(me.Shifta ?? 0),
            SexpNode.Int(me.Deband ?? 0),
            SexpNode.Int(me.Invincible ? 1 : 0),
            SexpNode.Int(me.State ?? 0),
            SexpNode.Int(_monstersAlive),
            SexpNode.Int(Kills),
            SexpNode.Int(snapshot.MapVariation ?? 0),
            SexpNode.Int(me.FreezeTraps ?? 0),
            SexpNode.Int(me.DamageTraps ?? 0),
            SexpNode.Int(me.ConfuseTraps ?? 0),
            SexpNode.Int(myDamage),
            SexpNode.Str(_currentWeaponId),
            ListOrNil(playerLocs),
            ListOrNil(monsterLocs),
            SexpNode.Int(snapshot.Map ?? 0)));
        // Boss HP histories and the monster HP pool advance with the frames.
        foreach (var boss in _bosses) boss.Hp.Add(_monsters.TryGetValue(boss.Id, out var s) ? s.Hp : 0);
        _monsterHpPoolSeries.Add(_monsterHpPool);
        _lastFrameSecond = second;
    }

    /// <summary>
    /// telemetry.lisp:469 telemetry-run-data: the accumulated telemetry as the
    /// printable plist the run queue stores - taken when a tracker finishes, so
    /// segment runs carry the totals of their own end time.
    /// </summary>
    public SexpNode RunData()
    {
        var items = new List<SexpNode>
        {
            K("frames"), ListOrNil(_frames.ToList()),
            K("events"), ListOrNil(_events.ToList()),
            K("track"), ListOrNil(_track.ToList()),
            K("death-count"), SexpNode.Int(DeathCount),
            K("meseta-charged"), SexpNode.Int(MesetaCharged),
            K("kills"), SexpNode.Int(Kills),
            K("tp-used"), SexpNode.Int(_tpUsed),
            K("traps-used"), SexpNode.List(K("dt"), SexpNode.Int(_trapsDt), K("ft"), SexpNode.Int(_trapsFt), K("ct"), SexpNode.Int(_trapsCt)),
            K("items-used"), _itemsUsed.ToAlist(k => SexpNode.Kw(k)),
            K("techs-cast"), _techsCast.ToAlist(SexpNode.Str),
            K("time-by-state"), _timeByState.ToAlist(SexpNode.Int),
            K("weapons"), ListOrNil(_weapons.Select(w => (SexpNode)SexpNode.List(
                K("id"), SexpNode.Str(w.Id), K("display"), SexpNode.Str(w.Display), K("type"), SexpNode.Kw(w.Type),
                K("seconds"), SexpNode.Int(w.Seconds), K("attacks"), SexpNode.Int(w.Attacks), K("techs"), SexpNode.Int(w.Techs))).ToList()),
            K("monsters"), ListOrNil(MonsterRunData()),
            K("bosses"), ListOrNil(_bosses.Select(b => (SexpNode)SexpNode.List(
                K("name"), SexpNode.Str(b.Name), K("id"), SexpNode.Int(b.Id), K("unitxt"), NumOrNil(b.Unitxt),
                K("spawn-t"), SexpNode.Int(b.SpawnT), K("killed-t"), NumOrNil(b.KilledT),
                K("hp"), ListOrNil(b.Hp.Select(h => (SexpNode)SexpNode.Int(h)).ToList()))).ToList()),
            K("player-damage"), _playerDamage.ToAlist(SexpNode.Int),
            K("last-hits"), _lastHits.ToAlist(SexpNode.Int),
            K("monster-hp-pool"), ListOrNil(_monsterHpPoolSeries.Select(v => (SexpNode)SexpNode.Int(v)).ToList()),
            K("max-party-pb-shifta"), NumOrNil(MaxPartyPbShifta),
            K("illegal-shifta"), SexpNode.Bool(_illegalShifta),
            K("fast-warps"), SexpNode.Bool(_fastWarps),
        };
        return new SList(items);
    }

    /// <summary>
    /// telemetry.lisp:438 monster-run-data: maphash + push + sort by spawn time.
    /// SBCL iterates an EQL table in insertion order and sorts lists stably, so
    /// ties on spawn_ms come out in reverse first-seen order; the golden pins it.
    /// </summary>
    private List<SexpNode> MonsterRunData()
    {
        var reversed = Enumerable.Reverse(_monsterOrder).ToList();
        return reversed.OrderBy(m => m.SpawnMs).Select(m => (SexpNode)SexpNode.List(
            K("id"), SexpNode.Int(m.Id), K("unitxt"), NumOrNil(m.Unitxt), K("name"), m.Name is null ? SexpNode.Nil : SexpNode.Str(m.Name),
            K("spawn-ms"), SexpNode.Int(m.SpawnMs), K("killed-ms"), NumOrNil(m.KilledMs), K("frame1"), SexpNode.Bool(m.Frame1))).ToList();
    }

    private static SexpNode Event(long second, string type) =>
        SexpNode.List(K("t"), SexpNode.Int(second), K("type"), SexpNode.Str(type));

    private static SexpNode Event(long second, string type, int floor, int room, long ms) =>
        SexpNode.List(K("t"), SexpNode.Int(second), K("type"), SexpNode.Str(type), K("floor"), SexpNode.Int(floor),
            K("room"), SexpNode.Int(room), K("ms"), SexpNode.Int(ms));

    internal static SKeyword K(string name) => SexpNode.Kw(name);

    internal static SFloat Single(float value) => new(value);

    internal static SexpNode NumOrNil(long? value) => value is { } v ? SexpNode.Int(v) : SexpNode.Nil;

    internal static SexpNode ListOrNil(List<SexpNode> items) => items.Count == 0 ? SexpNode.Nil : new SList(items);

    private sealed class WeaponEntry(string id, string display, string type)
    {
        public string Id { get; } = id;
        public string Display { get; } = display;
        public string Type { get; } = type;
        public long Seconds { get; set; }
        public long Attacks { get; set; }
        public long Techs { get; set; }
    }

    private sealed class MonsterTrack(int id, long? unitxt, int? index, string? name, int hp, long spawnMs)
    {
        public int Id { get; } = id;
        public long? Unitxt { get; } = unitxt;
        public int? Index { get; } = index;
        public string? Name { get; } = name;
        public int Hp { get; set; } = hp;
        public bool Alive { get; set; } = true;
        public long SpawnMs { get; } = spawnMs;
        public long? KilledMs { get; set; }
        public bool Frame1 { get; set; }
    }

    private sealed class BossTrack(int id, string name, long? unitxt, long spawnT)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public long? Unitxt { get; } = unitxt;
        public long SpawnT { get; } = spawnT;
        public long? KilledT { get; set; }
        public List<long> Hp { get; } = [];
    }
}

/// <summary>
/// A Lisp alist of counts built with bump-alist (telemetry.lisp:85): new keys
/// are consed on the front, and copy-alist-cells reverses, so the run data
/// lists keys in first-bumped order - which is what this keeps.
/// </summary>
internal sealed class OrderedCounts<TKey> where TKey : notnull
{
    private readonly List<TKey> _order = [];
    private readonly Dictionary<TKey, long> _counts = [];

    public void Bump(TKey key, long delta)
    {
        if (_counts.TryGetValue(key, out var v)) _counts[key] = v + delta;
        else
        {
            _counts[key] = delta;
            _order.Add(key);
        }
    }

    public long? Get(TKey key) => _counts.TryGetValue(key, out var v) ? v : null;

    public SexpNode ToAlist(Func<TKey, SexpNode> keyNode) =>
        _order.Count == 0
            ? SexpNode.Nil
            : new SList(_order.Select(k => (SexpNode)new SList([keyNode(k)], SexpNode.Int(_counts[k]))).ToList());
}
