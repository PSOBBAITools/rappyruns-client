using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using RappyRuns.Core.Media;

namespace RappyRuns.Win.Media;

/// <summary>
/// Windows.Graphics.Capture window capture (media spec §1.15, wgc-win32.lisp):
/// the overlap-proof, flip-model-proof way to record a windowed game. ffmpeg
/// has no WGC input, so this captures the window itself and serves raw BGRA
/// frames at a constant 30 fps on <c>\\.\pipe\ephinea-ta-video</c>.
/// <para>
/// Everything is raw WinRT-over-COM through vtable slots - no delegates, no
/// projections: the frame pool is CreateFreeThreaded and polled with
/// TryGetNextFrame from the feeder thread, exactly the shape validated in the
/// field. WinRT vtables start after IInspectable's six slots.
/// </para>
/// </summary>
public sealed unsafe class WgcSession
{
    private static readonly Guid IidItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidPoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid IidSessionStatics = new("2224A540-5974-49AA-B232-0882536F4CB5");
    private static readonly Guid IidSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid IidSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid IidClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IidWinRtDevice = new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");
    private static readonly Guid IidDxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const int PixelFormatB8G8R8A8 = 87;

    private static readonly object SupportGate = new();
    private static bool? _supported;

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public byte* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    private nint _device;
    private nint _context;
    private nint _winrtInspectable;
    private nint _winrtDevice;
    private nint _item;
    private nint _pool;
    private nint _session;
    private nint _staging;
    private byte* _frameBuffer;
    private OutboundPipe? _pipe;
    private Thread? _thread;
    private volatile bool _stop;

    private WgcSession(int framerate) => Framerate = framerate;

    public string PipeName => FfmpegArgs.VideoPipeName;

    /// <summary>Whole-window frame size the pipe carries.</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Framerate { get; }

    public long? ConnectMs { get; private set; }

    public long FramesWritten { get; private set; }

    public long FramesFresh { get; private set; }

    public string? Error { get; private set; }

