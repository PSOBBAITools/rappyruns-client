using System.Diagnostics;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Media;

/// <summary>The recorder's lifecycle state (recording.lisp:975).</summary>
public enum RecorderState
{
    Idle,
    Recording,
    Stopping,
    Remuxing,
}

/// <summary>Tray icon of a recorder notification (notify-user's :icon).</summary>
public enum NoticeIcon
{
    Warning,
    Info,
}

/// <summary>
/// A tray balloon the recorder wants shown: i18n keys (strings.json) for the
/// title and text. The integrator formats them with <c>Strings.Tr</c> in the
/// current language and shows the balloon; failures there are swallowed.
/// </summary>
public sealed record RecorderNotice(string TitleKey, string TextKey, NoticeIcon Icon);

/// <summary>
/// Everything the recorder reads from the rest of the client, injected so the
/// state machine stays testable (the Lisp reads specials and config directly).
/// Defaults are the production behavior where one exists.
/// </summary>
public sealed class RecorderEnvironment
{
    /// <summary>The recording config keys, read at each decision point.</summary>
    public Func<RecordingSettings> Settings { get; init; } = () => RecordingSettings.Default;

    /// <summary><c>resolve-record-dir</c> (with its one-time folder migration).</summary>
    public Func<string> RecordDir { get; init; } = () => RecordingFiles.ResolveRecordDir(null);

    /// <summary><c>resolve-ffmpeg-path</c>.</summary>
    public Func<string> FfmpegPath { get; init; } = () => RecordingFiles.ResolveFfmpegPath(null);

    /// <summary>The HW encoder probe's shared verdict.</summary>
    public HwEncoderStatus Hw { get; init; } = new();

    /// <summary>
    /// <c>start-hw-encoder-probe</c>: re-probe in the background (a no-op while
    /// one runs). Called at capture start while the verdict is :spawn-failed.
    /// </summary>
    public Action? StartHwEncoderProbe { get; init; }

    /// <summary><c>low-memory-machine-p</c>.</summary>
    public Func<bool> LowMemory { get; init; } = () => false;

    /// <summary><c>encoder-thread-count</c>.</summary>
    public Func<int> EncoderThreads { get; init; } = () => FfmpegArgs.EncoderThreadCount();

    /// <summary>Tray balloons (<c>notify-user</c>); null = silent, like the SBCL test runs.</summary>
    public Action<RecorderNotice>? Notify { get; init; }

    /// <summary><c>get-internal-real-time</c>: a monotonic tick count.</summary>
    public Func<long> Timestamp { get; init; } = Stopwatch.GetTimestamp;

    /// <summary>internal-time-units-per-second for <see cref="Timestamp"/>.</summary>
    public long TicksPerSecond { get; init; } = Stopwatch.Frequency;

    /// <summary>Local wall clock for the rec-tmp file name.</summary>
    public Func<DateTime> LocalNow { get; init; } = () => DateTime.Now;

    /// <summary><c>get-universal-time</c>, for naming a run without :finished-at.</summary>
    public Func<long> UniversalNow { get; init; } = Lisp.UniversalTimeNow;

    /// <summary><c>recording-token</c>.</summary>
    public Func<string> Token { get; init; } = RecordingFiles.RecordingToken;

    /// <summary>probe-file, for <c>deduplicate-path</c>.</summary>
    public Func<string, bool> PathExists { get; init; } = RecordingFiles.PathExists;

    /// <summary>A pause while <see cref="Recorder.Shutdown"/> polls (50 ms in the Lisp).</summary>
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;
}

/// <summary>
/// Automatic quest recording (media spec §1.2-§1.10, recording.lisp:971-1385):
/// one quest stay = one capture. Recording starts on the detector's
/// idle -> in-quest edge and stops when it returns to idle; the file is kept
/// (remuxed with the moov up front, loudness-normalized and tail-trimmed) only
/// when a run completed during the capture, else deleted. Every side effect
/// goes through <see cref="ICaptureBackend"/>.
/// <para>
/// Threading: <see cref="Step"/>, <see cref="Shutdown"/>,
/// <see cref="CleanupStaleRecordings"/> and <see cref="SweepRecordings"/> run
/// on the poll thread only. <see cref="State"/> and <see cref="LastError"/>
/// may be read from the UI as snapshots.
/// </para>
/// </summary>
public sealed class Recorder
{
    /// <summary>+stop-grace-seconds+: wait for "q" before killing (must exceed the 3 s audio drain).</summary>
    public const int StopGraceSeconds = 8;

