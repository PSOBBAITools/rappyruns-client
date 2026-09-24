using System.Text.Json;
using RappyRuns.Core.I18n;

namespace RappyRuns.Core.PinShare;

/// <summary>
/// The quest facts the pin-set fetch needs from one game snapshot.
/// <see cref="QuestPtr"/> 0 means no quest is loaded; the rest are what the
/// quest-definition lookup matches on.
/// </summary>
public sealed record PinShareQuest(long QuestPtr, string? QuestName, int? QuestNumber = null, int? Episode = null);

/// <summary>
/// The pin set chosen on the site for the loaded quest: when to fetch it,
/// and what is current (Lisp <c>*pinshare-pin-set*</c>,
/// <c>*pinshare-quest-slugs*</c>, <c>*pinshare-set-fetch-ptr*</c>,
/// <c>pinshare-set-fetch-wanted</c> pinshare.lisp:663 and
/// <c>maybe-start-pin-set-fetch</c> pinshare-win32.lisp:500). Fetched once
/// per quest load - the same pointer rule as the ghost. Thread-safe: the
/// poll loop feeds snapshots, the fetch lands on a pool thread, the relay
/// and the UI read.
/// </summary>
public sealed class PinSetTracker
{
    private readonly Func<PinShareQuest, IReadOnlyList<string>> _resolveSlugs;
    private readonly Func<bool> _fetchAllowed;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<JsonElement?>>? _fetch;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private PinSet? _current;
    private IReadOnlyList<string>? _questSlugs;
    private long? _fetchPtr;
    private long _load = 1; // S36: bumped per quest load, Refetch and Reset; 0 = none

    /// <param name="resolveSlugs">Every category slug matching the quest, primary first (Lisp <c>find-quest-defs</c> → <c>quest-def-slug</c>).</param>
    /// <param name="fetchAllowed">
    /// <c>:pinshare-enabled</c> and the rollout verdict and a linked token -
    /// choosing a set needs a linked account; an anonymous guest has none.
    /// </param>
    /// <param name="fetch">
    /// <c>GET /api/quests/&lt;slug&gt;/pins?slugs=&lt;rest,...&gt;</c> with the
    /// linked token: the payload on 200, null on 404 (none chosen, or the set
    /// went private); throws on auth and transport failures.
    /// </param>
    public PinSetTracker(
        Func<PinShareQuest, IReadOnlyList<string>> resolveSlugs,
        Func<bool> fetchAllowed,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<JsonElement?>>? fetch = null,
        Action<string>? log = null)
    {
        _resolveSlugs = resolveSlugs;
        _fetchAllowed = fetchAllowed;
        _fetch = fetch;
        _log = log;
    }

    /// <summary>The set to draw for the loaded quest, or null.</summary>
    public PinSet? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Every category slug of the loaded quest, primary first; null while no quest is loaded. What Save files a new set under.</summary>
    public IReadOnlyList<string>? QuestSlugs
    {
        get { lock (_lock) return _questSlugs; }
    }

    /// <summary>The quest pointer the fetch last ran for; null forces a fetch on the next snapshot.</summary>
    public long? FetchPtr
    {
        get { lock (_lock) return _fetchPtr; }
    }

    /// <summary>
    /// The current quest load's number (C#, S36). A fetch lands only while it
    /// is unchanged: the quest pointer alone cannot tell a later quest loaded
    /// at the same address (or a relaunched game) from the one the fetch was for.
    /// </summary>
    public long LoadId
    {
        get { lock (_lock) return _load; }
    }

    /// <summary>
    /// The slugs to fetch a pin set for when <paramref name="snapshot"/>'s
    /// quest has just loaded, else null (pinshare.lisp:663). A null snapshot
    /// is a failed read (normal for a frame at warps) and changes nothing -
    /// taking it for "no quest" would drop and refetch the set on every warp.
    /// No quest loaded forgets the set, the slugs and the load, so a quest's
    /// pins never show up in the next one.
    /// </summary>
    public IReadOnlyList<string>? FetchWanted(PinShareQuest? snapshot) => Wanted(snapshot).Slugs;

