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

    /// <summary>
    /// Consecutive counted failures (<see cref="UploadResult.Counted"/>: the
    /// upload itself failed mid-body, timed out, or got a 5xx) after which an
    /// upload gives up, as a rejection does (S17). The Lisp client retried
    /// forever, and a server that answers early and resets the connection
    /// never clears on its own. Counted failures back off exponentially
    /// (<see cref="CountedRetrySeconds"/>), so the twelfth comes about a day and
    /// a half in. Connect failures do not count, but an outage behind a proxy
    /// that answers 502/503 does: one longer than that gives up. The count lives on the entry
    /// (<see cref="RunKeys.UploadFailures"/>) and survives restarts; any server
    /// reply and the manual retry (<see cref="ResetUploadFailures"/>) clear it.
    /// </summary>
    public const int MaxUploadFailures = 12;

    /// <summary>The backoff cap for counted failures: 6 hours.</summary>
    public const long MaxUploadRetrySeconds = 6 * 3600;

    /// <summary>The backoff after the <paramref name="failures"/>th counted failure in a row: 300 s, doubling, capped at 6 h.</summary>
    public static long CountedRetrySeconds(long failures) =>
        failures >= 8 ? MaxUploadRetrySeconds : Math.Min(MaxUploadRetrySeconds, UploadRetrySeconds << (int)Math.Max(0, failures - 1));

    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly Func<long> _now;
    private readonly Func<string, bool> _fileExists;
    private List<RunEntry> _runs = [];
    private volatile UploadProgress? _uploadProgress;
    private volatile bool _unsaved; // the last Save failed: the file is older than memory

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
            var now = _now();
            lock (_lock)
            {
                form = new SList(_runs
                    .Where(e => RunEntries.IsActive(e.Raw, now))
                    .Select(e => RunEntries.Persistable(e.Raw).ToSexp())
                    .ToList());
            }
            try
            {
                // Not flushed to disk (S50): saved from the poll thread, which
                // must never wait on the disk.
                SexpWriter.WriteFile(Path, form, durable: false);
                _unsaved = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _unsaved = true;
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
            _runs = RunEntries.TrimFinished(_runs, e => e.Raw, MaxFinishedRuns, _now());
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
    /// Deviation (S40): the Lisp still saved then; here a gone entry raises no
    /// <see cref="Changed"/> and saves only to retry a failed earlier save.
    /// </summary>
    public RunEntry Update(RunEntry entry, IEnumerable<KeyValuePair<string, SexpNode>> updates) =>
        Change(entry, current => Apply(current, updates));

    /// <summary><see cref="Update(RunEntry, IEnumerable{KeyValuePair{string, SexpNode}})"/>, computing the new plist from the current one under the lock (read-modify-write).</summary>
    private RunEntry Change(RunEntry entry, Func<Plist, Plist> change)
    {
        RunEntry updated;
        bool found;
        lock (_lock)
        {
            var index = _runs.FindIndex(e => e.Id == entry.Id);
            found = index >= 0;
            // Build on the current version, not the caller's possibly stale copy.
            updated = new RunEntry(entry.Id, change(found ? _runs[index].Raw : entry.Raw));
            if (found)
            {
                _runs[index] = updated;
                _runs = RunEntries.TrimFinished(_runs, e => e.Raw, MaxFinishedRuns, _now());
            }
        }
        // Gone (cleared or trimmed meanwhile): nothing changed, so no Changed,
        // and no save unless the last one failed and is owed a retry (S40).
        if (found || _unsaved) Save();
        if (found) OnChanged();
        return updated;
    }

    /// <summary><see cref="Update(RunEntry, IEnumerable{KeyValuePair{string, SexpNode}})"/> with inline pairs.</summary>
    public RunEntry Update(RunEntry entry, params (string Key, SexpNode Value)[] updates) =>
        Update(entry, Pairs(updates));

    private static IEnumerable<KeyValuePair<string, SexpNode>> Pairs((string Key, SexpNode Value)[] updates) =>
        updates.Select(u => new KeyValuePair<string, SexpNode>(u.Key, u.Value));

    /// <summary>
    /// The manual retry (the Retry button, C# addition for S17): forget every
    /// counted-failure streak and its backoff, and bring back an upload the
    /// streak gave up on (still listed; a count-based give-up is the only one
    /// that carries a count). Rejections and vanished files stay given up. A
    /// given-up entry is not active, so it is not saved: it can be revived
    /// only until the client restarts. One save and one Changed for the lot.
    /// Returns the number of entries reset.
    /// </summary>
    public int ResetUploadFailures()
    {
        var reset = 0;
        lock (_lock)
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                var entry = _runs[i];
                if (!entry.Is(RunKeys.UploadFailures)) continue;
                var copy = entry.Raw.Clone();
                if ((RunEntries.Get(copy, RunKeys.UploadFailures).AsLong ?? 0) >= MaxUploadFailures)
                {
                    copy.Remove(RunKeys.UploadGivenUp);
                    copy.Remove(RunKeys.UploadError);
                }
                copy.Remove(RunKeys.UploadFailures);
                copy.Remove(RunKeys.NextUploadAt);
                _runs[i] = new RunEntry(entry.Id, copy);
                reset++;
            }
        }
        if (reset == 0) return 0;
        Save();
        OnChanged();
        return reset;
    }

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
    /// <paramref name="run"/> (matched by <see cref="RunEntries.SameRun"/>),
    /// with <c>:untrimmed t</c> when the remux failed (C#, S07).
    /// Returns the updated entry, or null when the run is no longer listed.
    /// </summary>
    public RunEntry? LinkVideoFile(Plist run, string videoPath, bool untrimmed = false)
    {
        var entry = Entries.FirstOrDefault(e => RunEntries.SameRun(run, e.Raw));
        if (entry is null) return null;
        // A clean file linked over an untrimmed one clears the mark.
        return untrimmed || entry.Is(RunKeys.Untrimmed)
            ? Update(entry, (RunKeys.VideoPath, SexpNode.Str(videoPath)), (RunKeys.Untrimmed, SexpNode.Bool(untrimmed)))
            : Update(entry, (RunKeys.VideoPath, SexpNode.Str(videoPath)));
    }

    /// <summary>
    /// upload-candidate (store.lisp:477): the oldest entry whose saved
    /// recording still needs uploading and is not backing off. Aborted and
    /// unranked runs never upload. An entry whose file vanished gives up on
    /// the spot and the scan moves on; that update raises <see cref="Changed"/>,
    /// which refreshes the runs list (the Lisp returned a repaint flag instead).
    /// </summary>
    public RunEntry? UploadCandidate(long? now = null)
    {
        var at = now ?? _now();
        var snapshot = Entries;
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            var entry = snapshot[i];
            // A held untrimmed recording (S07) the player deleted: give it up
            // too, so the row stops offering a file that is gone.
            if (RunEntries.AwaitsManualAttach(entry.Raw, at) && !(entry.VideoPath is { } held && _fileExists(held)))
            {
                Update(entry, (RunKeys.UploadGivenUp, SexpNode.T));
                continue;
            }
            if (!(RunEntries.AwaitsUpload(entry.Raw)
                  && (entry.Get(RunKeys.NextUploadAt) is var next && (next.IsNil || next.AsNumber is { } n && n <= at))))
                continue;
            if (entry.VideoPath is { } path && _fileExists(path)) return entry;
            Update(entry, (RunKeys.UploadGivenUp, SexpNode.T));
        }
        return null;
    }

    /// <summary>
    /// video-path-retention-sets (store.lisp:510): recordings the local
    /// storage sweep must never take (<c>Protected</c>: still awaiting their
    /// upload, automatic or - an untrimmed one, for 14 days - by hand) and
    /// those it reclaims first (<c>Uploaded</c>: the site holds them). A file in neither list is an orphan. Order mirrors the Lisp
    /// push (oldest entry first).
    /// </summary>
    public (IReadOnlyList<string> Protected, IReadOnlyList<string> Uploaded) VideoPathRetentionSets()
    {
        var now = _now();
        var @protected = new List<string>();
        var uploaded = new List<string>();
        foreach (var entry in Entries)
        {
            if (entry.VideoPath is not { } path) continue;
            if (entry.Is(RunKeys.VideoAttached)) uploaded.Insert(0, path);
            else if (RunEntries.AwaitsUpload(entry.Raw) || RunEntries.AwaitsManualAttach(entry.Raw, now))
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
    /// Whatever the server answered (and after a final api-error), the capture diagnostics follow
    /// (best effort). Attached/duplicate mark the entry
    /// <c>:video-attached :video-uploaded</c> with <c>:held</c>/<c>:approved</c>
    /// from the reply's status; the local file is NOT deleted (the retention
    /// sweep reclaims it). A "pending-limit" rejection backs off an hour, any
    /// other rejection gives up; an api-error backs off five minutes, and the
    /// <see cref="MaxUploadFailures"/>th one in a row gives up like a rejection.
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
                var error = (RunKeys.UploadError, SexpNode.Str(result.Message ?? ""));
                // Not the upload's own failure (offline, connect, 401): the old
                // fixed backoff, the streak left as it is, never a give-up.
                if (!result.Counted)
                    return Update(entry, (RunKeys.NextUploadAt, SexpNode.Int(_now() + UploadRetrySeconds)), error);
                var gaveUp = false;
                var updated = Change(entry, current =>
                {
                    var failures = (RunEntries.Get(current, RunKeys.UploadFailures).AsLong ?? 0) + 1;
                    gaveUp = failures >= MaxUploadFailures;
                    return Apply(current, Pairs([gaveUp
                            ? (RunKeys.UploadGivenUp, SexpNode.T)
                            : (RunKeys.NextUploadAt, SexpNode.Int(_now() + CountedRetrySeconds(failures))),
                        (RunKeys.UploadFailures, SexpNode.Int(failures)), error]));
                });
                // A final failure is worth the capture log on the server, as a rejection's is.
                if (gaveUp) await SendDiagnosticsAsync(uploader, serverId, cancellationToken).ConfigureAwait(false);
                return updated;
            }

            await SendDiagnosticsAsync(uploader, serverId, cancellationToken).ConfigureAwait(false);

            return result.Outcome switch
            {
                UploadOutcome.Attached or UploadOutcome.Duplicate => Settle(entry,
                    (RunKeys.VideoAttached, SexpNode.T),
                    (RunKeys.VideoUploaded, SexpNode.T),
                    (RunKeys.Held, SexpNode.Bool(result.Status == "held")),
                    (RunKeys.Approved, SexpNode.Bool(result.Status == "approved"))),
                _ when result.Error == "pending-limit" => Settle(entry,
                    (RunKeys.NextUploadAt, SexpNode.Int(_now() + UploadLimitRetrySeconds))),
                _ => Settle(entry,
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

    /// <summary>The capture diagnostics after an upload, best effort.</summary>
    private static async Task SendDiagnosticsAsync(IVideoUploader uploader, long serverId, CancellationToken cancellationToken)
    {
        try
        {
            await uploader.SendDiagnosticsAsync(serverId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Diagnostics must never break the upload flow.
        }
    }

    /// <summary>Update after a server reply: the reply also ends any api-error streak, so the count is removed.</summary>
    private RunEntry Settle(RunEntry entry, params (string Key, SexpNode Value)[] updates) =>
        Change(entry, current =>
        {
            var settled = Apply(current, Pairs(updates));
            settled.Remove(RunKeys.UploadFailures);
            return settled;
        });

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
