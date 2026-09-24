using System.Diagnostics;
using RappyRuns.Core.Config;
using RappyRuns.Core.Game;
using RappyRuns.Core.Ghost;
using RappyRuns.Core.Media;
using RappyRuns.Core.PinShare;
using RappyRuns.Core.Sexp;
using RappyRuns.Core.Store;
using RappyRuns.Core.Update;

namespace RappyRuns.Host;

/// <summary>Finds and attaches to the game (RappyRuns.Win.Game.PsobbConnector in production).</summary>
public interface IGameConnector
{
    /// <summary>A trusted, attached reader, or null (not running, or refused - see <see cref="Rejection"/>).</summary>
    IProcessMemoryReader? TryAttach();

    /// <summary>The current Authenticode refusal for the status line, or null.</summary>
    PsobbRejection? Rejection { get; }
}

/// <summary>What the 1 Hz (searching) / 4 Hz (attached) status tick knows (update-game-status's inputs).</summary>
public sealed record PollTick(
    bool Attached,
    Snapshot? Snapshot,
    bool ReadFailing,
    PsobbRejection? Rejection);

/// <summary>Everything the poll loop drives, injected so tests can run it without a game, a network or ffmpeg.</summary>
public sealed class PollLoopServices
{
    public required ConfigStore Config { get; init; }
    public required IGameClock Clock { get; init; }
    public required IGameConnector Connector { get; init; }
    public required GameFrameProcessor Frames { get; init; }
    public required QuestCatalog Catalog { get; init; }
    public required Recorder Recorder { get; init; }
    public required RunQueue Queue { get; init; }
    public required IAnonymousRegistrar Registrar { get; init; }
    public required IRunSubmitter Submitter { get; init; }
    public required IVideoUploader Uploader { get; init; }
    public required GhostSession Ghost { get; init; }

    /// <summary>GET the ghost for a fresh quest load: the raw body on 200, null on 404; throws otherwise.</summary>
    public Func<GhostFetchRequest, Task<string?>> GhostFetch { get; init; } = _ => Task.FromResult<string?>(null);

    /// <summary>The pin set for the loaded quest (Pin Share); null when not wired.</summary>
    public PinSetTracker? PinSets { get; init; }

    /// <summary>Close the trigger log when the loop ends (the frame processor writes it).</summary>
    public TriggerLog? TriggerLog { get; init; }

    /// <summary>The verified game's exe for the Pin Share relay (null on detach).</summary>
    public Action<string?> SetGameExe { get; init; } = _ => { };

    /// <summary>The recording settings in effect (the recorder's own source; gates the gdigrab probe).</summary>
    public Func<RecordingSettings>? RecordingSettings { get; init; }

    /// <summary>gdigrab window-capture probe at the 4 Hz slot (window title).</summary>
    public Action<string?> MaybeStartGdigrabProbe { get; init; } = _ => { };

    /// <summary>
    /// Stale-recording cleanup at start and the retention sweep. Off for a
    /// developer copy (recording forced off, or an isolated config): its queue
    /// knows nothing of the installed client's recordings in the shared folder,
    /// so both would delete that client's files.
    /// </summary>
    public bool ManageRecordingsFolder { get; init; } = true;

    /// <summary>The self-updater's busy deferral; null = no updater.</summary>
    public DeferredUpdate? Deferred { get; init; }

    /// <summary>Apply an update deferred past a run (called on the poll thread; must not join it).</summary>
    public Action<DeferredUpdate.ReadyUpdate> ApplyDeferredUpdate { get; init; } = _ => { };

    /// <summary>A run's celebration balloon (rank/PB/ghost).</summary>
    public Action<RunToast> Toast { get; init; } = _ => { };

    /// <summary>The status tick: game/quest lines, window title, overlay, Pin Share lines, rooms.</summary>
    public Action<PollTick> Tick { get; init; } = _ => { };

    /// <summary>The recording log (account-mode lines, hook failures).</summary>
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>For the anonymous guest's token label.</summary>
    public string? MachineName { get; init; }

    /// <summary>The pause between iterations; returns true when stopping. Tests pass a no-op.</summary>
    public Func<int, bool>? Wait { get; init; }
}