    /// <summary>+remux-grace-seconds+: the remux's run budget before it is killed.</summary>
    public const int RemuxGraceSeconds = 180;

    private readonly RecorderEnvironment _env;
    private volatile RecorderState _state = RecorderState.Idle;
    private volatile string? _lastError;
    private int _audioTargetPid;

    // Process-wide once-per-streak/once-per-process notices (recording.lisp:991-1007).
    // One recorder exists per process, so they live here.
    private bool _captureFailureNotified;
    private bool _softwareFallbackNotified;
    private bool _overlapRiskNotified;

    public Recorder(ICaptureBackend backend, RecorderEnvironment? environment = null)
    {
        Backend = backend;
        _env = environment ?? new RecorderEnvironment();
    }

    public ICaptureBackend Backend { get; }

    public RecorderState State => _state;

    /// <summary>The one-line error for the GUI, or null. Survives the return to idle.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// <c>on-keep</c>: called with (final path, best run, untrimmed) after a
    /// kept file is in place. main.lisp wires it to <c>link-video-file!</c> + a
    /// list refresh (<see cref="Store.RunQueue.LinkVideoFile"/>). Untrimmed (the
    /// remux failed, so the tail past the run may show the desktop) is a C#
    /// addition (S07): the entry is marked so it is never auto-uploaded.
    /// Exceptions are swallowed.
    /// </summary>
    public Action<string, Plist, bool>? OnKeep { get; set; }

    /// <summary>
    /// <c>*audio-target-pid*</c>: the attached PSOBB process (set on attach,
    /// cleared on detach by the poll loop). Null = no game audio.
    /// </summary>
    public int? AudioTargetPid
    {
        get => Volatile.Read(ref _audioTargetPid) is var pid and not 0 ? pid : null;
        set => Volatile.Write(ref _audioTargetPid, value ?? 0);
    }

    // Per-capture fields (recording.lisp:973).
    internal ICaptureHandle? Capture { get; private set; }

    /// <summary>Timestamp right after the capture spawn completed; null when not capturing.</summary>
    internal long? CaptureStartTicks { get; set; }

    internal string? TmpPath { get; private set; }

    internal List<Plist> SessionRunsList { get; private set; } = [];

    /// <summary>Capture elapsed (ms) when the last run of this capture completed.</summary>
    internal long? RunEndMs { get; private set; }

    private bool _lastInQuest;

    internal long? StopDeadline { get; set; }

    private bool _pendingKeep;
    private Plist? _pendingRun;

    internal string? FinalPath { get; private set; }

    internal ICaptureHandle? RemuxCapture { get; private set; }

    internal long? RemuxDeadline { get; set; }

    private long Now => _env.Timestamp();

