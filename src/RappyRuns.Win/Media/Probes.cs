using System.Diagnostics;
using System.Globalization;
using RappyRuns.Core.Media;

namespace RappyRuns.Win.Media;

/// <summary>
/// The gdigrab window probe (media spec §1.4.3, ffmpeg-win32.lisp:570-694):
/// only on machines WITHOUT WGC, and only while the player idles outside a
/// quest, spawn a few gdigrab frames through blackdetect to learn whether GDI
/// can read THIS window. A usable verdict routes a windowed capture to
/// overlap-proof gdigrab; black/failed/stale verdicts keep ddagrab + crop.
/// </summary>
public sealed class GdigrabProbe
{
    /// <summary>+gdigrab-probe-min-interval-seconds+.</summary>
    public const int MinIntervalSeconds = 10;

    /// <summary>+hw-probe-timeout-seconds+ (shared with the encoder probe).</summary>
    public const int TimeoutSeconds = 15;

    private readonly Func<nint> _findWindow;
    private readonly Func<string> _ffmpegPath;
    private readonly Func<bool> _wgcDisable;
    private readonly Func<bool> _wgcAvailable;
    private volatile GdigrabVerdict? _verdict;
    private Thread? _worker;
    private long? _lastStart;

    public GdigrabProbe(Func<nint> findWindow, Func<string> ffmpegPath, Func<bool> wgcDisable, Func<bool>? wgcAvailable = null)
    {
        _findWindow = findWindow;
        _ffmpegPath = ffmpegPath;
        _wgcDisable = wgcDisable;
        _wgcAvailable = wgcAvailable ?? WgcSession.Available;
    }

    /// <summary><c>*gdigrab-probe-verdict*</c>: the latest result, or null before the first probe.</summary>
    public GdigrabVerdict? Verdict => _verdict;

    /// <summary>
    /// <c>maybe-start-gdigrab-probe</c> (ffmpeg-win32.lisp:658). Called from the
    /// poll loop's 4 Hz slot only while attached, recording-enabled and NOT
    /// busy. A no-op on WGC machines, while a probe runs, when the verdict
    /// already matches the window (any result), or within 10 s of the last start.
    /// </summary>
    public void MaybeStart(string? windowTitle)
    {
        if (windowTitle is null) return;
        if (_wgcAvailable() && !_wgcDisable()) return;
        if (_worker is { IsAlive: true }) return;
        if (WindowGeometry.ProbeKey(_findWindow()) is not { } key) return;
        var verdict = _verdict;
        if (verdict is not null && verdict.Hwnd == key.Hwnd && verdict.Width == key.Width && verdict.Height == key.Height) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastStart is { } last && now - last < MinIntervalSeconds * Stopwatch.Frequency) return;
        _lastStart = now;
        _worker = new Thread(() =>
        {
            var result = Probe(windowTitle);
            _verdict = new GdigrabVerdict(key.Hwnd, key.Width, key.Height, result);
            RecordingLog.Write(string.Create(CultureInfo.InvariantCulture,
                $"gdigrab probe: window {key.Hwnd} {Lisp.List(key.Width, key.Height)} -> {result.ToString().ToUpperInvariant()}"));
        })
        {
            Name = "eta-gdigrab-probe",
            IsBackground = true,
        };
        _worker.Start();
    }

    /// <summary>
    /// <c>probe-gdigrab-window</c> (ffmpeg-win32.lisp:612): exit != 0 -> failed;
    /// unreadable stderr -> failed ("not proven black" is never "proven
    /// usable"); black_start -> black; else usable. The stderr is the probe's
    /// data, not an error transcript: never folded into the log, deleted here.
    /// </summary>
    internal GdigrabResult Probe(string windowTitle)
    {
        var stderrPath = Path.Combine(Path.GetTempPath(), "eta-gdigrab-probe-stderr.txt");
        FfmpegProcess process;
        try
        {
            process = FfmpegProcess.Spawn(_ffmpegPath(), FfmpegArgs.GdigrabProbeArgs(windowTitle), stderrPath);
        }
        catch (Exception e)
        {
            RecordingLog.Write("gdigrab probe failed to spawn: " + e.Message);
            return GdigrabResult.Failed;
        }
        try
        {
            WaitOrKill(process, TimeoutSeconds);
            var ok = process.Succeeded;
            var stderr = RecordingLog.FileTail(stderrPath, Win32FfmpegBackend.StderrTranscriptChars);
            if (!ok) return GdigrabResult.Failed;
            if (stderr is null) return GdigrabResult.Failed;
            return CaptureGeometry.BlackdetectReportsBlack(stderr) ? GdigrabResult.Black : GdigrabResult.Usable;
        }
        finally
        {
            if (process.IsAlive) process.Terminate();
            process.StderrPath = null;
            process.CloseHandles();
            try
            {
                if (File.Exists(stderrPath)) File.Delete(stderrPath);
            }
            catch (Exception)
            {
                // uiop:delete-file-if-exists; best effort.
            }
        }
    }

    /// <summary><c>wait-for-capture</c> for a probe: wait up to the timeout, then kill.</summary>
    internal static void WaitOrKill(FfmpegProcess process, int timeoutSeconds)
    {
        if (!process.WaitForExit((uint)(timeoutSeconds * 1000)) && process.IsAlive)
            process.Terminate();
        process.WaitForExit(1000);
    }
}