/// <summary>
/// The poll loop (main.lisp:380 poll-loop, spec core §17, media §0.1) on its
/// own thread <c>eta-client-poll</c> (MTA: the recorder's COM - WASAPI, WGC -
/// wants it). Attached: ~30 frames a second through
/// <see cref="GameFrameProcessor.Step"/> with this class as the frame hooks,
/// plus the 250 ms slot (uploads, retention, gdigrab probe, status). Not
/// attached: a search every second that keeps stops, uploads and the status
/// going. Run submission runs on one background worker, never on this thread
/// (deviation from Lisp, PLAN.md decision 2: an unreachable server must not
/// stall frames or the recorder). Nothing a hook or a worker throws ends the loop.
/// </summary>
public sealed class PollLoop : IFrameHooks
{
    /// <summary>+poll-interval+ 1/30 s.</summary>
    public const int PollIntervalMs = 33;

    /// <summary>+search-interval+.</summary>
    public const int SearchIntervalMs = 1000;

    /// <summary>+gui-update-interval+.</summary>
    public const int GuiIntervalMs = 250;

    private readonly PollLoopServices _s;
    private readonly ManualResetEventSlim _stop = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly AccountModeLogger _accountMode = new();
    private readonly Func<int, bool> _wait;
    private Thread? _thread;
    private readonly object _submitGate = new();
    private bool _submitPending;
    private readonly HashSet<Guid> _toastWanted = []; // entry ids of runs completed this session, not yet submitted
    private Task? _submitWorker;
    private IProcessMemoryReader? _reader;
    private long? _lastGui;
    private long? _lastSweep;
    private Task? _upload;
    private bool _busy;

    public PollLoop(PollLoopServices services)
    {
        _s = services;
        _wait = services.Wait ?? (ms => _stop.Wait(ms));
    }

    /// <summary>True while a run or a recording is in flight (<c>*poll-busy-p*</c>).</summary>
    public bool Busy => Volatile.Read(ref _busy);

    /// <summary>The in-flight upload (tests).</summary>
    internal Task? Upload => _upload;