    /// <summary>
    /// <c>recorder-step</c> (recording.lisp:1296): feed one poll frame, BEFORE
    /// the poll loop enqueues <paramref name="completedRuns"/> (the offsets
    /// stamped here must travel with the submission). Runs are credited before
    /// the stop check because the detector flips to idle on the very frame the
    /// full clear completes.
    /// </summary>
    /// <param name="inQuest">The detector's state after its step: true = :in-quest, false = :idle.</param>
    /// <param name="completedRuns">detector-step's return value (the shared run plists).</param>
    /// <param name="windowTitle">The PSOBB window's actual title, or null when not attached.</param>
    public void Step(bool inQuest, IReadOnlyList<Plist> completedRuns, string? windowTitle)
    {
        if (completedRuns.Count > 0 && _state is RecorderState.Recording or RecorderState.Stopping)
        {
            NoteRunVideoTiming(completedRuns);
            SessionRunsList.AddRange(completedRuns);
        }
        switch (_state)
        {
            case RecorderState.Idle:
                // Edge-triggered: a failed start or a mid-quest tracking-only
                // toggle never produces a partial video of a valid run.
                if (inQuest && !_lastInQuest && _env.Settings().RecordingEnabled && windowTitle is not null)
                    StartRecording(windowTitle);
                break;
            case RecorderState.Recording:
                if (!Backend.IsAlive(Capture!))
                    AbortCapture("ffmpeg exited unexpectedly");
                else if (!inQuest)
                    BeginStop();
                break;
            case RecorderState.Stopping:
                if (!Backend.IsAlive(Capture!))
                    FinalizeCapture();
                else if (StopDeadline is { } stopBy && Now >= stopBy)
                {
                    // "q" did not work; the fragmented MP4 survives the kill.
                    Try(() => Backend.Kill(Capture!));
                    StopDeadline = null;
                }
                break;
            case RecorderState.Remuxing:
                if (!Backend.IsAlive(RemuxCapture!))
                    FinishRemux();
                else if (RemuxDeadline is { } remuxBy && Now >= remuxBy)
                {
                    // Runaway remux: the next frame falls back via FinishRemux.
                    Try(() => Backend.Kill(RemuxCapture!));
                    RemuxDeadline = null;
                }
                break;
        }
        _lastInQuest = inQuest;
    }

    /// <summary><c>start-recording</c> (recording.lisp:1017).</summary>
    private void StartRecording(string windowTitle)
    {
        var settings = _env.Settings();
        var hw = _env.Hw;
        if (settings.HwEncode && hw.State == HwProbeState.SpawnFailed)
            _env.StartHwEncoderProbe?.Invoke();
        var ffmpeg = _env.FfmpegPath();
        var output = RecordingFiles.RecordingTmpPath(_env.RecordDir(), _env.LocalNow(), _env.Token());
        var audioPid = settings.RecordAudio ? AudioTargetPid : null;
        var audioPipe = audioPid is not null ? FfmpegArgs.AudioPipeName : null;
        var encoder = settings.HwEncode ? hw.Encoder : null;
        // Windowed WGC first (overlap-proof), then the monitor paths.
        var wgc = Backend.WgcCapture();
        var monitor = wgc is null ? Backend.CaptureMonitor() : null;
        var args = FfmpegArgs.Build(new FfmpegCaptureOptions
        {
            WindowTitle = windowTitle,
            OutputPath = output,
            AudioPipe = audioPipe,
            Monitor = monitor,
            Wgc = wgc,
            VideoEncoder = encoder,
            GpuChain = encoder == "h264_qsv" && hw.GpuChain,
            LowMemory = _env.LowMemory(),
            EncoderThreads = _env.EncoderThreads(),
        });
        var result = Backend.StartCapture(ffmpeg, args, output, audioPipe, audioPid, wgc?.Session);
        if (result.Handle is { } capture)
        {
            Capture = capture;
            CaptureStartTicks = Now;
            TmpPath = output;
            SessionRunsList = [];
            // Cleared with the runs it belongs to: an inherited mark would cut
            // this capture to the previous run's length.
            RunEndMs = null;
            _lastError = null;
            _state = RecorderState.Recording;
            _captureFailureNotified = false;
            if (settings.HwEncode && encoder is null && hw.State == HwProbeState.SpawnFailed && !_softwareFallbackNotified)
            {
                _softwareFallbackNotified = true;
                Notify("notify-software-encode-title", "notify-software-encode-text", NoticeIcon.Info);
            }
            if (monitor?.Crop is not null && !_overlapRiskNotified)
            {
                _overlapRiskNotified = true;
                Notify("notify-overlap-title", "notify-overlap-text", NoticeIcon.Info);
            }
        }
        else
        {
            // Stay idle; the edge trigger retries on the next quest.
            var message = result.Error ?? "could not start ffmpeg";
            _lastError = message;
            if (!_captureFailureNotified)
            {
                _captureFailureNotified = true;
                // 4551 = an Application Control policy (Smart App Control)
                // blocked ffmpeg.exe: the fix is in Windows Security.
                Notify("notify-capture-failed-title",
                    message.Contains("(Windows error 4551)", StringComparison.Ordinal)
                        ? "notify-capture-blocked-text"
                        : "notify-capture-failed-text",
                    NoticeIcon.Warning);
            }
        }
    }

