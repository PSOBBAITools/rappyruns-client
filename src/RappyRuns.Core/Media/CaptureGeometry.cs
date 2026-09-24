namespace RappyRuns.Core.Media;

/// <summary>
/// A screen rectangle as the Lisp client passes it around: (left top right
/// bottom). The live side fills it from GetWindowRect / GetClientRect +
/// ClientToScreen / MONITORINFO in PHYSICAL pixels (see
/// <c>RappyRuns.Win.Media.CaptureMonitorResolver</c> on DPI).
/// </summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>The Lisp list print the recording log shows: "(0 0 1920 1080)".</summary>
    public override string ToString() => Lisp.List(Left, Top, Right, Bottom);
}

/// <summary>An ffmpeg crop: (x y width height), relative to the frame it cuts.</summary>
public readonly record struct CropRect(int X, int Y, int Width, int Height)
{
    /// <summary>The Lisp list print the recording log shows: "(160 90 1600 900)".</summary>
    public override string ToString() => Lisp.List(X, Y, Width, Height);
}

/// <summary>
/// The monitor the game window sits on, for a ddagrab capture
/// (<c>backend-capture-monitor</c>, recording.lisp:64): the adapter-relative
/// DXGI output index, the adapter (null = a legacy plist without :adapter,
/// treated like 0), the monitor's pixel size and, for a windowed game, the
/// client area as a monitor-relative crop.
/// </summary>
public sealed record CaptureMonitor(int OutputIdx, int? Adapter, int Width, int Height, CropRect? Crop = null);

/// <summary>
/// A ready-to-feed Windows.Graphics.Capture plan (<c>backend-wgc-capture</c>,
/// recording.lisp:87): the pipe the frames arrive on, the whole-window frame
/// size, and the client-area crop. <see cref="Session"/> is opaque to the
/// recorder; it is handed to <see cref="ICaptureBackend.StartCapture"/>, which
/// owns it from then on.
/// </summary>
public sealed record WgcCapturePlan(object? Session, string Pipe, int Width, int Height, CropRect? Crop = null);

/// <summary>The gdigrab probe's outcome for one window (ffmpeg-win32.lisp:578).</summary>
public enum GdigrabResult
{
    Usable,
    Black,
    Failed,
}

/// <summary>
/// A gdigrab probe verdict keyed to the window it was taken on:
/// (:hwnd address :size (w h) :result r).
/// </summary>
public sealed record GdigrabVerdict(long Hwnd, int Width, int Height, GdigrabResult Result);

/// <summary>Pure rect math of the capture-source decision (media spec §1.4.1).</summary>
public static class CaptureGeometry
{
    /// <summary>+capture-crop-min-pixels+ (recording.lisp:588): smallest crop side worth recording.</summary>
    public const int CropMinPixels = 64;

    /// <summary>
    /// <c>rect-covers-p</c> (recording.lisp:578): the window contains the whole
    /// monitor - the fullscreen/borderless test. A maximized window stops at the
    /// work area above the taskbar and is NOT fullscreen.
    /// </summary>
    public static bool RectCovers(ScreenRect window, ScreenRect monitor) =>
        window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    /// <summary>
    /// <c>capture-crop-rect</c> (recording.lisp:593): where the client area
    /// sits on its monitor, clamped to the monitor and floored to even sizes
    /// (yuv420p), or null when the visible part is under 64 px a side.
    /// </summary>
    public static CropRect? CaptureCropRect(ScreenRect client, ScreenRect monitor)
    {
        var left = Math.Max(client.Left, monitor.Left);
        var top = Math.Max(client.Top, monitor.Top);
        var width = 2 * (int)Lisp.Floor(Math.Min(client.Right, monitor.Right) - left, 2);
        var height = 2 * (int)Lisp.Floor(Math.Min(client.Bottom, monitor.Bottom) - top, 2);
        if (width >= CropMinPixels && height >= CropMinPixels)
            return new CropRect(left - monitor.Left, top - monitor.Top, width, height);
        return null;
    }

    /// <summary>
    /// <c>wgc-crop-rect</c> (recording.lisp:611): the client area inside a WGC
    /// whole-window frame (which carries caption and borders), bounded by the
    /// frame size and floored even; null when degenerate (then the whole frame
    /// is recorded).
    /// </summary>
    public static CropRect? WgcCropRect(ScreenRect client, ScreenRect window, int frameWidth, int frameHeight)
    {
        var x = Math.Max(0, client.Left - window.Left);
        var y = Math.Max(0, client.Top - window.Top);
        var width = 2 * (int)Lisp.Floor(Math.Min(client.Right - client.Left, frameWidth - x), 2);
        var height = 2 * (int)Lisp.Floor(Math.Min(client.Bottom - client.Top, frameHeight - y), 2);
        if (width >= CropMinPixels && height >= CropMinPixels)
            return new CropRect(x, y, width, height);
        return null;
    }

    /// <summary>
    /// <c>capture-secondary-adapter</c> (recording.lisp:634): the adapter index
    /// when it is not the default one, else null. Adapter 0 and a missing
    /// adapter must keep the probe-verified argv byte for byte.
    /// </summary>
    public static int? SecondaryAdapter(CaptureMonitor? monitor) =>
        monitor?.Adapter is > 0 and var adapter ? adapter : null;

    /// <summary>
    /// <c>gdigrab-verdict-usable-p</c> (recording.lisp:687): the verdict proves
    /// gdigrab works for THIS window (same hwnd and client size) and said
    /// usable. Anything else - stale, black, failed, none - keeps ddagrab.
    /// </summary>
    public static bool GdigrabVerdictUsable(GdigrabVerdict? verdict, long hwnd, int clientWidth, int clientHeight) =>
        verdict is not null && verdict.Hwnd == hwnd &&
        verdict.Width == clientWidth && verdict.Height == clientHeight &&
        verdict.Result == GdigrabResult.Usable;

    /// <summary>
    /// <c>blackdetect-reports-black-p</c> (recording.lisp:681): a probe's stderr
    /// reported a black interval. Null stderr is NOT black - the caller must
    /// fail it instead.
    /// </summary>
    public static bool BlackdetectReportsBlack(string? stderr) =>
        stderr is not null && stderr.Contains("black_start", StringComparison.Ordinal);
}
