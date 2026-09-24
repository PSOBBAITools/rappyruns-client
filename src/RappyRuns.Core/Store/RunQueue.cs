using RappyRuns.Core.Config;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Store;

/// <summary>
/// The completed-run queue (store.lisp, spec core §9): holds detector output
/// until it is submitted, persists unsubmitted runs and pending video uploads
/// across restarts in queue.sexp, and drives resubmission and uploads.
/// <para>
/// queue.sexp keeps the Lisp client's exact format (a list of plists, newest
/// first, active entries only), so a downgrade to the Lisp client picks up the
/// same queue. Server-side duplicate detection (submitter x quest x time_ms)
/// makes any double submission harmless.
/// </para>
/// <para>
/// Thread-safe. Entries are immutable snapshots with a stable in-memory
/// <see cref="RunEntry.Id"/>; updates find their entry by id, never by
/// reference. Every mutation saves the file (atomically) and then raises
/// <see cref="Changed"/> on the mutating thread.
/// </para>
/// </summary>
public sealed class RunQueue
{
    /// <summary>+max-finished-runs+: finished entries kept in the list (active ones are never dropped).</summary>
    public const int MaxFinishedRuns = 50;

    /// <summary>+upload-retry-seconds+: backoff after a transport failure.</summary>
    public const long UploadRetrySeconds = 300;

    /// <summary>
    /// +upload-limit-retry-seconds+: backoff while the server's pending-review
    /// limit is full - only a moderator decision frees a slot.
    /// </summary>
    public const long UploadLimitRetrySeconds = 3600;

    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly Func<long> _now;
    private readonly Func<string, bool> _fileExists;
    private List<RunEntry> _runs = [];
    private volatile UploadProgress? _uploadProgress;

    /// <param name="path">queue.sexp (<see cref="ConfigStore.QueuePath"/> in production).</param>
    /// <param name="universalNow">The clock, as CL universal time; defaults to <see cref="UniversalTime.Now"/>.</param>
    /// <param name="fileExists">Recording existence check for <see cref="UploadCandidate"/>; defaults to File.Exists.</param>
    public RunQueue(string path, Func<long>? universalNow = null, Func<string, bool>? fileExists = null)
    {
        Path = path;
        _now = universalNow ?? UniversalTime.Now;
        _fileExists = fileExists ?? File.Exists;
    }

    public string Path { get; }

    /// <summary>Raised after any change to the entries (for the runs list to refresh).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when an upload's whole percent moves, or the upload ends (for the Video column).</summary>
    public event EventHandler? UploadProgressChanged;

    /// <summary>
    /// Raised when writing queue.sexp fails. The in-memory queue stays
    /// correct and the next change retries the write (the Lisp client let
    /// the error unwind into its caller instead).
    /// </summary>
    public event EventHandler<Exception>? SaveFailed;

    /// <summary>queued-runs: a snapshot of every entry, newest first.</summary>
    public IReadOnlyList<RunEntry> Entries
    {
        get
        {
            lock (_lock) return _runs.ToList();
        }
    }

    /// <summary>The current version of the entry with <paramref name="id"/>, or null once it left the list.</summary>
    public RunEntry? Find(Guid id)
    {
        lock (_lock) return _runs.Find(e => e.Id == id);
    }

    /// <summary>
    /// load-queue!: append the saved entries after whatever is in memory. A
    /// missing or unreadable file loads nothing; items that are not keyword
    /// plists are skipped.
    /// </summary>
    public void Load()
    {
        var saved = SexpReader.TryReadFile(Path);
        if (saved is null || !(saved.IsNil || saved is SList { Tail: null })) return;
        var loaded = saved.Elements
            .Select(Plist.From)
            .Where(p => p is not null)
            .Select(p => new RunEntry(Guid.NewGuid(), p!))
            .ToList();
        lock (_lock) _runs = [.. _runs, .. loaded];
        OnChanged();
    }

