using System.Text.Json;
using RappyRuns.Core.Game;
using RappyRuns.Core.I18n;

namespace RappyRuns.Core.PinShare;

/// <summary>
/// The quest facts the pin-set fetch needs from one game snapshot.
/// <see cref="QuestPtr"/> 0 means no quest is loaded; the rest are what the
/// quest-definition lookup matches on.
/// </summary>
public sealed record PinShareQuest(long QuestPtr, string? QuestName, int? QuestNumber = null, int? Episode = null);

/// <summary>
/// One pin-set fetch's answer: the set's payload and ETag on 200, no payload
/// on 404 (none chosen, or the set went private), <see cref="NotModified"/>
/// on 304 (the set is still the one the ETag names).
/// </summary>
public sealed record PinSetResponse(JsonElement? Payload, string? ETag = null, bool NotModified = false);

/// <summary>
/// The pin set chosen on the site for the loaded quest: when to fetch it,
/// and what is current (Lisp <c>*pinshare-pin-set*</c>,
/// <c>*pinshare-quest-slugs*</c>, <c>*pinshare-set-fetch-ptr*</c>,
/// <c>pinshare-set-fetch-wanted</c> pinshare.lisp:663 and
/// <c>maybe-start-pin-set-fetch</c> pinshare-win32.lisp:500). Fetched when
/// a quest loads - the same pointer rule as the ghost - and re-asked every
/// <see cref="RefreshInterval"/> while it stays loaded, so a set edited or
/// chosen on the site shows up in-game within seconds (the ETag makes an
/// unchanged set a bodiless 304). Thread-safe: the poll loop feeds
/// snapshots, the fetch lands on a pool thread, the relay and the UI read.
/// </summary>
public sealed class PinSetTracker
{
    /// <summary>How often the loaded quest's set is re-asked.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>The wait after a failed fetch (offline, token revoked) - not a request every few seconds that is bound to fail.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(60);

    private readonly Func<PinShareQuest, IReadOnlyList<string>> _resolveSlugs;
    private readonly Func<bool> _fetchAllowed;
    private readonly Func<string, IReadOnlyList<string>, string?, CancellationToken, Task<PinSetResponse>>? _fetch;
    private readonly Action<string>? _log;
    private readonly Func<long> _nowMs;
    private readonly object _lock = new();
    private PinSet? _current;
    private IReadOnlyList<string>? _questSlugs;
    private readonly QuestLoadIdentity _quest = new(); // S36/S46: which load is current; under _lock
    // Re-ask state, under _lock: the drawn set's ETag, when the next re-ask
    // is due, and the load a fetch is in flight for (0 = none).
    private string? _etag;
    private long _nextFetchMs;
    private long _inFlightLoad;

    /// <param name="resolveSlugs">Every category slug matching the quest, primary first (Lisp <c>find-quest-defs</c> → <c>quest-def-slug</c>).</param>
    /// <param name="fetchAllowed">
    /// <c>:pinshare-enabled</c> and the rollout verdict and a linked token -
    /// choosing a set needs a linked account; an anonymous guest has none.
    /// </param>
    /// <param name="fetch">
    /// <c>GET /api/quests/&lt;slug&gt;/pins?slugs=&lt;rest,...&gt;</c> with the
    /// linked token and the ETag to send as If-None-Match (null when no set
    /// is drawn): see <see cref="PinSetResponse"/>; throws on auth and
    /// transport failures.
    /// </param>
    /// <param name="nowMs">A monotonic clock in ms; tests pass a fake.</param>
    public PinSetTracker(
        Func<PinShareQuest, IReadOnlyList<string>> resolveSlugs,
        Func<bool> fetchAllowed,
        Func<string, IReadOnlyList<string>, string?, CancellationToken, Task<PinSetResponse>>? fetch = null,
        Action<string>? log = null,
        Func<long>? nowMs = null)
    {
        _resolveSlugs = resolveSlugs;
        _fetchAllowed = fetchAllowed;
        _fetch = fetch;
        _log = log;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
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

    /// <summary>The quest pointer of the current load; null while none is loaded.</summary>
    public long? FetchPtr
    {
        get { lock (_lock) return _quest.Ptr; }
    }

    /// <summary>
    /// The current quest load's number (C#, S36). A fetch lands only while it
    /// is unchanged: the quest pointer alone cannot tell a later quest loaded
    /// at the same address (or a relaunched game) from the one the fetch was for.
    /// </summary>
    public long LoadId
    {
        get { lock (_lock) return _quest.Load; }
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
            switch (_quest.Observe(snapshot.QuestPtr, snapshot.QuestName))
            {
                case QuestLoadChange.Unchanged:
                    return (null, 0);
                case QuestLoadChange.Unloaded:
                    ForgetOwned();
                    return (null, 0);
                case QuestLoadChange.NewQuest:
                    // A new quest's slugs replace the previous quest's below;
                    // until then there are none.
                    _current = null;
                    _questSlugs = null;
                    break;
                case QuestLoadChange.Refetched:
                    _current = null; // the slugs stay: same quest
                    break;
            }
            load = _quest.Load;
        }
        var slugs = _resolveSlugs(snapshot);
        var list = slugs.Count > 0 ? slugs : null;
        lock (_lock)
        {
            // A Reset / newer load meanwhile owns the slugs now.
            if (!_quest.IsCurrent(load)) return (null, 0);
            _questSlugs = list;
        }
        return (list is not null && _fetchAllowed() ? list : null, load);
    }

