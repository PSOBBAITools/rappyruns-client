using System.Globalization;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Game;

/// <summary>
/// A reader attached to a live process (RappyRuns.Win.Game.LiveReader), with
/// what the poll loop needs beyond reads: liveness, the pid for logs and audio
/// capture, the failing read's error code, the exe path.
/// </summary>
public interface IProcessMemoryReader : IMemoryReader, IDisposable
{
    int Pid { get; }

    /// <summary>GetLastError of the most recent failed read (win32.lisp:358), or null.</summary>
    int? LastReadError { get; }

    /// <summary>GetExitCodeProcess == STILL_ACTIVE (win32.lisp:323 reader-alive-p).</summary>
    bool IsAlive { get; }

    /// <summary>QueryFullProcessImageNameW of the process, or null (win32.lisp:384).</summary>
    string? ImagePath { get; }
}

/// <summary>
/// The Authenticode rejection of a running PSOBB (main.lisp:45 *psobb-rejection*):
/// the poll loop refuses to attach to that pid - nothing is detected, recorded or submitted.
/// </summary>
public sealed record PsobbRejection(int Pid, string? Path, SignatureStatus Status, string? Signer)
{
    /// <summary>i18n.lisp:804 signature-status-label: why the exe was refused.</summary>
    public string Label(Language language) => StatusLabel(language, Status, Signer);

    public static string StatusLabel(Language language, SignatureStatus status, string? signer) => status switch
    {
        SignatureStatus.Unsigned => Strings.Default.Tr(language, "signature-unsigned"),
        // A valid signature that still got rejected: the signer is not trusted - show who signed it.
        SignatureStatus.Valid => Strings.Default.Tr(language, "signature-untrusted-signer", signer ?? "?"),
        _ => Strings.Default.Tr(language, "signature-invalid"),
    };
}

/// <summary>
/// Hook points of one attached poll frame, called by <see cref="GameFrameProcessor.Step"/>
/// in the order main.lisp:298 poll-frame-step fixes (spec core §17.3 - a contract).
/// Every hook is isolated: an exception is swallowed (ignore-errors) and never
/// reaches detection. Default implementations do nothing.
/// </summary>
public interface IFrameHooks
{
    /// <summary>
    /// §17.3 step 4, every frame: the recorder. <paramref name="runs"/> are this
    /// frame's completed runs, before they are queued - the recorder may add
    /// :VIDEO-OFFSET-MS to them here (spec core §22 #45).
    /// </summary>
    void RecorderStep(DetectorState state, IReadOnlyList<Plist> runs, string? windowTitle)
    {
    }

    /// <summary>§17.3 step 5: start the ghost fetch once per quest load.</summary>
    void GhostFetch(Snapshot? snapshot)
    {
    }

    /// <summary>§17.3 step 6: the Pin Set fetch.</summary>
    void PinSetFetch(Snapshot? snapshot)
    {
    }

    /// <summary>§17.3 step 7: ghost race / overlay step.</summary>
    void GhostRaceStep(Detector detector, Snapshot? snapshot)
    {
    }

    /// <summary>
    /// §17.3 step 8, only when runs completed: ghost annotation, account-mode
    /// log, tracking stamp, enqueue, submit, toasts, list refresh.
    /// </summary>
    void CompletedRuns(IReadOnlyList<Plist> runs)
    {
    }
}

/// <summary>What one poll frame produced.</summary>
public sealed record FrameResult(
    Snapshot? Snapshot,
    IReadOnlyList<Plist> Runs,
    bool ReadFailing,
    bool InReadGrace);

/// <summary>Options of the frame processor, read every frame (settings can change at any time).</summary>
public sealed class GameFrameOptions
{
    /// <summary>config :trigger-log.</summary>
    public Func<bool> TriggerLogEnabled { get; init; } = () => false;

    /// <summary>config :ghost-overlay ∧ :ghost-marker: read the camera every frame.</summary>
    public Func<bool> CameraWanted { get; init; } = () => false;

    /// <summary>
    /// PLAN.md decision 2a: consecutive unreadable frames are tolerated this long
    /// while the process lives before the detector sees "game gone". 0 restores
    /// the Lisp behaviour (abandon on the first failed snapshot).
    /// </summary>
    public long ReadGraceMs { get; init; } = 1000;
}

