using System.Globalization;
using System.Runtime.InteropServices;
using RappyRuns.Core.Media;

namespace RappyRuns.Win.Media;

/// <summary>
/// Window/monitor geometry queries for the capture-source decision
/// (ffmpeg-win32.lisp:356-568, 808-830).
/// <para>
/// DPI: the Lisp client declares no DPI awareness (no manifest entry), so on
/// a monitor scaled above 100 % its GetWindowRect / GetClientRect /
/// GetMonitorInfo returned 96-DPI-virtualized rects while ddagrab frames and
/// the WGC frame size are PHYSICAL pixels - its crop was only right at 100 %.
/// Every query here runs under DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 on
/// the calling thread (whatever the process or thread default is), so all
/// rects are physical and consistent with what ffmpeg and WGC see. At 100 %
/// (the field machines seen so far, e.g. the 5120x2160 dev monitor) the
/// numbers are identical to the Lisp; above it this is an intentional fix.
/// </para>
/// </summary>
internal static unsafe class WindowGeometry
{
    /// <summary>Run <paramref name="query"/> per-monitor-v2 aware, restoring the thread's context.</summary>
    public static T PhysicalPixels<T>(Func<T> query)
    {
        nint previous = 0;
        try
        {
            previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1607 Windows: whatever the process awareness is.
        }
        try
        {
            return query();
        }
        finally
        {
            if (previous != 0) NativeMethods.SetThreadDpiAwarenessContext(previous);
        }
    }

    private static ScreenRect ToRect(in NativeMethods.Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary><c>window-rect-of</c>: GetWindowRect, or null.</summary>
    public static ScreenRect? WindowRect(nint hwnd) =>
        PhysicalPixels(() => NativeMethods.GetWindowRect(hwnd, out var r) ? ToRect(r) : (ScreenRect?)null);

    /// <summary>
    /// <c>window-client-screen-rect</c> (ffmpeg-win32.lisp:554): the client area
    /// in screen coordinates (what gdigrab title= captures), or null.
    /// </summary>
    public static ScreenRect? ClientScreenRect(nint hwnd) =>
        PhysicalPixels(() =>
        {
            if (!NativeMethods.GetClientRect(hwnd, out var rect)) return (ScreenRect?)null;
            var point = new NativeMethods.Point();
            if (!NativeMethods.ClientToScreen(hwnd, ref point)) return null;
            return new ScreenRect(point.X, point.Y, point.X + rect.Right, point.Y + rect.Bottom);
        });

    /// <summary>The window rect, its monitor's rect and the monitor's GDI device name (\\.\DISPLAYn); null when a query fails.</summary>
    public static (ScreenRect Window, ScreenRect Monitor, string Device)? WindowAndMonitor(nint hwnd) =>
        PhysicalPixels(() =>
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
            var info = new NativeMethods.MonitorInfoExW { CbSize = (uint)sizeof(NativeMethods.MonitorInfoExW) };
            if (monitor == 0 || !NativeMethods.GetWindowRect(hwnd, out var window) || !NativeMethods.GetMonitorInfo(monitor, ref info))
                return ((ScreenRect, ScreenRect, string)?)null;
            var device = new string(info.Device, 0, 32);
            var nul = device.IndexOf('\0', StringComparison.Ordinal);
            if (nul >= 0) device = device[..nul];
            return (ToRect(window), ToRect(info.Monitor), device);
        });

    /// <summary>
    /// <c>window-covers-monitor-p</c> (ffmpeg-win32.lisp:814): the window spans
    /// its monitor - AND true when the queries fail, because "windowed" licenses
    /// the WGC path and an unknown state must keep the monitor capture.
    /// </summary>
    public static bool WindowCoversMonitor(nint hwnd) =>
        WindowAndMonitor(hwnd) is not { } q || CaptureGeometry.RectCovers(q.Window, q.Monitor);

    /// <summary>
    /// <c>psobb-window-probe-key</c> (ffmpeg-win32.lisp:596): the window's
    /// identity for the gdigrab verdict - hwnd address and client size - or
    /// null when absent or degenerate (under 64 px).
    /// </summary>
    public static (long Hwnd, int Width, int Height)? ProbeKey(nint hwnd)
    {
        if (hwnd == 0) return null;
        if (ClientScreenRect(hwnd) is not { } client) return null;
        if (client.Width < CaptureGeometry.CropMinPixels || client.Height < CaptureGeometry.CropMinPixels) return null;
        return (hwnd, client.Width, client.Height);
    }
}

/// <summary>
/// <c>dxgi-output-index-for-device</c> (ffmpeg-win32.lisp:499): the DXGI output
/// whose DeviceName equals the monitor's GDI device name, searched over EVERY
/// adapter (up to 8 x 8). NEVER guess DISPLAYn -> n-1: that guess recorded a
/// neighboring monitor (someone's Discord) in run 1047. The output index is
/// adapter-relative - what ddagrab's output_idx means on that adapter's device.
/// </summary>
internal static unsafe class DxgiOutputs
{
    /// <summary>IID_IDXGIFactory - not Factory1: one field machine's CreateDXGIFactory1 refused the Factory1 IID.</summary>
    private static readonly Guid IidIDXGIFactory = new("7B7166EC-21C7-44AE-B21A-C9AE321AE369");

    private const int MaxAdapters = 8;
    private const int MaxOutputs = 8;

    /// <summary>DXGI_OUTPUT_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc
    {
        public fixed char DeviceName[32];
        public NativeMethods.Rect DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }

    /// <summary>(output index, adapter index), or null - after logging the outputs that WERE there.</summary>
    public static (int Output, int Adapter)? Find(string deviceName)
    {
        if (NativeMethods.CreateDXGIFactory1(IidIDXGIFactory, out var factory) != 0 || factory == 0) return null;
        var seen = new List<string>();
        try
        {
            for (var adapterIndex = 0; adapterIndex < MaxAdapters; adapterIndex++)
            {
                if (EnumChild(factory, (uint)adapterIndex, out var adapter) != 0 || adapter == 0) break;
                try
                {
                    for (var index = 0; index < MaxOutputs; index++)
                    {
                        if (EnumChild(adapter, (uint)index, out var output) != 0 || output == 0) break;
                        try
                        {
                            OutputDesc desc;
                            if (((delegate* unmanaged[Stdcall]<nint, OutputDesc*, int>)Com.Method(output, 7))(output, &desc) == 0)
                            {
                                var name = new string(desc.DeviceName);
                                seen.Add(string.Create(CultureInfo.InvariantCulture, $"{name}[adapter {adapterIndex}]"));
                                if (name == deviceName) return (index, adapterIndex);
                            }
                        }
                        finally
                        {
                            Com.Release(output);
                        }
                    }
                }
                finally
                {
                    Com.Release(adapter);
                }
            }
            RecordingLog.Write($"capture check: no DXGI output named {Lisp.Prin1(deviceName)} (saw {string.Join(", ", seen.Select(Lisp.Prin1))})");
            return null;
        }
        finally
        {
            Com.Release(factory);
        }
    }

    /// <summary>EnumAdapters / EnumOutputs: both vtable slot 7 (after IUnknown 0-2 and IDXGIObject 3-6).</summary>
    private static int EnumChild(nint parent, uint index, out nint child)
    {
        nint c = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Com.Method(parent, 7))(parent, index, &c);
        child = c;
        return hr;
    }
}