    /// <summary>
    /// <c>note-run-video-timing</c> (recording.lisp:1102): one elapsed reading,
    /// round(1000 * ticks / frequency) half to even, moves the run-end mark and
    /// stamps the runs' :video-offset-ms (<see cref="SessionRuns.NoteRunVideoTiming"/>).
    /// </summary>
    private void NoteRunVideoTiming(IReadOnlyList<Plist> runs)
    {
        if (CaptureStartTicks is not { } start) return;
        var elapsedMs = Lisp.Round(1000 * (Now - start), _env.TicksPerSecond);
        RunEndMs = SessionRuns.NoteRunVideoTiming(elapsedMs, RunEndMs, runs);
    }

    /// <summary>
    /// <c>begin-stop</c> (recording.lisp:1162): decide the file's fate - keep it
    /// under the best run's (deduplicated) name, or delete it - and ask ffmpeg
    /// to finish.
    /// </summary>
    private void BeginStop()
    {
        var best = SessionRuns.BestSessionRun(SessionRunsList);
        _pendingKeep = best is not null;
        _pendingRun = best;
        FinalPath = best is null
            ? null
            : RecordingFiles.DeduplicatePath(
                Path.Combine(_env.RecordDir(), RecordingFiles.RunVideoFilename(best, _env.UniversalNow)),
                _env.PathExists);
        StopDeadline = Now + StopGraceSeconds * _env.TicksPerSecond;
        _state = RecorderState.Stopping;
        Try(() => Backend.RequestStop(Capture!));
    }

    /// <summary><c>reset-recorder</c>: back to idle, every per-capture field cleared (not LastError).</summary>
    private void Reset()
    {
        Capture = null;
        CaptureStartTicks = null;
        TmpPath = null;
        SessionRunsList = [];
        RunEndMs = null;
        _pendingKeep = false;
        _pendingRun = null;
        FinalPath = null;
        RemuxCapture = null;
        RemuxDeadline = null;
        StopDeadline = null;
        _state = RecorderState.Idle;
    }

    /// <summary><c>finalize-capture</c>: ffmpeg is dead - remux a kept file or delete an abandoned one.</summary>
    private void FinalizeCapture()
    {
        Try(() => Backend.Close(Capture!));
        if (_pendingKeep && FinalPath is not null)
            BeginRemux();
        else
        {
            Try(() => Backend.DeleteFile(TmpPath!));
            Reset();
        }
    }

    /// <summary>
    /// <c>begin-remux</c> (recording.lisp:1206). The duration is read HERE, not
    /// at the stop: runs credited while stopping must not be cut off.
    /// </summary>
    private void BeginRemux()
    {
        var args = FfmpegArgs.BuildRemux(TmpPath!, FinalPath!, SessionRuns.SessionVideoDurationMs(RunEndMs));
        var result = Backend.StartRemux(_env.FfmpegPath(), args);
        if (result.Handle is { } remux)
        {
            RemuxCapture = remux;
            RemuxDeadline = Now + RemuxGraceSeconds * _env.TicksPerSecond;
            _state = RecorderState.Remuxing;
        }
        else
            SaveRecording(remuxed: false);
    }

    /// <summary><c>finish-remux</c>: keep a clean remux, else drop its partial output and fall back.</summary>
    private void FinishRemux()
    {
        var capture = RemuxCapture!;
        var ok = false;
        Try(() => ok = Backend.Succeeded(capture));
        Try(() => Backend.Close(capture));
        if (!ok) Try(() => Backend.DeleteFile(FinalPath!));
        SaveRecording(ok);
    }

