using System.Diagnostics;
using System.Text;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Ghost;

/// <summary>What the poll loop knows about a freshly identified quest load, for the fetch gate.</summary>
/// <param name="Slugs">Every category slug matching the load (find-quest-defs), primary first. Empty when the quest is unknown.</param>
/// <param name="Difficulty">difficulty-label(difficulty, anguish), e.g. "Very Hard"; null for an unknown difficulty id (the query then omits it).</param>
/// <param name="PartyMembers">The party size without NPCs (party-of).</param>
/// <param name="GhostRaceEnabled">The :ghost-race setting.</param>
/// <param name="HasSubmissionToken">submission-token is non-empty. The guest token counts: an anonymous player's PBs live under the guest account.</param>
/// <param name="AccountMode">"normal" / "sandbox" from the Detector's settled name colour for this load (the reading that also stamps the run); null when it could not be read (the query then omits it and the server leaves the mode open).</param>
public sealed record GhostLoadInfo(
    IReadOnlyList<string> Slugs,
    string? Difficulty,
    int PartyMembers,
    bool GhostRaceEnabled,
    bool HasSubmissionToken,
    string? AccountMode);

/// <summary>
/// One GET /api/quests/:slug/ghost to make (ghost-fetch-wanted's values,
/// ghost.lisp:415). <see cref="Pb"/> is always 0: a run is No PB until a
/// discharge is seen, so the PB fallback board at load time is the No-PB one;
/// an explicitly chosen target is served regardless (ghost.lisp:455).
/// </summary>
public sealed record GhostFetchRequest(IReadOnlyList<string> Slugs, string? Difficulty, int PartySize, int Pb = 0,
    string? AccountMode = null)
{
    /// <summary>
    /// The request path with its query, as fetch-ghost-splits builds it
    /// (api-client.lisp:679): <c>/api/quests/&lt;slug1&gt;/ghost?slugs=&lt;rest,...&gt;&amp;difficulty=&lt;enc&gt;&amp;party_size=&lt;n&gt;&amp;pb=0</c>.
    /// slugs= only when the load has other categories (slugify output, safe raw).
    /// </summary>
    public string PathAndQuery()
    {
        var parameters = new List<string>();
        if (Slugs.Count > 1) parameters.Add("slugs=" + string.Join(",", Slugs.Skip(1)));
        if (Difficulty is not null) parameters.Add("difficulty=" + UrlEncodeComponent(Difficulty));
        parameters.Add("party_size=" + PartySize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        parameters.Add("pb=" + Pb.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // The session's account mode (S25): a player with both a normal and a
        // sandbox account races the best of the one they are on.
        if (AccountMode is not null) parameters.Add("account_mode=" + UrlEncodeComponent(AccountMode));
        return $"/api/quests/{Slugs[0]}/ghost?{string.Join("&", parameters)}";
    }

    /// <summary>
    /// UTF-8 percent-encoding of everything but the unreserved
    /// <c>A-Za-z0-9-_.~</c>, upper-case hex (url-encode-component,
    /// api-client.lisp:666): "Very Hard" -> "Very%20Hard".
    /// </summary>
    public static string UrlEncodeComponent(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

/// <summary>The submitter's own player as the race needs it: absent :floor/:room read as 0 and :y as 0.0 (ghost.lisp:217-222).</summary>
public sealed record GhostPlayer(long Floor, long Room, float Y);

/// <summary>
/// The ghost race's cross-thread state (the Lisp specials *ghost*,
/// *ghost-race*, *live-camera*, *ghost-fetch-ptr*; ghost.lisp:78-94) and its
/// per-frame orchestration. One instance lives for the client's lifetime.
/// </summary>
/// <remarks>
/// Threading, as in Lisp: <see cref="Ghost"/> is written by the fetch task and
/// read by the poll thread; <see cref="LiveCamera"/> is written by the poll
/// thread at its full rate and read by the overlay thread; <see cref="Race"/>
/// is poll-thread only. Each is a single reference swap of an immutable value.
/// </remarks>
public sealed class GhostSession
{
    private GhostReference? _ghost;
    private CameraState? _liveCamera;
    // The quest-ptr the current fetch was started (or skipped) for; 0 = none.
    private long _fetchPtr;
    // Makes a landing fetch's "still the same load?" check and its write one
    // step against Reset (the poll thread) - else an exited game's ghost could
    // land just after the reset.
    private readonly Lock _gate = new();
    // S36: bumped on every new load and every forget, under _gate. A fetch
    // lands only while it is unchanged: the pointer alone cannot tell a later
    // quest loaded at the same address (or a relaunched game) from its own.
    private long _load = 1;

    /// <summary>The ghost fetched for the currently loaded quest, or null.</summary>
    public GhostReference? Ghost
    {
        get => Volatile.Read(ref _ghost);
        set => Volatile.Write(ref _ghost, value);
    }

    /// <summary>
    /// The freshest camera while a quest runs, or null. The in-world marker must
    /// pan with the camera, and the 4 Hz status cadence visibly lags a turn, so
    /// the overlay reads this directly (hand it to the overlay as its camera source).
    /// </summary>
    public CameraState? LiveCamera
    {
        get => Volatile.Read(ref _liveCamera);
        private set => Volatile.Write(ref _liveCamera, value);
    }

    /// <summary>Live race state while a quest runs, or null. Poll thread only.</summary>
    public GhostRace? Race { get; private set; }

    /// <summary>
    /// The fetch to start for a freshly loaded quest, or null
    /// (ghost-fetch-wanted, ghost.lisp:415). Keys off the quest POINTER, not the
    /// detector: a quest loads seconds before its start trigger fires, and those
    /// seconds hide the round trip. Consumes the pointer once the quest is
    /// identified (<paramref name="questName"/> read), so one load asks once;
    /// the previous ghost is dropped the moment a new load is seen, and the
    /// pointer is forgotten whenever no quest is loaded - a reload at the same
    /// allocation address must still refetch (the target or PB may have changed).
    /// </summary>
    /// <param name="describeLoad">Called only on a fresh identified load; builds what the gate needs.</param>
    public GhostFetchRequest? FetchWanted(long? questPtr, string? questName, Func<GhostLoadInfo> describeLoad) =>
        Wanted(questPtr, questName, describeLoad).Request;

    // FetchWanted plus the load id it adopted, taken in the same locked step.
    private (GhostFetchRequest? Request, long Load) Wanted(long? questPtr, string? questName, Func<GhostLoadInfo> describeLoad)
    {
        if (questPtr is not { } ptr || ptr <= 0)
        {
            // No quest loaded: forget the load AND the ghost, so a stale
            // reference can never race the next quest. (Completed runs are
            // annotated while the quest is still loaded.)
            ForgetLoad();
            return (null, 0);
        }
        if (questName is null || ptr == Volatile.Read(ref _fetchPtr)) return (null, 0);
        long load;
        lock (_gate)
        {
            Volatile.Write(ref _fetchPtr, ptr);
            load = ++_load;
            Ghost = null;
        }
        var info = describeLoad();
        if (info.Slugs.Count == 0 || !info.GhostRaceEnabled || !info.HasSubmissionToken) return (null, 0);
        return (new GhostFetchRequest(info.Slugs, info.Difficulty, Math.Max(1, info.PartyMembers),
            AccountMode: info.AccountMode), load);
    }

    /// <summary>
    /// Kick off the background ghost fetch when a quest just loaded
    /// (maybe-start-ghost-fetch, ghost.lisp:452). Call every poll frame, before
    /// <see cref="Step"/>. The result lands in <see cref="Ghost"/> only if the
    /// same load is still current when the reply arrives.
    /// </summary>
    /// <param name="fetch">GET the ghost: the raw JSON body on 200, null on 404
    /// (nothing to race); throws on auth / transport errors (swallowed here).</param>
    /// <returns>The started task (tests await it), or null when nothing was started.</returns>
    public Task? MaybeStartFetch(long? questPtr, string? questName, Func<GhostLoadInfo> describeLoad,
        Func<GhostFetchRequest, Task<string?>> fetch)
    {
        var (request, load) = Wanted(questPtr, questName, describeLoad);
        if (request is null) return null;
        return Task.Run(async () =>
        {
            GhostReference? ghost;
            try
            {
                ghost = GhostReference.Parse(await fetch(request).ConfigureAwait(false));
            }
            catch (Exception)
            {
                ghost = null;
            }
            lock (_gate)
            {
                if (_load == load) Ghost = ghost;
            }
        });
    }

    /// <summary>
    /// One poll-loop step (ghost-race-step, ghost.lisp:198): track the room
    /// progression from the moment the detector enters a quest, attach the
    /// fetched ghost once it arrives (the cursor alignment tolerates a late
    /// join), and drop everything when the quest ends. Safe every frame.
    /// </summary>
    /// <param name="inQuest">detector state is :in-quest.</param>
    /// <param name="camera">This frame's camera (null when not read: marker off, or unreadable).</param>
    /// <param name="me">The submitter's player, null when not found.</param>
    /// <param name="elapsedMs">telemetry-elapsed-ms now: the quest telemetry's clock (first tracker's
    /// start), the base the reference's room events were stamped on. Null without telemetry.</param>
    /// <param name="questHasSlug">Is this category slug one of the loaded quest's defs? Guards a stale
    /// ghost from racing the wrong quest; matches DEFS, not trackers, so a segment whose start
    /// trigger has not fired yet still belongs (ghost-matches-snapshot-p, ghost.lisp:188).</param>
    public void Step(bool inQuest, CameraState? camera, GhostPlayer? me, long? elapsedMs, Func<string?, bool> questHasSlug)
    {
        if (!inQuest)
        {
            Race = null;
            LiveCamera = null;
            return;
        }
        var race = Race ??= new GhostRace();
        var ghost = Ghost;
        LiveCamera = camera;
        if (ghost is not null && !ReferenceEquals(race.Ghost, ghost) && questHasSlug(ghost.QuestSlug))
            race.Ghost = ghost;
        if (me is not null && elapsedMs is { } elapsed)
        {
            race.NoteRoom(me.Floor, me.Room, elapsed);
            race.NotePosition(me.Floor, me.Y);
        }
    }

    /// <summary>
    /// The game exited (C#, S37; the Lisp kept all of it until the next
    /// attach's first frame): forget the load, the ghost, the race and the
    /// camera, as an unloaded quest and a non-quest frame do. Poll thread only.
    /// </summary>
    public void Reset()
    {
        ForgetLoad();
        Race = null;
        LiveCamera = null;
    }

    // No load: a fetch still in flight lands nowhere (it checks under the same lock).
    private void ForgetLoad()
    {
        lock (_gate)
        {
            // Once per unload, not every lobby frame.
            if (Volatile.Read(ref _fetchPtr) == 0 && Ghost is null) return;
            Volatile.Write(ref _fetchPtr, 0);
            _load++;
            Ghost = null;
        }
    }

    /// <summary>The overlay's ghost snapshot now (see <see cref="GhostOverlayData.From"/>), or null.</summary>
    public GhostOverlayData? OverlayData(long elapsedMs, bool marker) =>
        Race is { } race ? GhostOverlayData.From(race, elapsedMs, marker, Stopwatch.GetTimestamp()) : null;

    /// <summary>Stamp completed runs against the current ghost (see <see cref="GhostFinish.AnnotateRuns"/>).</summary>
    public IReadOnlyList<Plist> AnnotateRuns(IReadOnlyList<Plist> runs) => GhostFinish.AnnotateRuns(Ghost, runs);

    /// <summary>" | vs 2:03.456 -3.2s" for the quest-status line, or null (ghost-status-suffix).</summary>
    public string? StatusSuffix() => GhostFormat.StatusSuffix(Race);

    /// <summary>" -3.2s" for the window title, or null (ghost-title-suffix).</summary>
    public string? TitleSuffix() => GhostFormat.TitleSuffix(Race);
}
