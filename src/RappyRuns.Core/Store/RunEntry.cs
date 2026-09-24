using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Store;

/// <summary>
/// Keyword names used on run queue entries (spec core §9.1). The detector's
/// own run keys (§14.7) ride along untouched; these are the ones the store
/// reads or writes.
/// </summary>
public static class RunKeys
{
    public const string Status = "STATUS";
    public const string QuestSlug = "QUEST-SLUG";
    public const string QuestName = "QUEST-NAME";
    public const string TimeMs = "TIME-MS";
    public const string FinishedAt = "FINISHED-AT";
    public const string Telemetry = "TELEMETRY";
    public const string Aborted = "ABORTED";
    public const string Unranked = "UNRANKED";
    public const string RunPrivate = "RUN-PRIVATE";
    public const string Url = "URL";
    public const string ServerId = "SERVER-ID";
    public const string Reason = "REASON";
    public const string StandingRank = "STANDING-RANK";
    public const string StandingParties = "STANDING-PARTIES";
    public const string StandingPrevMs = "STANDING-PREV-MS";
    public const string StandingDeltaMs = "STANDING-DELTA-MS";
    public const string GhostDeltaMs = "GHOST-DELTA-MS";
    public const string GhostTimeMs = "GHOST-TIME-MS";
    public const string GhostLabel = "GHOST-LABEL";
    public const string VideoPath = "VIDEO-PATH";
    public const string VideoOffsetMs = "VIDEO-OFFSET-MS";
    public const string VideoAttached = "VIDEO-ATTACHED";
    public const string VideoUploaded = "VIDEO-UPLOADED";
    public const string VideoUrl = "VIDEO-URL";
    public const string Held = "HELD";
    public const string Approved = "APPROVED";
    public const string NextUploadAt = "NEXT-UPLOAD-AT";
    public const string UploadGivenUp = "UPLOAD-GIVEN-UP";
    public const string UploadError = "UPLOAD-ERROR";

    /// <summary>Consecutive api-error upload attempts (C# addition, S17; the Lisp client ignores it).</summary>
    public const string UploadFailures = "UPLOAD-FAILURES";
}

/// <summary>The <c>:status</c> keyword names.</summary>
public static class RunStatus
{
    public const string Queued = "QUEUED";
    public const string Submitted = "SUBMITTED";
    public const string Duplicate = "DUPLICATE";
    public const string Rejected = "REJECTED";
    public const string Failed = "FAILED";
}

/// <summary>
/// One run queue entry: the detector's run plist plus the status keys, kept
/// as a <see cref="Plist"/> so queue.sexp stays Lisp-compatible and unknown
/// keys survive. Immutable: an update produces a new entry with the same
/// <see cref="Id"/>. The id is in-memory only (never written to disk) and is
/// what updates match on - the Lisp client matched by EQ and silently lost
/// updates made from a stale copy (store.lisp:344, spec core §9.3).
/// </summary>
public sealed class RunEntry
{
    private readonly Plist _data;

    internal RunEntry(Guid id, Plist data)
    {
        Id = id;
        _data = data;
    }

    /// <summary>Stable in-memory identity across updates.</summary>
    public Guid Id { get; }

    /// <summary>A copy of the entry's plist (mutating it does not touch the queue).</summary>
    public Plist Data => _data.Clone();

    internal Plist Raw => _data;

    /// <summary>getf: the value, or NIL when absent.</summary>
    public SexpNode Get(string key) => _data.Get(key) ?? SexpNode.Nil;

    /// <summary>Lisp truthiness of the key's value (absent = NIL = false).</summary>
    public bool Is(string key) => Get(key).IsTrue;

    /// <summary>The <c>:status</c> keyword name (see <see cref="RunStatus"/>), or null.</summary>
    public string? Status => Get(RunKeys.Status).KeywordName;

    public string? QuestSlug => Get(RunKeys.QuestSlug).AsString;

    public string? QuestName => Get(RunKeys.QuestName).AsString;

    public long? TimeMs => Get(RunKeys.TimeMs).AsLong;

    public long? ServerId => Get(RunKeys.ServerId).AsLong;

    public string? Url => Get(RunKeys.Url).AsString;

    public string? VideoPath => Get(RunKeys.VideoPath).AsString;

    public override string ToString() => $"{Id} {SexpWriter.Write(_data.ToSexp())}";
}

/// <summary>
/// The pure entry predicates and transforms from store.lisp, over plain
/// plists so they apply equally to detector runs and queue entries.
/// </summary>
public static class RunEntries
{
    /// <summary>getf with NIL for absent keys.</summary>
    public static SexpNode Get(Plist entry, string key) => entry.Get(key) ?? SexpNode.Nil;

    /// <summary>Lisp truthiness of a key.</summary>
    public static bool Is(Plist entry, string key) => Get(entry, key).IsTrue;