    /// <summary>
    /// <c>save-recording</c> (recording.lisp:1246): the remux wrote the final
    /// file (delete the tmp), or the fragmented tmp is renamed onto it - then
    /// untrimmed, which is ballooned. The Lisp on-keep handed that file
    /// straight to the uploader; here on-keep is told it is untrimmed, and the
    /// entry stays out of the auto-upload (S07).
    /// </summary>
    private void SaveRecording(bool remuxed)
    {
        try
        {
            if (remuxed)
                Try(() => Backend.DeleteFile(TmpPath!));
            else
                Backend.RenameFile(TmpPath!, FinalPath!);
            if (!remuxed)
            {
                _lastError = "remux failed; recording kept whole - its tail is untrimmed";
                Notify("notify-untrimmed-title", "notify-untrimmed-text", NoticeIcon.Warning);
            }
            if (OnKeep is { } onKeep)
            {
                var path = FinalPath!;
                var run = _pendingRun!;
                Try(() => onKeep(path, run, !remuxed));
            }
        }
        catch (Exception e)
        {
            _lastError = "could not save recording: " + e.Message;
        }
        Reset();
    }

    /// <summary><c>abort-capture</c>: ffmpeg died on its own mid-capture.</summary>
    private void AbortCapture(string message)
    {
        Try(() => Backend.Close(Capture!));
        Try(() => Backend.DeleteFile(TmpPath!));
        Reset();
        _lastError = message;
    }

    /// <summary>
    /// <c>recorder-shutdown</c> (recording.lisp:1365): the client is exiting -
    /// stop a capture, wait up to <paramref name="timeout"/> (8 s) for it, kill
    /// it if needed, then the same for the remux of a kept file.
    /// </summary>
    public void Shutdown(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(StopGraceSeconds);
        if (_state == RecorderState.Recording) BeginStop();
        if (_state == RecorderState.Stopping)
        {
            WaitForCapture(Capture!, limit);
            FinalizeCapture();
        }
        if (_state == RecorderState.Remuxing)
        {
            WaitForCapture(RemuxCapture!, limit);
            FinishRemux();
        }
    }

    /// <summary><c>wait-for-capture</c>: poll every 50 ms until exit or the deadline, then kill.</summary>
    private void WaitForCapture(ICaptureHandle capture, TimeSpan timeout)
    {
        var deadline = Now + (long)(timeout.TotalSeconds * _env.TicksPerSecond);
        while (Backend.IsAlive(capture) && Now < deadline)
            _env.Sleep(TimeSpan.FromMilliseconds(50));
        if (Backend.IsAlive(capture))
            Try(() => Backend.Kill(capture));
    }

    /// <summary>
    /// <c>cleanup-stale-recordings</c> (recording.lisp:1380): at poll-loop start,
    /// delete rec-tmp-*.mp4 left by a crashed session (not their .stderr.txt -
    /// parity).
    /// </summary>
    public void CleanupStaleRecordings()
    {
        IReadOnlyList<string> stale = [];
        Try(() => stale = Backend.ListStaleFiles(_env.RecordDir()));
        foreach (var path in stale)
            Try(() => Backend.DeleteFile(path));
    }

    /// <summary>
    /// <c>apply-recording-retention</c> (store.lisp:529) minus the queue scan:
    /// only while idle and with a cap, delete what
    /// <see cref="RecordingRetention.RecordingsToEvict"/> picks. The store
    /// supplies the protected/uploaded sets (<c>video-path-retention-sets</c>).
    /// Returns the deleted paths.
    /// </summary>
    public IReadOnlyList<string> SweepRecordings(long? capBytes, IReadOnlyCollection<string> protectedPaths, IReadOnlyCollection<string> uploaded)
    {
        if (capBytes is null || _state != RecorderState.Idle) return [];
        IReadOnlyList<RecordingFile> files = [];
        Try(() => files = Backend.ListRecordings(_env.RecordDir()));
        var evict = RecordingRetention.RecordingsToEvict(files, capBytes, protectedPaths, uploaded);
        foreach (var path in evict)
            Try(() => Backend.DeleteFile(path));
        return evict;
    }

    private void Notify(string titleKey, string textKey, NoticeIcon icon)
    {
        if (_env.Notify is { } notify)
            Try(() => notify(new RecorderNotice(titleKey, textKey, icon)));
    }

    /// <summary>Lisp <c>ignore-errors</c>.</summary>
    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Swallowed, as the Lisp does.
        }
    }
}
