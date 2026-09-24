namespace RappyRuns.Core.Media;

/// <summary>An opaque running-ffmpeg token (a capture or a remux).</summary>
public interface ICaptureHandle;

/// <summary>A spawn's outcome: a handle, or null with the error string the GUI and tray show.</summary>
public readonly record struct CaptureStartResult(ICaptureHandle? Handle, string? Error)
{
    public static CaptureStartResult Ok(ICaptureHandle handle) => new(handle, null);

    public static CaptureStartResult Fail(string error) => new(null, error);
}

/// <summary>
/// The capture-backend protocol (recording.lisp:23-110): every side effect of
/// the recorder - spawning and stopping ffmpeg, renaming and deleting files,
/// resolving the capture source - goes through here, so <see cref="Recorder"/>
/// stays pure orchestration that tests drive with a mock. The live
/// implementation is <c>RappyRuns.Win.Media.Win32FfmpegBackend</c>.
/// </summary>
public interface ICaptureBackend
{
    /// <summary>
    /// Spawn the capture ffmpeg. With <paramref name="audioPipe"/> the backend
    /// serves <paramref name="audioPid"/>'s game audio there (retargeting or
    /// stripping the audio argv); with <paramref name="wgcSession"/> it adopts
    /// the WGC session - including on failure, when it must stop and release it.
    /// </summary>
    CaptureStartResult StartCapture(string ffmpegPath, IReadOnlyList<string> args, string outputPath,
        string? audioPipe, int? audioPid, object? wgcSession);

    bool IsAlive(ICaptureHandle capture);

    /// <summary>Ask ffmpeg to finish gracefully ("q" on stdin, after the audio drain).</summary>
    void RequestStop(ICaptureHandle capture);

    /// <summary>TerminateProcess.</summary>
    void Kill(ICaptureHandle capture);

    /// <summary>Release process/pipe handles once the process is dead. Idempotent.</summary>
    void Close(ICaptureHandle capture);

    /// <summary>Spawn the post-capture remux (<see cref="FfmpegArgs.BuildRemux"/>).</summary>
    CaptureStartResult StartRemux(string ffmpegPath, IReadOnlyList<string> args);

    /// <summary>Did the dead process exit with status 0?</summary>
    bool Succeeded(ICaptureHandle capture);

    /// <summary>Rename, overwriting the target.</summary>
    void RenameFile(string from, string to);

    /// <summary>Delete if it exists.</summary>
    void DeleteFile(string path);

    /// <summary>The monitor capture plan (ddagrab), or null for gdigrab. Queried once per capture start.</summary>
    CaptureMonitor? CaptureMonitor() => null;

    /// <summary>A ready WGC plan, or null. Queried first, once per capture start.</summary>
    WgcCapturePlan? WgcCapture() => null;

    /// <summary>Leftover rec-tmp-*.mp4 files in <paramref name="dir"/>.</summary>
    IReadOnlyList<string> ListStaleFiles(string dir);

    /// <summary>Kept (non rec-tmp) *.mp4 recordings in <paramref name="dir"/>.</summary>
    IReadOnlyList<RecordingFile> ListRecordings(string dir);
}