    /// <summary>
    /// <c>wgc-available-p</c> (wgc-win32.lisp:227): the WGC activation
    /// factories exist and GraphicsCaptureSession.IsSupported() is true.
    /// Cached for the process; never throws (any surprise = unsupported).
    /// </summary>
    public static bool Available()
    {
        lock (SupportGate)
        {
            if (_supported is null)
            {
                bool yes;
                try
                {
                    NativeMethods.RoInitialize(1); // RO_INIT_MULTITHREADED; idempotent
                    var statics = ActivationFactory("Windows.Graphics.Capture.GraphicsCaptureSession", IidSessionStatics);
                    var interop = ActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", IidItemInterop);
                    try
                    {
                        byte supported = 0;
                        yes = statics != 0 && interop != 0 &&
                              ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Com.Method(statics, 6))(statics, &supported) == 0 &&
                              supported != 0;
                    }
                    finally
                    {
                        Com.Release(statics);
                        Com.Release(interop);
                    }
                }
                catch (Exception e)
                {
                    RecordingLog.Write("wgc probe failed: " + e.Message);
                    yes = false;
                }
                _supported = yes;
                RecordingLog.Write("wgc support: " + (yes ? "YES" : "NO"));
            }
            return _supported.Value;
        }
    }

    /// <summary>
    /// <c>start-wgc-session</c> (wgc-win32.lisp:309): D3D device, capture item,
    /// free-threaded pool, session, staging texture, zeroed frame buffer, the
    /// pipe and the feeder thread - WITHOUT starting the capture (the thread
    /// does once ffmpeg connects, so video time 0 is the first delivered
    /// frame). Everything that can fail fails here, at capture start. Null with
    /// the reason logged; the caller falls back to the monitor paths.
    /// </summary>
    public static WgcSession? Start(nint hwnd, int framerate = FfmpegArgs.Framerate)
    {
        try
        {
            NativeMethods.RoInitialize(1);
            var hr = NativeMethods.D3D11CreateDevice(0, 1 /* HARDWARE */, 0, 0x20 /* BGRA_SUPPORT */, 0, 0, 7, out var device, 0, out var context);
            if (hr != 0) throw new InvalidOperationException($"D3D11CreateDevice hr={hr:X}");
            var session = new WgcSession(framerate) { _device = device, _context = context };
            try
            {
                session.Build(hwnd);
                return session;
            }
            catch (Exception)
            {
                session.Close();
                throw;
            }
        }
        catch (Exception e)
        {
            RecordingLog.Write("wgc session failed (falling back): " + e.Message);
            return null;
        }
    }

    private void Build(nint hwnd)
    {
        var dxgi = Com.Query(_device, IidDxgiDevice);
        if (dxgi == 0) throw new InvalidOperationException("QI IDXGIDevice failed");
        try
        {
            var hr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out _winrtInspectable);
            if (hr != 0) throw new InvalidOperationException($"CreateDirect3D11Device hr={hr:X}");
        }
        finally
        {
            Com.Release(dxgi);
        }
        _winrtDevice = Com.Query(_winrtInspectable, IidWinRtDevice);
        if (_winrtDevice == 0) throw new InvalidOperationException("QI IDirect3DDevice failed");

        var interop = ActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", IidItemInterop);
        if (interop == 0) throw new InvalidOperationException("no GraphicsCaptureItem factory");
        try
        {
            nint item;
            var iid = IidItem;
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Com.Method(interop, 3))(interop, hwnd, &iid, &item);
            if (hr != 0) throw new InvalidOperationException($"CreateForWindow hr={hr:X}");
            _item = item;
        }
        finally
        {
            Com.Release(interop);
        }

        long size;
        var hrSize = ((delegate* unmanaged[Stdcall]<nint, long*, int>)Com.Method(_item, 7))(_item, &size);
        if (hrSize != 0) throw new InvalidOperationException($"get_Size hr={hrSize:X}");
        var width = (int)(size & 0xFFFFFFFF);
        var height = (int)(size >> 32);
        if (width < CaptureGeometry.CropMinPixels || height < CaptureGeometry.CropMinPixels)
            throw new InvalidOperationException($"window too small for capture: {width}x{height}");
        Width = width;
        Height = height;

        var statics = ActivationFactory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IidPoolStatics2);
        if (statics == 0) throw new InvalidOperationException("no FramePool factory");
        try
        {
            nint pool;
            var packed = (long)(uint)width | ((long)(uint)height << 32); // SizeInt32 by value
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, int, int, long, nint*, int>)Com.Method(statics, 6))(
                statics, _winrtDevice, PixelFormatB8G8R8A8, 2, packed, &pool);
            if (hr != 0) throw new InvalidOperationException($"CreateFreeThreaded hr={hr:X}");
            _pool = pool;
        }
        finally
        {
            Com.Release(statics);
        }

        nint captureSession;
        var hrSession = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Com.Method(_pool, 10))(_pool, _item, &captureSession);
        if (hrSession != 0) throw new InvalidOperationException($"CreateCaptureSession hr={hrSession:X}");
        _session = captureSession;

        // Cosmetics, both optional: no cursor (parity with draw_mouse=0), and
        // no on-screen border where the OS lets an unpackaged app drop it.
        PutBoolIfSupported(_session, IidSession2, 7, false);
        PutBoolIfSupported(_session, IidSession3, 7, false);

        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = PixelFormatB8G8R8A8,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = 3,                 // STAGING
            BindFlags = 0,
            CpuAccessFlags = 0x20000,  // READ
            MiscFlags = 0,
        };
        nint staging;
        var hrTex = ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Com.Method(_device, 5))(_device, &desc, 0, &staging);
        if (hrTex != 0) throw new InvalidOperationException($"CreateTexture2D(staging) hr={hrTex:X}");
        _staging = staging;
        // Zero-filled: a late first delivery sends black, never garbage.
        _frameBuffer = (byte*)NativeMemory.AllocZeroed((nuint)width * (nuint)height * 4);

        _pipe = OutboundPipe.Create(FfmpegArgs.VideoPipeName, 4 * 1024 * 1024)
            ?? throw new InvalidOperationException($"CreateNamedPipe(video) failed ({OutboundPipe.LastCreateError})");

        _thread = new Thread(() =>
        {
            var co = NativeMethods.CoInitializeEx(0, NativeMethods.CoinitMultithreaded);
            try
            {
                CaptureLoop();
            }
            catch (Exception e)
            {
                Error = e.Message;
                RecordingLog.Write("wgc capture loop died: " + e.Message);
                _pipe?.Close(flush: false);
            }
            finally
            {
                if (co >= 0) NativeMethods.CoUninitialize();
            }
        })
        {
            Name = "eta-wgc-capture",
            IsBackground = true,
        };
        _thread.Start();
        RecordingLog.Write(string.Create(CultureInfo.InvariantCulture, $"wgc session ready: {width}x{height} @{Framerate}"));
    }

    /// <summary>
    /// <c>wgc-capture-loop</c> (wgc-win32.lisp:528): wait for ffmpeg, StartCapture,
    /// wait up to 1 s for the first frame, then one frame per 1/30 s - fresh
    /// when the window changed, the previous one again otherwise (rawvideo
    /// needs constant cadence). More than a second behind resets the deadline
    /// instead of bursting to catch up. Closing the pipe is ffmpeg's video EOF.
    /// </summary>
    private void CaptureLoop()
    {
        var connectEpoch = Stopwatch.GetTimestamp();
        if (!_pipe!.Connect())
            throw new InvalidOperationException("ffmpeg never opened the video pipe");
        ConnectMs = Lisp.Round(1000 * (Stopwatch.GetTimestamp() - connectEpoch), Stopwatch.Frequency);
        var hr = Com.CallNoArgs(_session, 6); // StartCapture
        if (hr != 0) throw new InvalidOperationException($"StartCapture hr={hr:X}");
        for (var i = 0; i < 100 && !_stop && !DrainLatestFrame(); i++)
            Thread.Sleep(10);
        ReadStaging();
        var freq = Stopwatch.Frequency;
        // Exact rational ticks per frame (the Lisp keeps a ratio); track the
        // deadline in frame-scaled units so no rounding drift accumulates.
        long deadlineScaled = Stopwatch.GetTimestamp() * Framerate;
        var frameBytes = Width * Height * 4;
        while (!_stop)
        {
            if (DrainLatestFrame())
            {
                ReadStaging();
                FramesFresh++;
            }
            if (!_pipe.Write(_frameBuffer, frameBytes)) break;
            FramesWritten++;
            deadlineScaled += freq;
            var nowScaled = Stopwatch.GetTimestamp() * Framerate;
            if (deadlineScaled > nowScaled)
            {
                var ms = (deadlineScaled - nowScaled) * 1000 / ((long)Framerate * freq);
                if (ms > 0) Thread.Sleep((int)ms);
                else Thread.Yield();
            }
            else if (nowScaled - deadlineScaled > freq * Framerate)
            {
                deadlineScaled = nowScaled;
            }
        }
        _pipe.Close(flush: false);
        RecordingLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"wgc capture ended: {FramesWritten} frames ({FramesFresh} fresh), connect {Lisp.Opt(ConnectMs)} ms"));
    }

    /// <summary>
    /// <c>wgc-drain-latest-frame</c>: copy the newest pending frame into the
    /// staging texture; true when anything new arrived. Every COM temporary is
    /// released - the pool holds only 2 buffers and starves on a leak.
    /// </summary>
    private bool DrainLatestFrame()
    {
        var fresh = false;
        while (true)
        {
            if (Com.GetObject(_pool, 7, out var frame) != 0 || frame == 0) return fresh; // TryGetNextFrame
            try
            {
                if (Com.GetObject(frame, 6, out var surface) == 0 && surface != 0) // get_Surface
                {
                    try
                    {
                        var access = Com.Query(surface, IidDxgiAccess);
                        if (access != 0)
                        {
                            try
                            {
                                nint texture;
                                var iid = IidTexture2D;
                                var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Method(access, 3))(access, &iid, &texture);
                                if (hr == 0 && texture != 0)
                                {
                                    try
                                    {
                                        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Com.Method(_context, 47))(_context, _staging, texture);
                                        fresh = true;
                                    }
                                    finally
                                    {
                                        Com.Release(texture);
                                    }
                                }
                            }
                            finally
                            {
                                Com.Release(access);
                            }
                        }
                    }
                    finally
                    {
                        Com.Release(surface);
                    }
                }
            }
            finally
            {
                Com.Release(frame);
            }
        }
    }

    /// <summary><c>wgc-read-staging</c>: Map the staging texture and pack its rows (GPU pitch) tight.</summary>
    private void ReadStaging()
    {
        MappedSubresource mapped;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, MappedSubresource*, int>)Com.Method(_context, 14))(
            _context, _staging, 0, 1 /* READ */, 0, &mapped);
        if (hr != 0) throw new InvalidOperationException($"Map(staging) hr={hr:X}");
        try
        {
            var widthBytes = Width * 4;
            if (mapped.RowPitch == widthBytes)
                Buffer.MemoryCopy(mapped.Data, _frameBuffer, (long)widthBytes * Height, (long)widthBytes * Height);
            else
                for (var row = 0; row < Height; row++)
                    Buffer.MemoryCopy(mapped.Data + (long)row * mapped.RowPitch, _frameBuffer + (long)row * widthBytes, widthBytes, widthBytes);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Com.Method(_context, 15))(_context, _staging, 0);
        }
    }

    /// <summary>
    /// <c>stop-wgc-session</c>: stop flag + poke the pipe so a feeder still
    /// waiting for ffmpeg wakes.
    /// </summary>
    public void Stop()
    {
        _stop = true;
        OutboundPipe.Poke(FfmpegArgs.VideoPipeName);
    }

    /// <summary>
    /// <c>close-wgc-session</c> (wgc-win32.lisp:592), idempotent: join the
    /// feeder up to 2 s; if it is STILL alive, leak everything rather than free
    /// it under a live thread. Session and pool get IClosable.Close first (stops
    /// DWM delivery and the border at once), then every object is released.
    /// </summary>
    public void Close()
    {
        _stop = true;
        var thread = _thread;
        if (thread is { IsAlive: true })
        {
            thread.Join(TimeSpan.FromSeconds(2));
            if (thread.IsAlive)
            {
                RecordingLog.Write("wgc close: feeder thread still alive, leaking session");
                return;
            }
        }
        CloseAndRelease(ref _session);
        CloseAndRelease(ref _pool);
        ReleaseField(ref _item);
        ReleaseField(ref _staging);
        ReleaseField(ref _winrtDevice);
        ReleaseField(ref _winrtInspectable);
        ReleaseField(ref _context);
        ReleaseField(ref _device);
        var buffer = _frameBuffer;
        _frameBuffer = null;
        if (buffer is not null) NativeMemory.Free(buffer);
        _pipe?.Close(flush: false);
    }

    private static void CloseAndRelease(ref nint field)
    {
        var obj = Interlocked.Exchange(ref field, 0);
        if (obj == 0) return;
        var closable = Com.Query(obj, IidClosable);
        if (closable != 0)
        {
            try
            {
                Com.CallNoArgs(closable, 6);
            }
            catch (Exception)
            {
                // ignore-errors
            }
            Com.Release(closable);
        }
        Com.Release(obj);
    }

    private static void ReleaseField(ref nint field) => Com.Release(Interlocked.Exchange(ref field, 0));

    private static void PutBoolIfSupported(nint obj, in Guid iid, int slot, bool value)
    {
        var itf = Com.Query(obj, iid);
        if (itf == 0) return;
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, byte, int>)Com.Method(itf, slot))(itf, value ? (byte)1 : (byte)0);
        }
        finally
        {
            Com.Release(itf);
        }
    }

    /// <summary><c>wgc-activation-factory</c>: the class's factory under <paramref name="iid"/>, or 0.</summary>
    private static nint ActivationFactory(string className, in Guid iid)
    {
        nint hstring;
        fixed (char* p = className)
        {
            if (NativeMethods.WindowsCreateString(p, (uint)className.Length, out hstring) != 0) return 0;
        }
        try
        {
            return NativeMethods.RoGetActivationFactory(hstring, iid, out var factory) == 0 ? factory : 0;
        }
        finally
        {
            NativeMethods.WindowsDeleteString(hstring);
        }
    }
}