    /// <summary>Starts the thread (once).</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { Name = "eta-client-poll", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>How long <see cref="Stop"/> waits for an in-flight submission pass (cancelled; the queue is saved per entry).</summary>
    public static readonly TimeSpan SubmitStopWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// <c>*stop-requested*</c> + join (quit-app, core §2.6): wakes the loop,
    /// which shuts the recorder down on its own thread, then waits up to
    /// <paramref name="timeout"/>, plus up to <see cref="SubmitStopWait"/> for a
    /// submission pass (its HTTP is cancelled; entries not yet answered simply
    /// stay queued). Safe from any thread but the poll thread itself.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        _stop.Set();
        _cancel.Cancel();
        var thread = _thread;
        var joined = thread is null || thread == Thread.CurrentThread || thread.Join(timeout);
        if (!WaitForSubmissions(SubmitStopWait)) _s.Log("poll loop: a submission pass was still running at stop");
        return joined;
    }

    /// <summary>
    /// The Retry button and a verified token (<c>*retry-requested*</c>): a
    /// submission pass on the worker, attached or not. Safe from any thread.
    /// </summary>
    public void RequestRetry() => RequestSubmit();

    /// <summary>Waits until no submission pass runs or is pending (stop, tests). False on timeout.</summary>
    internal bool WaitForSubmissions(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            Task? worker;
            lock (_submitGate) worker = _submitWorker;
            if (worker is null) return true;
            var left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0) return false;
            try
            {
                if (!worker.Wait(TimeSpan.FromSeconds((double)left / Stopwatch.Frequency))) return false;
            }
            catch (AggregateException)
            {
                // The worker guards everything itself; nothing to add here.
            }
        }
    }

    private bool Stopping => _stop.IsSet;

    private void Run()
    {
        Startup();
        try
        {
            while (!Stopping) Iterate();
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>The loop's start (main.lisp:380): sweep the stale recordings a crash left (not in a developer copy).</summary>
    internal void Startup()
    {
        if (_s.ManageRecordingsFolder) Guard("cleanup-stale-recordings", _s.Recorder.CleanupStaleRecordings);
    }

    /// <summary>
    /// One pass of the loop body (main.lisp:399-410), including its wait.
    /// The thread calls it until stopped; tests call it directly.
    /// </summary>
    internal void Iterate()
    {
        try
        {
            NoteActivity();
            if (_reader is null) _reader = SearchStep();
            else if (!_reader.IsAlive) DetachStep();
            else FrameStep(_reader);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Nothing may end the loop (ui-shell §4.4); back off like a search.
            _s.Log("poll loop: " + e);
            _wait(SearchIntervalMs);
        }
    }

    /// <summary>The unwind at the end of poll-loop: recorder shutdown, trigger log, reader.</summary>
    internal void Shutdown()
    {
        Guard("recorder shutdown", () => _s.Recorder.Shutdown());
        Guard("trigger log close", () => _s.TriggerLog?.Close());
        Guard("reader close", () => _reader?.Dispose());
        _reader = null;
        Guard("pin share game", () => _s.SetGameExe(null));
    }

    /// <summary>note-poll-activity (main.lisp:215): busy flag; apply a deferred update at the first idle frame.</summary>
    private void NoteActivity()
    {
        var inQuest = _s.Frames.Detector.State == DetectorState.InQuest;
        var recording = _s.Recorder.State == RecorderState.Recording;
        Volatile.Write(ref _busy, inQuest || recording);
        if (_s.Deferred?.NoteActivity(inQuest, recording) is { } ready)
            Guard("deferred update", () => _s.ApplyDeferredUpdate(ready));
    }

    /// <summary>poll-search-step (main.lisp:237).</summary>
    private IProcessMemoryReader? SearchStep()
    {
        var reader = _s.Connector.TryAttach();
        _s.Recorder.AudioTargetPid = reader?.Pid;
        // Only a verified game gets the Pin Share relay (and the addon beside it).
        var exe = reader?.ImagePath;
        Guard("pin share game", () => _s.SetGameExe(exe));
        if (reader is not null)
        {
            _s.Frames.Attach(); // a fresh attach disarms
            _lastGui = null;
            return reader;
        }
        // Keep any in-flight stop moving (deletes an abandoned file once ffmpeg exits).
        Guard("recorder", () => _s.Recorder.Step(_s.Frames.Detector.State == DetectorState.InQuest, [], null));
        Guard("upload", MaybeStartUpload);
        Guard("retention", MaybeSweepRecordings);
        Guard("status", () => _s.Tick(new PollTick(false, null, false, _s.Connector.Rejection)));
        _wait(SearchIntervalMs);
        return null;
    }

    /// <summary>poll-detach-step (main.lisp:285) with decision 2b: runs abandoned by the exit are queued.</summary>
    private void DetachStep()
    {
        var reader = _reader!;
        _reader = null;
        Guard("reader close", reader.Dispose);
        _s.Recorder.AudioTargetPid = null;
        Guard("pin share game", () => _s.SetGameExe(null));
        _s.Frames.Detach(this);
    }

    /// <summary>poll-frame-step (main.lisp:298): the frame, then the 250 ms slot and the wait.</summary>
    private void FrameStep(IProcessMemoryReader reader)
    {
        var result = _s.Frames.Step(reader, this);
        var now = _s.Clock.Now;
        if (_lastGui is not { } last || LispMath.ElapsedMs(now, last, _s.Clock.TicksPerSecond) > GuiIntervalMs)
        {
            _lastGui = now;
            Guard("upload", MaybeStartUpload);
            Guard("retention", MaybeSweepRecordings);
            // Keep the gdigrab verdict fresh while idle; never during a quest or capture.
            if ((_s.RecordingSettings?.Invoke() ?? RecordingSettingsFor(_s.Config)).RecordingEnabled && !Busy)
                Guard("gdigrab probe", () => _s.MaybeStartGdigrabProbe(reader.WindowTitle));
            var snapshot = result.Snapshot ?? (result.InReadGrace ? _s.Frames.PreviousSnapshot : null);
            Guard("status", () => _s.Tick(new PollTick(true, snapshot, _s.Frames.ReadFailing, null)));
        }
        _wait(PollIntervalMs);
    }

    /// <summary>The recording settings from config (media §1.1).</summary>
    public static RecordingSettings RecordingSettingsFor(ConfigStore config) => new()
    {
        RecordEnabled = config.RecordEnabled,
        TrackingOnly = config.TrackingOnly,
        RecordAudio = config.RecordAudio,
        HwEncode = config.HwEncode,
        WgcDisable = config.GetBool(ConfigKeys.WgcDisable),
    };

    // ---- IFrameHooks (spec core §17.3 steps 4-8) ----

    void IFrameHooks.RecorderStep(DetectorState state, IReadOnlyList<Plist> runs, string? windowTitle) =>
        _s.Recorder.Step(state == DetectorState.InQuest, runs, windowTitle);

    void IFrameHooks.GhostFetch(Snapshot? snapshot) =>
        _s.Ghost.MaybeStartFetch(snapshot?.QuestPtr, snapshot?.QuestName, () => DescribeLoad(snapshot!), _s.GhostFetch);

    void IFrameHooks.PinSetFetch(Snapshot? snapshot) =>
        _s.PinSets?.OnSnapshot(snapshot is null
            ? null
            : new PinShareQuest(snapshot.QuestPtr ?? 0, snapshot.QuestName, snapshot.QuestNumber, snapshot.Episode),
            _cancel.Token);

    void IFrameHooks.GhostRaceStep(Detector detector, Snapshot? snapshot)
    {
        var camera = snapshot?.Camera is { } c ? new CameraState(c.X, c.Y, c.Z, c.DirX, c.DirY, c.DirZ, (int)c.Zoom) : null;
        var me = snapshot?.MyPlayer is { } p ? new GhostPlayer(p.Floor ?? 0, p.Room ?? 0, p.Y ?? 0f) : null;
        long? elapsed = detector.Telemetry is { } t ? LispMath.ElapsedMs(_s.Clock.Now, t.StartTime, _s.Clock.TicksPerSecond) : null;
        _s.Ghost.Step(detector.State == DetectorState.InQuest, camera, me, elapsed,
            slug => snapshot is not null && slug is not null &&
                    _s.Catalog.FindAll(snapshot.QuestNumber, snapshot.Episode, snapshot.QuestName).Any(d => d.Slug == slug));
    }

    void IFrameHooks.CompletedRuns(IReadOnlyList<Plist> runs) => HandleCompletedRuns(runs);

    /// <summary>ghost-fetch-wanted's facts about a fresh quest load (ghost.lisp:415).</summary>
    private GhostLoadInfo DescribeLoad(Snapshot snapshot) => new(
        _s.Catalog.FindAll(snapshot.QuestNumber, snapshot.Episode, snapshot.QuestName).Select(d => d.Slug).ToList(),
        PsobbTables.DifficultyLabel(snapshot.Difficulty, snapshot.Anguish),
        Detector.PartyOf(snapshot).Count,
        _s.Config.GhostRace,
        _s.Config.SubmissionToken.Length > 0,
        PsobbTables.AccountModeOfColor(snapshot.MyPlayer?.NameColor));

    /// <summary>
    /// handle-completed-runs (main.lisp:136): drop aborted runs unless
    /// :submit-aborted, annotate against the ghost, log the account mode, stamp
    /// tracking-only mode, enqueue, then submit and celebrate. The recorder has
    /// already stamped :video-offset-ms onto these very plists (step 4). The
    /// enqueue happens here on the poll thread; the submission and its toasts
    /// follow on the submit worker.
    /// </summary>
    internal void HandleCompletedRuns(IReadOnlyList<Plist> runs)
    {
        var config = _s.Config;
        IReadOnlyList<Plist> kept = config.SubmitAborted ? runs : runs.Where(r => !r.Get("ABORTED").IsTruthy()).ToList();
        kept = _s.Ghost.AnnotateRuns(kept);
        var autoSubmit = config.AutoSubmit;
        // Marked under the gate in the same step as the enqueue, so a pass
        // already running cannot submit one of these before it is marked.
        lock (_submitGate)
        {
            foreach (var run in kept)
            {
                if (_accountMode.LineFor(run, _s.Clock.UniversalTime) is { } line) _s.Log(line);
                var entry = _s.Queue.Enqueue(RunEntries.ApplyTrackingMode(run, config.TrackingOnly, config.TrackingPrivate));
                if (autoSubmit) _toastWanted.Add(entry.Id);
            }
        }
        if (kept.Count > 0 && autoSubmit) RequestSubmit();
    }

    /// <summary>
    /// Asks the submit worker for a pass, starting it when idle. Requests made
    /// while a pass runs coalesce into one more pass. The celebration is per
    /// entry, not per pass: a run completed this session (<see cref="_toastWanted"/>)
    /// is celebrated by whichever pass submits it - a pass started for a Retry
    /// may pick a fresh run up before the pass asked for it. Retried backlog is
    /// never celebrated (Lisp: notify-standing-toasts after handle-completed-runs).
    /// </summary>
    private void RequestSubmit()
    {
        lock (_submitGate)
        {
            if (_cancel.IsCancellationRequested) return;
            _submitPending = true;
            _submitWorker ??= Task.Run(SubmitWorker);
        }
    }

    /// <summary>The submit worker: one pass at a time until no request is pending.</summary>
    private void SubmitWorker()
    {
        while (true)
        {
            Guid[] wantedAtStart;
            lock (_submitGate)
            {
                if (!_submitPending || _cancel.IsCancellationRequested)
                {
                    _submitWorker = null;
                    return;
                }
                _submitPending = false;
                wantedAtStart = [.. _toastWanted];
            }
            Guard("submit", () =>
            {
                var results = Submit();
                var toasts = new List<RunEntry>();
                lock (_submitGate)
                {
                    // Every run this pass saw is settled: celebrated now or never.
                    // One enqueued after the pass read the queue stays wanted.
                    foreach (var entry in results ?? [])
                    {
                        if (_toastWanted.Remove(entry.Id)) toasts.Add(entry);
                    }
                    _toastWanted.ExceptWith(wantedAtStart);
                }
                if (!_s.Config.RankToast) return;
                foreach (var entry in toasts)
                {
                    if (RunDisplay.ToastFor(entry.Data, _s.Config.Language) is { } t)
                        Guard("toast", () => _s.Toast(t));
                }
            });
        }
    }

    /// <summary>submit-queued! on the submit worker. Null when no token could be had.</summary>
    private IReadOnlyList<RunEntry>? Submit()
    {
        // Deviation (improvement): with nothing unsent there is nothing to do.
        // The Lisp pass still registered an anonymous guest first, so a Retry
        // click on an empty queue created a server account for nothing.
        if (!_s.Queue.Entries.Any(e => e.Status is RunStatus.Queued or RunStatus.Failed)) return [];
        try
        {
            return _s.Queue.SubmitQueuedAsync(_s.Config, _s.Registrar, _s.Submitter, _s.MachineName, _cancel.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            return null; // stopping: unanswered entries stay queued
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _s.Log("submit failed: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// maybe-start-upload (main.lisp:172): the oldest pending recording, one at
    /// a time, never while a run or any recorder activity is in flight.
    /// </summary>
    private void MaybeStartUpload()
    {
        if (!_s.Config.VideoUpload || Busy || _s.Recorder.State != RecorderState.Idle) return;
        if (_upload is { IsCompleted: false }) return;
        var entry = _s.Queue.UploadCandidate(); // a give-up raises Queue.Changed (the runs list refreshes)
        if (entry is null) return;
        var token = _cancel.Token;
        _upload = Task.Run(async () =>
        {
            try
            {
                await _s.Queue.UploadEntryVideoAsync(entry, _s.Uploader, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _s.Log("video upload failed: " + e.Message);
            }
        });
    }

    /// <summary>maybe-sweep-recordings (main.lisp:33): the size budget, every 120 s.</summary>
    private void MaybeSweepRecordings()
    {
        if (!_s.ManageRecordingsFolder) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastSweep is { } last && now - last < RecordingRetention.IntervalSeconds * Stopwatch.Frequency) return;
        _lastSweep = now;
        var (@protected, uploaded) = _s.Queue.VideoPathRetentionSets();
        _s.Recorder.SweepRecordings(_s.Config.RecordMaxTotalBytes, @protected, uploaded);
    }

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _s.Log($"poll loop: {what} failed: {e.Message}");
        }
    }
}

internal static class SexpTruth
{
    /// <summary>Lisp truthiness of a possibly absent value.</summary>
    public static bool IsTruthy(this SexpNode? node) => node is { IsTrue: true };
}
