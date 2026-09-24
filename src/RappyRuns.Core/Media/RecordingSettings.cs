namespace RappyRuns.Core.Media;

/// <summary>
/// The config keys the recording pipeline reads (media spec §1.1,
/// config.lisp:13), as a snapshot the integrator builds from the config each
/// time the recorder asks. Defaults are the Lisp defaults.
/// </summary>
public sealed record RecordingSettings
{
    public static readonly RecordingSettings Default = new();

    /// <summary>:record-enabled - a forced key (no GUI, always on).</summary>
    public bool RecordEnabled { get; init; } = true;

    /// <summary>:tracking-only - record-only farming measurement suppresses recording.</summary>
    public bool TrackingOnly { get; init; }

    /// <summary>:record-audio.</summary>
    public bool RecordAudio { get; init; } = true;

    /// <summary>:hw-encode - use the probed GPU encoder.</summary>
    public bool HwEncode { get; init; } = true;

    /// <summary>:wgc-disable - hidden escape hatch (config.sexp only) back to the monitor paths.</summary>
    public bool WgcDisable { get; init; }

    /// <summary>
    /// <c>recording-enabled-p</c> (recording.lisp:1289): record-enabled and not
    /// tracking-only.
    /// </summary>
    public bool RecordingEnabled => RecordEnabled && !TrackingOnly;
}

/// <summary>What the hardware-encoder probe has found so far (<c>*hw-encoder-probe-state*</c>).</summary>
public enum HwProbeState
{
    /// <summary>NIL: the probe has not finished yet.</summary>
    NotRun,

    /// <summary>:done - a verdict, even a negative one.</summary>
    Done,

    /// <summary>
    /// :spawn-failed - ffmpeg never started for any candidate (Smart App
    /// Control's "Windows error 4551" in the field). Provisional: capture starts
    /// re-probe.
    /// </summary>
    SpawnFailed,
}

/// <summary>
/// The startup probe's shared verdict (<c>*hw-video-encoder*</c>,
/// <c>*hw-encoder-probe-state*</c>, <c>*hw-fullscreen-gpu-chain*</c>,
/// recording.lisp:180-201). Written by the probe thread, read at capture start,
/// so each field is a single volatile slot like the Lisp specials.
/// </summary>
public sealed class HwEncoderStatus
{
    private volatile string? _encoder;
    private volatile int _state;
    private volatile bool _gpuChain;

    /// <summary>The chosen encoder name, or null for libx264.</summary>
    public string? Encoder { get => _encoder; set => _encoder = value; }

    public HwProbeState State { get => (HwProbeState)_state; set => _state = (int)value; }

    /// <summary>The QSV zero-copy fullscreen chain was verified.</summary>
    public bool GpuChain { get => _gpuChain; set => _gpuChain = value; }

    /// <summary>
    /// The "hw encoder probe: ..." recording-log line (ffmpeg-win32.lisp:331).
    /// </summary>
    public static string ProbeLogLine(string? encoder, HwProbeState state) =>
        "hw encoder probe: using " + (encoder ?? "libx264 (no hardware encoder)") +
        (state == HwProbeState.SpawnFailed ? " (provisional - ffmpeg would not start)" : "");

    /// <summary>The "gpu chain probe: ..." recording-log line (ffmpeg-win32.lisp:336).</summary>
    public static string GpuChainLogLine(bool chain) =>
        "gpu chain probe: fullscreen captures " +
        (chain ? "stay on the GPU (hwmap -> vpp_qsv)" : "keep the hwdownload fallback");
}

/// <summary>
/// Machine facts the argv and the diagnostics depend on. The Win project
/// fills <see cref="PhysicalMemoryBytes"/> (GlobalMemoryStatusEx) and the OS
/// strings; null memory means unknown (never low-memory).
/// </summary>
public sealed record MachineInfo(long? PhysicalMemoryBytes, int LogicalProcessors, string? SoftwareType, string? SoftwareVersion)
{
    /// <summary><c>(round bytes 2^30)</c> - RAM in whole GiB (half to even), or null.</summary>
    public long? RamGb => PhysicalMemoryBytes is { } b ? Lisp.Round(b, 1L << 30) : null;

    public bool LowMemory => FfmpegArgs.LowMemoryMachine(PhysicalMemoryBytes);
}