    /// <summary>
    /// Feeds one poll snapshot; when a quest just loaded, fetches its set in
    /// the background, and while it stays loaded re-asks every
    /// <see cref="RefreshInterval"/>. A result lands only if that quest load
    /// is still current; the relay picks it up on its next tick. Returns the
    /// fetch task (for tests), or null when none started.
    /// </summary>
    public Task? OnSnapshot(PinShareQuest? snapshot, CancellationToken cancellationToken = default)
    {
        var (slugs, load) = Wanted(snapshot);
        if (_fetch is null) return null;
        if (slugs is not null)
        {
            lock (_lock)
            {
                if (!_quest.IsCurrent(load)) return null;
                _etag = null;
                _inFlightLoad = load;
            }
            return Fetch(slugs, load, null, revalidating: false, cancellationToken);
        }
        // A failed read (null snapshot) changes nothing, as in Wanted.
        if (snapshot is null || snapshot.QuestPtr == 0 || !ReaskDue() || !_fetchAllowed()) return null;
        string? etag;
        lock (_lock)
        {
            if (!ReaskDue()) return null; // a Reset or another fetch got in between
            slugs = _questSlugs!;
            load = _quest.Load;
            etag = _etag;
            _inFlightLoad = load;
        }
        return Fetch(slugs, load, etag, revalidating: true, cancellationToken);
    }

    // A quest with slugs is loaded, nothing is in flight and the interval passed.
    private bool ReaskDue()
    {
        lock (_lock) return _questSlugs is not null && _quest.Ptr is not null && _inFlightLoad == 0 && _nowMs() >= _nextFetchMs;
    }

    private Task Fetch(IReadOnlyList<string> slugs, long load, string? etag, bool revalidating,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            PinSetResponse? response = null;
            try
            {
                response = await _fetch!(slugs[0], [.. slugs.Skip(1)], etag, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log?.Invoke($"pin set fetch failed: {e.Message}");
            }
            Landed(load, revalidating, response);
        }, CancellationToken.None);

    // A failed first fetch draws nothing (as before); a failed re-ask keeps
    // what is drawn. Either way the next try waits FailureBackoff. A re-ask
    // that finds the set unchanged - a 304, or the same body from a server
    // that sends no ETag - leaves the drawn PinSet alone, so the relay does
    // not redraw it.
    private void Landed(long load, bool revalidating, PinSetResponse? response)
    {
        PinSet? set;
        lock (_lock)
        {
            if (_inFlightLoad == load) _inFlightLoad = 0;
            if (!_quest.IsCurrent(load)) return;
            _nextFetchMs = _nowMs() + (long)(response is null ? FailureBackoff : RefreshInterval).TotalMilliseconds;
            if (revalidating && (response is null or { NotModified: true })) return;
            set = PinSet.FromFetch(response?.Payload);
            if (revalidating && set?.Payload.GetRawText() == _current?.Payload.GetRawText()) return;
            _etag = set is null ? null : response!.ETag;
        }
        if (!Land(load, set) || !revalidating) return;
        if (set is null) _log?.Invoke("pin share: the pin set is no longer chosen on the site");
    }

    /// <summary>Adopts a fetched set when <paramref name="load"/> (<see cref="LoadId"/> at the fetch) is still the current load; true when adopted.</summary>
    public bool Land(long load, PinSet? set)
    {
        lock (_lock)
        {
            if (!_quest.IsCurrent(load)) return false;
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
        lock (_lock)
        {
            _quest.Forget();
            ForgetOwned();
        }
    }

    // What a forgotten load took with it. Callers hold _lock.
    private void ForgetOwned()
    {
        _questSlugs = null;
        _current = null;
        _etag = null;
    }

    /// <summary>Ask again for the loaded quest's set on the next snapshot (after an overwrite, so the locked copy shows the new pins).</summary>
    public void Refetch()
    {
        lock (_lock) _quest.Refetch(); // the pre-overwrite reply still in flight is stale
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