/// <summary>
/// The hardware-encoder probe (media spec §1.5, ffmpeg-win32.lisp:245-337):
/// once at startup (and again at capture start while the verdict is
/// :spawn-failed), try h264_nvenc, h264_amf, h264_qsv on 8 black frames; the
/// first to exit 0 wins. A QSV winner is probed further for the zero-copy
/// fullscreen chain.
/// </summary>
public sealed class HwEncoderProbe
{
    private readonly HwEncoderStatus _status;
    private readonly Func<string> _ffmpegPath;
    private readonly object _gate = new();
    private Thread? _worker;

    public HwEncoderProbe(HwEncoderStatus status, Func<string> ffmpegPath)
    {
        _status = status;
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// <c>start-hw-encoder-probe</c>: probe on a worker thread and publish the
    /// result; a no-op while one is already running. Captures before it lands
    /// use libx264.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_worker is { IsAlive: true }) return;
            _worker = new Thread(Run) { Name = "eta-hw-encoder-probe", IsBackground = true };
            _worker.Start();
        }
    }

    /// <summary>Wait for a running probe (tests, smoke checks).</summary>
    public bool Join(TimeSpan timeout) => _worker?.Join(timeout) ?? true;

    private void Run()
    {
        string? encoder;
        HwProbeState state;
        try
        {
            (encoder, state) = ProbeEncoder();
        }
        catch (Exception e)
        {
            // No ffmpeg at all lands here: treat it like a spawn failure so a
            // later repair is picked up by the capture-start retry.
            RecordingLog.Write("hw encoder probe failed: " + e.Message);
            (encoder, state) = (null, HwProbeState.SpawnFailed);
        }
        _status.Encoder = encoder;
        _status.State = state;
        RecordingLog.Write(HwEncoderStatus.ProbeLogLine(encoder, state));
        if (encoder == "h264_qsv")
        {
            _status.GpuChain = ProbeGpuChain();
            RecordingLog.Write(HwEncoderStatus.GpuChainLogLine(_status.GpuChain));
        }
    }

    /// <summary>
    /// <c>probe-hw-encoder</c>: the first candidate that encodes, with :done when
    /// ffmpeg ran at least once (so null really means "no hardware encoder"),
    /// :spawn-failed when it never started (e.g. Smart App Control, error 4551).
    /// </summary>
    private (string? Encoder, HwProbeState State) ProbeEncoder()
    {
        var ffmpeg = _ffmpegPath();
        var spawned = false;
        foreach (var candidate in FfmpegArgs.HwEncoderCandidates)
        {
            FfmpegProcess process;
            try
            {
                process = FfmpegProcess.Spawn(ffmpeg, FfmpegArgs.HwEncoderProbeArgs(candidate));
            }
            catch (Exception e)
            {
                RecordingLog.Write($"hw encoder probe {candidate} failed to spawn: {e.Message}");
                continue;
            }
            spawned = true;
            try
            {
                GdigrabProbe.WaitOrKill(process, GdigrabProbe.TimeoutSeconds);
                if (process.Succeeded) return (candidate, HwProbeState.Done);
            }
            finally
            {
                process.CloseHandles();
            }
        }
        return (null, spawned ? HwProbeState.Done : HwProbeState.SpawnFailed);
    }

    /// <summary><c>probe-hw-gpu-chain</c>: the ddagrab -> hwmap -> vpp_qsv -> h264_qsv chain exits 0.</summary>
    private bool ProbeGpuChain()
    {
        try
        {
            var process = FfmpegProcess.Spawn(_ffmpegPath(), FfmpegArgs.HwGpuChainProbeArgs());
            try
            {
                GdigrabProbe.WaitOrKill(process, GdigrabProbe.TimeoutSeconds);
                return process.Succeeded;
            }
            finally
            {
                process.CloseHandles();
            }
        }
        catch (Exception e)
        {
            RecordingLog.Write("gpu chain probe failed to spawn: " + e.Message);
            return false;
        }
    }
}

/// <summary>
/// Machine facts for the argv profile, the "session:" log line and the
/// diagnostics report (<c>physical-memory-bytes</c>, <c>logical-processor-count</c>,
/// <c>software-type</c>/<c>software-version</c>).
/// </summary>
public static class MachineProbe
{
    /// <summary>GlobalMemoryStatusEx.ullTotalPhys, or null when the call fails.</summary>
    public static long? PhysicalMemoryBytes()
    {
        try
        {
            var status = new NativeMethods.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
            return NativeMethods.GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The machine summary. The OS strings imitate what LispWorks printed on the
    /// field machines ("Windows NT", "Windows 11: 10.0 (build 26200) ") so log
    /// tails read the same; they are diagnostics only.
    /// </summary>
    public static MachineInfo Current()
    {
        var v = Environment.OSVersion.Version;
        var name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : v.Major == 10 ? "Windows 10" : "Windows";
        return new MachineInfo(
            PhysicalMemoryBytes(),
            FfmpegArgs.LogicalProcessorCount(),
            "Windows NT",
            string.Create(CultureInfo.InvariantCulture, $"{name}: {v.Major}.{v.Minor} (build {v.Build}) "));
    }
}