/// <summary>
/// The detection half of the Lisp poll loop (main.lisp:237-377): attach,
/// per-frame snapshot → detector → hooks → trigger log / last kill / run logs,
/// and detach. The poll thread owns it; call <see cref="Step"/> about 30 times
/// a second (sleep 1/30 s after each), <see cref="Attach"/> once per fresh
/// attach, <see cref="Detach"/> when the process died.
/// </summary>
public sealed class GameFrameProcessor(
    Detector detector,
    RunLogs runLogs,
    IGameClock clock,
    GameFrameOptions? options = null,
    TriggerLog? triggerLog = null,
    Action<string>? log = null)
{
    /// <summary>main.lisp:10 +read-failure-frames+: ≈2 s of attached frames without a snapshot.</summary>
    public const int ReadFailureFrames = 60;

    private readonly GameFrameOptions _options = options ?? new GameFrameOptions();
    private long? _lastHeavySample;
    private long? _graceStart;

    public Detector Detector => detector;

    public RunLogs RunLogs => runLogs;

    /// <summary>The previous frame's snapshot (null after a failed frame or a detach).</summary>
    public Snapshot? PreviousSnapshot { get; private set; }

    /// <summary>Consecutive attached frames with no snapshot (win32.lisp:217 unreadable-frames).</summary>
    public int UnreadableFrames { get; private set; }

    /// <summary>"Attached but reads failing" for the status line (≥ <see cref="ReadFailureFrames"/>).</summary>
    public bool ReadFailing => UnreadableFrames >= ReadFailureFrames;

    /// <summary>
    /// A fresh attach (main.lisp:262): detector-step(NIL) disarms, so a quest the
    /// client attached to mid-run is never timed. Returns the (normally empty) runs.
    /// </summary>
    public List<Plist> Attach()
    {
        UnreadableFrames = 0;
        _graceStart = null;
        PreviousSnapshot = null;
        return detector.Step(null);
    }

    /// <summary>
    /// The attached process died (main.lisp:285 poll-detach-step). Returns the
    /// runs detector-step(NIL) emits - trackers running ≥15 s, as aborted runs.
    /// <b>Deviation</b> (PLAN.md decision 2b): the Lisp dropped these; the caller
    /// must now hand them to the same completed-runs path as a lobby return
    /// (<see cref="IFrameHooks.CompletedRuns"/> is invoked here). The recorder
    /// gets its wind-down step with no runs, as in the Lisp.
    /// </summary>
    public List<Plist> Detach(IFrameHooks hooks)
    {
        var runs = detector.Step(null);
        PreviousSnapshot = null;
        UnreadableFrames = 0;
        _graceStart = null;
        Guard(() => hooks.RecorderStep(detector.State, [], null));
        if (runs.Count > 0) Guard(() => hooks.CompletedRuns(runs));
        return runs;
    }

    /// <summary>
    /// main.lisp:69 augment-snapshot: monsters every frame, the camera when the
    /// ghost marker wants it, the inventory about once a second - only while a
    /// quest is loaded. Each read failing reads as absent.
    /// </summary>
    public Snapshot? Augment(IMemoryReader reader, Snapshot? snapshot)
    {
        if (snapshot is null || !snapshot.QuestLoaded) return snapshot;
        snapshot = snapshot with { Monsters = Try(() => PsobbReader.ReadMonsters(reader)) };
        if (_options.CameraWanted()) snapshot = snapshot with { Camera = Try(() => PsobbReader.ReadCamera(reader)) };
        var now = clock.Now;
        if (_lastHeavySample is not { } last || now - last >= clock.TicksPerSecond)
        {
            _lastHeavySample = now;
            var myIndex = snapshot.MyIndex ?? 0;
            snapshot = snapshot with { Inventory = Try(() => PsobbReader.ReadInventory(reader, myIndex)) };
        }
        return snapshot;
    }

    /// <summary>
    /// One attached frame (main.lisp:298 poll-frame-step), steps 1-10 of spec
    /// core §17.3 with the hooks in between:
    /// 1 snapshot (+augment) → 2 detector → 3 read health → 4 recorder →
    /// 5 ghost fetch → 6 pin set fetch → 7 ghost race → 8 completed runs →
    /// 9 trigger log → 10 last kill + run logs. The caller then does step 11
    /// (the retry request), step 12 (the 250 ms GUI tick: uploads, retention,
    /// gdigrab probe, status line with <see cref="ReadFailing"/>) and sleeps 1/30 s.
    /// </summary>
    public FrameResult Step(IMemoryReader reader, IFrameHooks hooks)
    {
        // 1. Snapshot; any read exception is "no snapshot" (ignore-errors).
        Snapshot? snapshot;
        try
        {
            snapshot = PsobbReader.ReadSnapshot(reader);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            snapshot = null;
        }
        snapshot = Augment(reader, snapshot);

        // 3 (health), counted first so the grace decision below can use it.
        if (snapshot is not null)
        {
            UnreadableFrames = 0;
            _graceStart = null;
        }
        else if (++UnreadableFrames == ReadFailureFrames)
        {
            var pid = (reader as IProcessMemoryReader)?.Pid;
            var err = (reader as IProcessMemoryReader)?.LastReadError;
            log?.Invoke(string.Format(CultureInfo.InvariantCulture, "attached but memory reads failing: pid {0} err {1} ({2}){3}",
                pid?.ToString(CultureInfo.InvariantCulture) ?? "NIL", err?.ToString(CultureInfo.InvariantCulture) ?? "NIL",
                err is { } e1 ? Win32ErrorLabel(e1) : "NIL", err == 5 ? " - run the client as administrator" : ""));
        }

        // Decision 2a: a transient unreadable stretch while the process lives
        // is not "game gone". Within the grace the frame is skipped entirely
        // (the detector, hooks and logs never see it; the previous snapshot is
        // kept so the diff after recovery spans the gap). The recorder still
        // gets its per-frame step so a capture keeps being driven.
        if (snapshot is null && _options.ReadGraceMs > 0)
        {
            var now = clock.Now;
            _graceStart ??= now;
            if (LispMath.ElapsedMs(now, _graceStart.Value, clock.TicksPerSecond) < _options.ReadGraceMs)
            {
                Guard(() => hooks.RecorderStep(detector.State, [], reader.WindowTitle));
                return new FrameResult(null, [], ReadFailing, InReadGrace: true);
            }
        }

        // 2. Detect.
        var runs = detector.Step(snapshot);
        // 4-7.
        Guard(() => hooks.RecorderStep(detector.State, runs, reader.WindowTitle));
        Guard(() => hooks.GhostFetch(snapshot));
        Guard(() => hooks.PinSetFetch(snapshot));
        Guard(() => hooks.GhostRaceStep(detector, snapshot));
        // 8.
        if (runs.Count > 0) Guard(() => hooks.CompletedRuns(runs));
        // 9.
        if (triggerLog is not null && _options.TriggerLogEnabled()) Guard(() => triggerLog.LogChanges(PreviousSnapshot, snapshot));
        // 10. Regardless of the log toggle: the rule dialog reads these any time.
        Guard(() => runLogs.UpdateLastKill(PreviousSnapshot, snapshot));
        Guard(() => runLogs.UpdateRunLogs(PreviousSnapshot, snapshot));
        PreviousSnapshot = snapshot;
        return new FrameResult(snapshot, runs, ReadFailing, InReadGrace: false);
    }

    /// <summary>win32.lisp:28 win32-error-label: a name for the GetLastError values the reader hits.</summary>
    public static string Win32ErrorLabel(int code) => code switch
    {
        0 => "no error",
        5 => "ERROR_ACCESS_DENIED",
        6 => "ERROR_INVALID_HANDLE",
        299 => "ERROR_PARTIAL_COPY",
        _ => "error",
    };

    private static T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke("poll frame hook failed: " + e.Message);
        }
    }
}

/// <summary>
/// main.lisp:104 log-account-mode: the recording-log line naming the account
/// verdict beside the colour it came from, written when the reading changes or
/// every 300 s (so it stays inside the 64 KB diagnostics tail).
/// </summary>
public sealed class AccountModeLogger
{
    /// <summary>main.lisp:101 +account-mode-log-interval+.</summary>
    public const long IntervalSeconds = 300;

    private (string? Mode, long? Color, long LoggedAt)? _last;

    /// <summary>The line to log for <paramref name="run"/> now (universal seconds), or null to stay quiet.</summary>
    public string? LineFor(Plist run, long nowUniversal)
    {
        var mode = run.Get("account-mode")?.AsString;
        var color = run.Get("my-name-color")?.AsLong;
        if (_last is { } last && last.Mode == mode && last.Color == color && nowUniversal - last.LoggedAt < IntervalSeconds) return null;
        _last = (mode, color, nowUniversal);
        var colorText = color is { } c ? c.ToString("X8", CultureInfo.InvariantCulture) : "?";
        return $"account-mode: {mode ?? "no verdict"} (name color {colorText})";
    }
}