    /// <summary>
    /// save-queue!: write the active entries (newest first), each through
    /// <see cref="RunEntries.Persistable"/>. An empty queue writes NIL.
    /// Saves are serialized and each snapshots the latest state, so the file
    /// never ends up older than memory.
    /// </summary>
    public void Save()
    {
        lock (_saveLock)
        {
            SexpNode form;
            lock (_lock)
            {
                form = new SList(_runs
                    .Where(e => RunEntries.IsActive(e.Raw))
                    .Select(e => RunEntries.Persistable(e.Raw).ToSexp())
                    .ToList());
            }
            try
            {
                SexpWriter.WriteFile(Path, form);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SaveFailed?.Invoke(this, e);
            }
        }
    }

    /// <summary>
    /// enqueue-run!: push <c>(:status :queued . run)</c> to the front, trim the
    /// finished entries, save. The run plist is copied, so later changes to
    /// the caller's instance do not leak in (spec core §22 #45: add
    /// <c>:video-offset-ms</c> before enqueuing).
    /// </summary>
    public RunEntry Enqueue(Plist run)
    {
        var data = run.Clone();
        data.Set(RunKeys.Status, SexpNode.Kw(RunStatus.Queued));
        var entry = new RunEntry(Guid.NewGuid(), data);
        lock (_lock)
        {
            _runs.Insert(0, entry);
            _runs = RunEntries.TrimFinished(_runs, e => e.Raw, MaxFinishedRuns);
        }
        Save();
        OnChanged();
        return entry;
    }

    /// <summary>
    /// update-run! (store.lisp:344): a copy of <paramref name="entry"/> with
    /// <paramref name="updates"/> set in order (new keys go to the front, as
    /// with <c>(setf getf)</c>), minus its telemetry once the status is
    /// submitted/duplicate/rejected - the megabytes of frames live on the
    /// server then (spec core §22 #22). Replaces the entry with the same id,
    /// trims, saves and returns the copy. When the entry already left the
    /// list the copy is still returned, as in Lisp, but nothing is stored.
    /// </summary>
    public RunEntry Update(RunEntry entry, IEnumerable<KeyValuePair<string, SexpNode>> updates)
    {
        RunEntry updated;
        lock (_lock)
        {
            // Build on the current version, not the caller's possibly stale copy.
            var current = _runs.Find(e => e.Id == entry.Id) ?? entry;
            updated = new RunEntry(entry.Id, Apply(current.Raw, updates));
            var index = _runs.FindIndex(e => e.Id == entry.Id);
            if (index >= 0)
            {
                _runs[index] = updated;
                _runs = RunEntries.TrimFinished(_runs, e => e.Raw, MaxFinishedRuns);
            }
        }
        Save();
        OnChanged();
        return updated;
    }

    /// <summary><see cref="Update(RunEntry, IEnumerable{KeyValuePair{string, SexpNode}})"/> with inline pairs.</summary>
    public RunEntry Update(RunEntry entry, params (string Key, SexpNode Value)[] updates) =>
        Update(entry, updates.Select(u => new KeyValuePair<string, SexpNode>(u.Key, u.Value)));

    /// <summary>
    /// clear-runs!: drop every entry except unsent ones (:queued/:failed).
    /// Cleared drafts stay on the server and their recordings on disk; only
    /// this client's link between the two is forgotten. Returns the number removed.
    /// </summary>
    public int Clear()
    {
        int removed;
        lock (_lock)
        {
            var kept = _runs.Where(e => RunEntries.IsUnsent(e.Raw)).ToList();
            removed = _runs.Count - kept.Count;
            _runs = kept;
        }
        Save();
        OnChanged();
        return removed;
    }

    /// <summary>
    /// link-video-file!: record <paramref name="videoPath"/> on the entry for
    /// <paramref name="run"/> (matched by <see cref="RunEntries.SameRun"/>).
    /// Returns the updated entry, or null when the run is no longer listed.
    /// </summary>
    public RunEntry? LinkVideoFile(Plist run, string videoPath, bool untrimmed = false)
    {
        var entry = Entries.FirstOrDefault(e => RunEntries.SameRun(run, e.Raw));
        if (entry is null) return null;
        return untrimmed
            ? Update(entry, (RunKeys.VideoPath, SexpNode.Str(videoPath)), (RunKeys.Untrimmed, SexpNode.T))
            : Update(entry, (RunKeys.VideoPath, SexpNode.Str(videoPath)));
    }