    // FetchWanted plus the load it started, read in the same step.
    private (IReadOnlyList<string>? Slugs, long Load) Wanted(PinShareQuest? snapshot)
    {
        if (snapshot is null) return (null, 0);
        long load;
        lock (_lock)
        {
            if (snapshot.QuestPtr <= 0)
            {
                ForgetLoad();
                return (null, 0);
            }
            if (snapshot.QuestName is null || snapshot.QuestPtr == _fetchPtr) return (null, 0);
            _fetchPtr = snapshot.QuestPtr;
            _current = null;
            _questSlugs = null; // the previous quest's, until this load's resolve
            load = ++_load;
        }
        var slugs = _resolveSlugs(snapshot);
        var list = slugs.Count > 0 ? slugs : null;
        lock (_lock)
        {
            // A Reset / newer load meanwhile owns the slugs now.
            if (load != _load) return (null, 0);
            _questSlugs = list;
        }
        return (list is not null && _fetchAllowed() ? list : null, load);
    }

    /// <summary>
    /// Feeds one poll snapshot; when a quest just loaded, fetches its set in
    /// the background. The result lands only if that quest load is still
    /// current; the relay picks it up on its next tick. Returns the fetch
    /// task (for tests), or null when none started.
    /// </summary>
    public Task? OnSnapshot(PinShareQuest? snapshot, CancellationToken cancellationToken = default)
    {
        var (slugs, load) = Wanted(snapshot);
        if (slugs is null || _fetch is null) return null;
        return Task.Run(async () =>
        {
            PinSet? set = null;
            try
            {
                set = PinSet.FromFetch(await _fetch(slugs[0], [.. slugs.Skip(1)], cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                _log?.Invoke($"pin set fetch failed: {e.Message}");
            }
            Land(load, set);
        }, CancellationToken.None);
    }

    /// <summary>Adopts a fetched set when <paramref name="load"/> (<see cref="LoadId"/> at the fetch) is still the current load; true when adopted.</summary>
    public bool Land(long load, PinSet? set)
    {
        lock (_lock)
        {
            if (load != _load) return false;
            _current = set;
        }
        if (set is not null) _log?.Invoke($"pin share: drawing pin set \"{set.DisplayName}\"");
        return true;
    }

    /// <summary>
    /// The game exited (C#, S37): forget the set, the slugs and the load, as
    /// an unloaded quest does, so the Settings line stops naming a set for a
    /// game that is gone and a fetch still in flight lands nowhere.
    /// </summary>
    public void Reset()
    {
        lock (_lock) ForgetLoad();
    }

    // Callers hold _lock.
    private void ForgetLoad()
    {
        // Once per unload, not every lobby frame.
        if (_fetchPtr is null && _current is null && _questSlugs is null) return;
        _load++;
        _fetchPtr = null;
        _questSlugs = null;
        _current = null;
    }

    /// <summary>Ask again for the loaded quest's set on the next snapshot (after an overwrite, so the locked copy shows the new pins).</summary>
    public void Refetch()
    {
        lock (_lock)
        {
            _fetchPtr = null;
            _load++; // the pre-overwrite reply still in flight is stale
        }
    }

    /// <summary>The Settings line naming the set drawn for the loaded quest (gui.lisp:1229).</summary>
    public string PinSetText(Language language)
    {
        PinSet? set;
        IReadOnlyList<string>? slugs;
        lock (_lock)
        {
            set = _current;
            slugs = _questSlugs;
        }
        if (set is not null) return Strings.Default.Tr(language, "pinshare-pin-set-active", set.DisplayName, set.Author);
        return Strings.Default.Tr(language, slugs is not null ? "pinshare-pin-set-none" : "pinshare-pin-set-none-idle");
    }
}
