using System.Globalization;
using RappyRuns.Core.Media;

namespace RappyRuns.Win.Media;

/// <summary>
/// The live capture backend (<c>win32-ffmpeg-backend</c>, ffmpeg-win32.lisp:777-1026):
/// ffmpeg via CreateProcessW, WASAPI audio and WGC video served on named pipes,
/// ddagrab monitor resolution through DXGI, and the file operations.
/// </summary>
public sealed class Win32FfmpegBackend : ICaptureBackend
{
    /// <summary>+audio-drain-seconds+: after the audio EOF, ffmpeg's time to drain before "q".</summary>
    public const int AudioDrainSeconds = 3;

    /// <summary>+stderr-transcript-chars+: how much of a dead ffmpeg's stderr reaches the log.</summary>
    public const int StderrTranscriptChars = 8192;

    private readonly Func<nint> _findWindow;
    private readonly Func<bool> _wgcDisable;
    private readonly GdigrabProbe? _gdigrabProbe;

    /// <param name="findWindow"><c>find-psobb-window</c>: the PSOBB top-level window, or 0.</param>
    /// <param name="wgcDisable">The hidden :wgc-disable escape hatch.</param>
    /// <param name="gdigrabProbe">The gdigrab verdict source (null = never verified, ddagrab + crop).</param>
    public Win32FfmpegBackend(Func<nint> findWindow, Func<bool> wgcDisable, GdigrabProbe? gdigrabProbe)
    {
        _findWindow = findWindow;
        _wgcDisable = wgcDisable;
        _gdigrabProbe = gdigrabProbe;
    }