    /// <summary>
    /// upload-candidate (store.lisp:477): the oldest entry whose saved
    /// recording still needs uploading and is not backing off. Aborted and
    /// unranked runs never upload. An entry whose file vanished gives up on
    /// the spot and the scan moves on; <c>GaveUp</c> then tells the caller the
    /// runs list needs a refresh (the <see cref="Changed"/> event fires too).
    /// </summary>
    public (RunEntry? Candidate, bool GaveUp) UploadCandidate(long? now = null)
    {
        var at = now ?? _now();
        var gaveUp = false;
        var snapshot = Entries;
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            var entry = snapshot[i];
            if (!(RunEntries.AwaitsUpload(entry.Raw)
                  && (entry.Get(RunKeys.NextUploadAt) is var next && (next.IsNil || next.AsNumber is { } n && n <= at))))
                continue;
            if (entry.VideoPath is { } path && _fileExists(path)) return (entry, gaveUp);
            Update(entry, (RunKeys.UploadGivenUp, SexpNode.T));
            gaveUp = true;
        }
        return (null, gaveUp);
    }

    /// <summary>
    /// video-path-retention-sets (store.lisp:510): recordings the local
    /// storage sweep must never take (<c>Protected</c>: still awaiting their
    /// upload, an untrimmed one's by hand included) and those it reclaims first (<c>Uploaded</c>: the site holds
    /// them). A file in neither list is an orphan. Order mirrors the Lisp
    /// push (oldest entry first).
    /// </summary>
    public (IReadOnlyList<string> Protected, IReadOnlyList<string> Uploaded) VideoPathRetentionSets()
    {
        var @protected = new List<string>();
        var uploaded = new List<string>();
        foreach (var entry in Entries)
        {
            if (entry.VideoPath is not { } path) continue;
            if (entry.Is(RunKeys.VideoAttached)) uploaded.Insert(0, path);
            else if (RunEntries.AwaitsUpload(entry.Raw, orByHand: true))
                @protected.Insert(0, path);
        }
        return (@protected, uploaded);
    }

    /// <summary>The in-flight upload's progress, or null (the Lisp <c>*upload-progress*</c>).</summary>
    public UploadProgress? CurrentUpload => _uploadProgress;

    /// <summary>upload-progress-percent: whole percent of <paramref name="entry"/>'s in-flight upload, or null.</summary>
    public int? UploadProgressPercent(Plist entry)
    {
        var progress = _uploadProgress;
        return progress is not null && RunEntries.Get(entry, RunKeys.ServerId).AsLong == progress.ServerId
            ? progress.Percent
            : null;
    }

    /// <summary>
    /// ensure-submission-token (store.lisp:66): the token submissions go out
    /// with, registering a fresh anonymous guest first when the client has
    /// neither a linked account nor a guest (then saving it as
    /// <c>:anon-token</c>). Null when no token can be had - any failure just
    /// means "not now" and the entries stay :queued (spec core §22 #30).
    /// </summary>
    public static async Task<string?> EnsureSubmissionTokenAsync(ConfigStore config, IAnonymousRegistrar registrar,
        string? machineName = null, CancellationToken cancellationToken = default)
    {
        var token = config.SubmissionToken;
        if (token.Length > 0) return token;
        try
        {
            var fresh = await registrar.RegisterAnonymousAsync(
                Submission.AnonymousClientLabel(machineName ?? Submission.SafeMachineName()), cancellationToken).ConfigureAwait(false);
            config.AnonToken = fresh;
            config.Save();
            return fresh;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// submit-queued! (store.lisp:409): submit every :queued/:failed entry in
    /// list order (newest first) and return the UPDATED entries. Null when no
    /// token can be had (offline first run): nothing is sent and entries stay
    /// queued. There is no periodic retry; the app calls this when a run
    /// completes, on "Submit pending runs", and after a successful token
    /// check (spec core §9.4).
    /// </summary>
    public async Task<IReadOnlyList<RunEntry>?> SubmitQueuedAsync(ConfigStore config, IAnonymousRegistrar registrar,
        IRunSubmitter submitter, string? machineName = null, CancellationToken cancellationToken = default)
    {
        var token = await EnsureSubmissionTokenAsync(config, registrar, machineName, cancellationToken).ConfigureAwait(false);
        if (token is null) return null;
        var pending = Entries.Where(e => RunEntries.IsUnsent(e.Raw)).ToList();
        var results = new List<RunEntry>(pending.Count);
        foreach (var entry in pending)
            results.Add(await SubmitEntryAsync(entry, token, submitter, cancellationToken).ConfigureAwait(false));
        return results;
    }

    /// <summary>submit-entry!: submit one entry and store the outcome (<see cref="Submission.Updates"/>).</summary>
    public async Task<RunEntry> SubmitEntryAsync(RunEntry entry, string token, IRunSubmitter submitter,
        CancellationToken cancellationToken = default)
    {
        var result = await submitter.SubmitAsync(entry.Data, token, cancellationToken).ConfigureAwait(false);
        return Update(entry, Submission.Updates(result));
    }

    /// <summary>
    /// upload-entry-video! (store.lisp:552): upload the entry's recording to
    /// its server draft, tracking <see cref="CurrentUpload"/> meanwhile.
    /// Whatever the server answered, the capture diagnostics follow
    /// (best effort). Attached/duplicate mark the entry
    /// <c>:video-attached :video-uploaded</c> with <c>:held</c>/<c>:approved</c>
    /// from the reply's status; the local file is NOT deleted (the retention
    /// sweep reclaims it). A "pending-limit" rejection backs off an hour, any
    /// other rejection gives up; an api-error backs off five minutes.
    /// Run it on a worker thread, one upload at a time (spec core §9.5).
    /// </summary>
    public async Task<RunEntry> UploadEntryVideoAsync(RunEntry entry, IVideoUploader uploader,
        CancellationToken cancellationToken = default)
    {
        var serverId = entry.ServerId ?? throw new ArgumentException("entry has no server id", nameof(entry));
        var path = entry.VideoPath ?? throw new ArgumentException("entry has no video path", nameof(entry));
        var lastPercent = -1;
        try
        {
            var result = await uploader.UploadVideoAsync(serverId, path, entry.Get(RunKeys.VideoOffsetMs).AsLong,
                (done, total) =>
                {
                    var progress = new UploadProgress(serverId, done, total);
                    _uploadProgress = progress;
                    // Repaint only when the whole percent moves, not every megabyte.
                    if (progress.Percent != lastPercent)
                    {
                        lastPercent = progress.Percent;
                        UploadProgressChanged?.Invoke(this, EventArgs.Empty);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Outcome == UploadOutcome.ApiError)
            {
                return Update(entry,
                    (RunKeys.NextUploadAt, SexpNode.Int(_now() + UploadRetrySeconds)),
                    (RunKeys.UploadError, SexpNode.Str(result.Message ?? "")));
            }

            try
            {
                await uploader.SendDiagnosticsAsync(serverId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Diagnostics must never break the upload flow.
            }

            return result.Outcome switch
            {
                UploadOutcome.Attached or UploadOutcome.Duplicate => Update(entry,
                    (RunKeys.VideoAttached, SexpNode.T),
                    (RunKeys.VideoUploaded, SexpNode.T),
                    (RunKeys.Held, SexpNode.Bool(result.Status == "held")),
                    (RunKeys.Approved, SexpNode.Bool(result.Status == "approved"))),
                _ when result.Error == "pending-limit" => Update(entry,
                    (RunKeys.NextUploadAt, SexpNode.Int(_now() + UploadLimitRetrySeconds))),
                _ => Update(entry,
                    (RunKeys.UploadGivenUp, SexpNode.T),
                    (RunKeys.UploadError, SexpNode.Str(result.Message ?? result.Error ?? "rejected"))),
            };
        }
        finally
        {
            _uploadProgress = null;
            UploadProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Replace the whole list (tests: the Lisp with-test-store). Does not save.</summary>
    public void ResetForTests(IEnumerable<Plist> runs)
    {
        lock (_lock) _runs = runs.Select(p => new RunEntry(Guid.NewGuid(), p.Clone())).ToList();
    }

    private static Plist Apply(Plist entry, IEnumerable<KeyValuePair<string, SexpNode>> updates)
    {
        var copy = entry.Clone();
        foreach (var (key, value) in updates) copy.Set(key, value);
        if (RunEntries.Status(copy) is RunStatus.Submitted or RunStatus.Duplicate or RunStatus.Rejected)
            copy.Remove(RunKeys.Telemetry);
        return copy;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