    /// <summary>The status keyword name, or null.</summary>
    public static string? Status(Plist entry) => Get(entry, RunKeys.Status).KeywordName;

    /// <summary>
    /// entry-active-p (store.lisp:25): unfinished business that must survive
    /// trimming and restarts - entries awaiting (re)submission, and entries
    /// whose saved video still needs attaching to their server draft. Aborted
    /// and unranked runs never upload, and a permanently rejected upload is as
    /// finished as a rejected run, so none of those keep an entry active.
    /// </summary>
    public static bool IsActive(Plist entry) =>
        IsUnsent(entry)
        || (Is(entry, RunKeys.VideoPath)
            && Is(entry, RunKeys.ServerId)
            && !Is(entry, RunKeys.Aborted)
            && !Is(entry, RunKeys.Unranked)
            && !Is(entry, RunKeys.VideoAttached)
            && !Is(entry, RunKeys.UploadGivenUp));

    /// <summary>
    /// entry-unsent-p (store.lisp:303): :queued or :failed - runs that exist
    /// nowhere but this client, so clearing one loses it for good.
    /// </summary>
    public static bool IsUnsent(Plist entry) => Status(entry) is RunStatus.Queued or RunStatus.Failed;

    /// <summary>
    /// trim-finished-runs (store.lisp:43): <paramref name="runs"/> is newest
    /// first; keep every active entry and only the newest
    /// <paramref name="limit"/> finished ones, preserving order.
    /// </summary>
    public static List<T> TrimFinished<T>(IEnumerable<T> runs, Func<T, Plist> plistOf, int limit)
    {
        var finished = 0;
        var kept = new List<T>();
        foreach (var run in runs)
        {
            if (!IsActive(plistOf(run)) && ++finished > limit) continue;
            kept.Add(run);
        }
        return kept;
    }

    /// <summary>trim-finished-runs over bare plists.</summary>
    public static List<Plist> TrimFinished(IEnumerable<Plist> runs, int limit) => TrimFinished(runs, p => p, limit);

    /// <summary>
    /// persistable-entry (store.lisp:283): the entry as written to disk.
    /// Anything but :queued/:failed was already submitted, so its (large)
    /// telemetry payload is dropped. Returns the same instance when unchanged.
    /// </summary>
    public static Plist Persistable(Plist entry)
    {
        if (IsUnsent(entry)) return entry;
        var copy = entry.Clone();
        copy.Remove(RunKeys.Telemetry);
        return copy;
    }

    /// <summary>
    /// same-run-p (store.lisp:432): the natural key (quest slug, time, finish
    /// time) that links a recording to its queue entry.
    /// </summary>
    public static bool SameRun(Plist a, Plist b) =>
        LispEqual(Get(a, RunKeys.QuestSlug), Get(b, RunKeys.QuestSlug))
        && LispEqual(Get(a, RunKeys.TimeMs), Get(b, RunKeys.TimeMs))
        && LispEqual(Get(a, RunKeys.FinishedAt), Get(b, RunKeys.FinishedAt));

    /// <summary>
    /// hosted-video-replaceable-p (store.lisp:446): the server holds the
    /// auto-uploaded copy and no external URL was attached, so the player may
    /// still swap in their own YouTube/Twitch link before approval.
    /// </summary>
    public static bool HostedVideoReplaceable(Plist entry) =>
        Is(entry, RunKeys.VideoUploaded) && !Is(entry, RunKeys.VideoUrl);

    /// <summary>
    /// apply-tracking-mode (store.lisp:317): stamp a completed detector run
    /// with tracking-only mode's flags before it is enqueued - <c>:unranked</c>,
    /// and <c>:run-private</c> when the private sub-setting is on. Read once at
    /// enqueue time so a later settings change never re-flags a queued run.
    /// Aborted runs (already private drafts) and tracking-off runs come back
    /// as the very same instance.
    /// </summary>
    public static Plist ApplyTrackingMode(Plist run, bool trackingOnly, bool trackingPrivate)
    {
        if (!trackingOnly || Is(run, RunKeys.Aborted)) return run;
        var stamped = run.Clone();
        // Set pushes new keys to the front: set :run-private first so the
        // result reads (:unranked t :run-private t . run) as in Lisp.
        if (trackingPrivate) stamped.Set(RunKeys.RunPrivate, SexpNode.T);
        stamped.Set(RunKeys.Unranked, SexpNode.T);
        return stamped;
    }

    // equal / eql for the datum kinds a natural key holds (strings,
    // integers, NIL). Both spellings of NIL compare equal.
    internal static bool LispEqual(SexpNode a, SexpNode b) =>
        a.IsNil ? b.IsNil : !b.IsNil && a.Equals(b);
}