    /// <summary>
    /// <c>backend-wgc-capture</c> (ffmpeg-win32.lisp:832): WGC for a confidently
    /// WINDOWED game when supported and not disabled; a fullscreen game stays on
    /// ddagrab (nothing can overlap it and the QSV zero-copy chain applies).
    /// </summary>
    public WgcCapturePlan? WgcCapture()
    {
        if (!WgcSession.Available() || _wgcDisable()) return null;
        var hwnd = _findWindow();
        if (hwnd == 0 || WindowGeometry.WindowCoversMonitor(hwnd)) return null;
        var session = WgcSession.Start(hwnd);
        if (session is null) return null;
        var window = WindowGeometry.WindowRect(hwnd);
        var client = WindowGeometry.ClientScreenRect(hwnd);
        var crop = window is { } w && client is { } c ? CaptureGeometry.WgcCropRect(c, w, session.Width, session.Height) : null;
        RecordingLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"capturing via wgc {session.Width}x{session.Height}{(crop is { } cr ? " crop=" + cr : "")}"));
        return new WgcCapturePlan(session, session.PipeName, session.Width, session.Height, crop);
    }

    /// <summary><c>backend-capture-monitor</c> (ffmpeg-win32.lisp:799).</summary>
    public CaptureMonitor? CaptureMonitor()
    {
        CaptureMonitor? monitor = null;
        try
        {
            monitor = PsobbCaptureMonitor();
        }
        catch (Exception)
        {
            // ignore-errors: gdigrab.
        }
        if (monitor is not null)
            RecordingLog.Write(string.Create(CultureInfo.InvariantCulture,
                $"capturing via ddagrab output_idx={monitor.OutputIdx} ({monitor.Width}x{monitor.Height}){(monitor.Crop is { } c ? " crop=" + c : "")}"));
        return monitor;
    }

    /// <summary>
    /// <c>psobb-capture-monitor</c> (ffmpeg-win32.lisp:696): decision table rows
    /// 2-8 (media spec §1.4), each logged with the same text the Lisp writes.
    /// </summary>
    private CaptureMonitor? PsobbCaptureMonitor()
    {
        var hwnd = _findWindow();
        if (hwnd == 0)
        {
            RecordingLog.Write("capture check: no PSOBB window");
            return null;
        }
        if (WindowGeometry.WindowAndMonitor(hwnd) is not { } q)
        {
            RecordingLog.Write("capture check: window/monitor query failed");
            return null;
        }
        var covers = CaptureGeometry.RectCovers(q.Window, q.Monitor);
        (int Output, int Adapter)? found = null;
        try
        {
            found = DxgiOutputs.Find(q.Device);
        }
        catch (Exception)
        {
            // ignore-errors
        }
        RecordingLog.Write($"capture check: window={q.Window} monitor={q.Monitor} ({q.Device}) covers={Lisp.Bool(covers)} dxgi-idx={Lisp.Opt(found?.Output)} adapter={Lisp.Opt(found?.Adapter)}");
        if (found is not { } dxgi)
        {
            RecordingLog.Write("capture check: monitor unresolvable in DXGI, staying on gdigrab");
            return null;
        }
        if (covers)
            return new CaptureMonitor(dxgi.Output, dxgi.Adapter, q.Monitor.Width, q.Monitor.Height);
        var client = WindowGeometry.ClientScreenRect(hwnd);
        if (client is { } cl && CaptureGeometry.GdigrabVerdictUsable(_gdigrabProbe?.Verdict, hwnd, cl.Width, cl.Height))
        {
            RecordingLog.Write("capture check: windowed, probe-verified gdigrab (overlap-proof window capture)");
            return null;
        }
        var crop = client is { } c ? CaptureGeometry.CaptureCropRect(c, q.Monitor) : null;
        if (crop is null)
        {
            RecordingLog.Write($"capture check: unusable client rect {(client?.ToString() ?? "NIL")}, staying on gdigrab");
            return null;
        }
        return new CaptureMonitor(dxgi.Output, dxgi.Adapter, q.Monitor.Width, q.Monitor.Height, crop);
    }

    /// <summary>
    /// <c>backend-start-capture</c> (ffmpeg-win32.lisp:857): audio session first
    /// (its pipe must exist before ffmpeg opens inputs; any audio failure drops
    /// to video-only), then the spawn. A failed spawn stops and releases the
    /// adopted WGC session so its feeder never waits on a pipe forever.
    /// </summary>
    public CaptureStartResult StartCapture(string ffmpegPath, IReadOnlyList<string> args, string outputPath,
        string? audioPipe, int? audioPid, object? wgcSession)
    {
        var wgc = wgcSession as WgcSession;
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AudioSession? audio = null;
            if (audioPipe is not null)
            {
                try
                {
                    audio = AudioSession.Start(audioPipe, audioPid);
                }
                catch (Exception e)
                {
                    RecordingLog.Write($"audio session failed (video-only fallback), pid={Lisp.Opt(audioPid)}: {e.Message}");
                }
            }
            var finalArgs = audio is not null
                ? FfmpegArgs.RetargetAudioArgs(args, audio.SampleFormat, audio.SampleRate, audio.ChannelCount)
                : FfmpegArgs.StripAudioArgs(args, audioPipe);
            FfmpegProcess capture;
            try
            {
                capture = FfmpegProcess.Spawn(ffmpegPath, finalArgs, RecordingFiles.StderrFileFor(outputPath));
            }
            catch (Exception)
            {
                audio?.Stop();
                throw;
            }
            capture.Audio = audio;
            capture.Wgc = wgc;
            RecordingLog.Write("capture argv: " + CommandLine.Build(ffmpegPath, finalArgs));
            RecordingLog.Write($"capture started: audio={(audio is null ? "NIL" : audio.Scope.ToString().ToUpperInvariant())} wgc={Lisp.Bool(wgc is not null)}");
            return CaptureStartResult.Ok(capture);
        }
        catch (Exception e)
        {
            if (wgc is not null)
            {
                Try(wgc.Stop);
                Try(wgc.Close);
            }
            RecordingLog.Write($"capture start FAILED: {e.Message}\n  ffmpeg={ffmpegPath} output={outputPath} pid={Lisp.Opt(audioPid)}");
            return CaptureStartResult.Fail(e.Message);
        }
    }

    public bool IsAlive(ICaptureHandle capture) => ((FfmpegProcess)capture).IsAlive;

    public bool Succeeded(ICaptureHandle capture) => ((FfmpegProcess)capture).Succeeded;

    /// <summary><c>backend-start-remux</c>: stderr next to the final file.</summary>
    public CaptureStartResult StartRemux(string ffmpegPath, IReadOnlyList<string> args)
    {
        try
        {
            return CaptureStartResult.Ok(FfmpegProcess.Spawn(ffmpegPath, args, RecordingFiles.StderrFileFor(args[^1])));
        }
        catch (Exception e)
        {
            RecordingLog.Write("remux start FAILED: " + e.Message);
            return CaptureStartResult.Fail(e.Message);
        }
    }

    /// <summary>
    /// <c>backend-request-stop</c> (ffmpeg-win32.lisp:950): with audio, end the
    /// audio stream (pipe EOF) and send "q" only after a 3 s drain, off the
    /// poll thread - ffmpeg reads piped audio behind real time and "q" stops
    /// reading at once. Without audio, "q" now. WGC is not stopped here: ffmpeg
    /// exiting breaks its pipe and the feeder ends by itself.
    /// </summary>
    public void RequestStop(ICaptureHandle capture)
    {
        var process = (FfmpegProcess)capture;
        if (process.Audio is { } audio)
        {
            audio.Stop();
            var thread = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(AudioDrainSeconds));
                Try(process.WriteQuit);
            })
            {
                Name = "eta-ffmpeg-stop",
                IsBackground = true,
            };
            thread.Start();
        }
        else
            process.WriteQuit();
    }

    /// <summary><c>backend-kill-capture</c>: stop audio and WGC, TerminateProcess(1).</summary>
    public void Kill(ICaptureHandle capture)
    {
        var process = (FfmpegProcess)capture;
        process.Audio?.Stop();
        if (process.Wgc is { } wgc) Try(wgc.Stop);
        process.Terminate();
    }

    /// <summary>
    /// <c>backend-close-capture</c> (ffmpeg-win32.lisp:992), idempotent: stop
    /// audio, stop+close WGC (owned by this token), fold the stderr tail into
    /// the recording log, close the handles.
    /// </summary>
    public void Close(ICaptureHandle capture)
    {
        var process = (FfmpegProcess)capture;
        process.Audio?.Stop();
        var wgc = process.Wgc;
        process.Wgc = null;
        if (wgc is not null)
        {
            Try(wgc.Stop);
            Try(wgc.Close);
        }
        TranscribeStderr(process);
        process.CloseHandles();
    }

    /// <summary>
    /// <c>transcribe-capture-stderr</c>: the last 8192 chars, logged only when
    /// they hold something besides whitespace (a clean -loglevel error run
    /// leaves the file empty), then the file is deleted. Never throws.
    /// </summary>
    internal static void TranscribeStderr(FfmpegProcess process)
    {
        try
        {
            if (process.StderrPath is not { } path) return;
            var tail = RecordingLog.FileTail(path, StderrTranscriptChars);
            if (tail is not null && tail.Any(ch => ch > ' '))
                RecordingLog.Write($"ffmpeg stderr ({path}):\n{tail}");
            if (File.Exists(path)) File.Delete(path);
            process.StderrPath = null;
        }
        catch (Exception)
        {
            // Diagnostics, not control flow.
        }
    }

    /// <summary>uiop:rename-file-overwriting-target.</summary>
    public void RenameFile(string from, string to) => File.Move(from, to, overwrite: true);

    /// <summary>uiop:delete-file-if-exists.</summary>
    public void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary><c>backend-list-stale-files</c>: rec-tmp-*.mp4 in the folder.</summary>
    public IReadOnlyList<string> ListStaleFiles(string dir) =>
        Directory.Exists(dir)
            ? Mp4Files(dir).Where(p => Path.GetFileName(p).StartsWith("rec-tmp-", StringComparison.Ordinal)).ToList()
            : [];

    /// <summary><c>backend-list-recordings</c>: the kept (non rec-tmp) *.mp4 files with size and write date.</summary>
    public IReadOnlyList<RecordingFile> ListRecordings(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var files = new List<RecordingFile>();
        foreach (var path in Mp4Files(dir))
        {
            if (Path.GetFileNameWithoutExtension(path).StartsWith("rec-tmp-", StringComparison.Ordinal)) continue;
            long size = 0, date = 0;
            try
            {
                var info = new FileInfo(path);
                size = info.Length;
                date = (long)Math.Floor((new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) - Lisp.UniversalEpoch).TotalSeconds);
            }
            catch (Exception)
            {
                // (or ... 0), as the Lisp does.
            }
            files.Add(new RecordingFile(path, size, date));
        }
        return files;
    }

    /// <summary>
    /// *.mp4 with exactly that extension. Directory.EnumerateFiles("*.mp4")
    /// would also match ".mp4x" through 8.3 names; the Lisp matches the type
    /// exactly.
    /// </summary>
    private static IEnumerable<string> Mp4Files(string dir) =>
        Directory.EnumerateFiles(dir).Where(p => Path.GetExtension(p).Equals(".mp4", StringComparison.OrdinalIgnoreCase));

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // ignore-errors
        }
    }
}
