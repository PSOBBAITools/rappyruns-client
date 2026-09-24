using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RappyRuns.Core.Media;

namespace RappyRuns.Win.Media;

/// <summary>What an audio session hears (<c>audio-session-scope</c>).</summary>
public enum AudioScope
{
    /// <summary>:none - every WASAPI path failed; silence is served.</summary>
    None,

    /// <summary>:game - process loopback of the PSOBB process tree only.</summary>
    Game,

    /// <summary>:desktop - endpoint loopback of the default render device (all system sound).</summary>
    Desktop,
}

/// <summary>
/// Game-audio capture for recordings (media spec §1.14, audio-win32.lisp):
/// WASAPI process loopback scoped to the PSOBB process tree (Windows 10 2004+),
/// else endpoint loopback, else silence - served as raw PCM on
/// <c>\\.\pipe\ephinea-ta-audio</c>, ffmpeg's second input. The pacing clock
/// is re-anchored to the pipe-connect instant (≈ video time 0, thanks to
/// -probesize 32): anchoring at session creation put the audio 567 ms late.
/// Wall-clock pacing tops the stream up with silence so audio time always
/// tracks video time, even when the source goes quiet or never activated.
/// </summary>
public sealed unsafe class AudioSession
{
    /// <summary>+audio-rate+: the process-loopback request rate.</summary>
    public const int Rate = 48000;

    /// <summary>+audio-channels+.</summary>
    public const int Channels = 2;

    private const int ActivationTypeProcessLoopback = 1;
    private const int LoopbackModeIncludeTree = 0;
    private const ushort VtBlob = 65;
    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;
    private const int AudclntShared = 0;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint BufferFlagsSilent = 2;
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const long BufferDuration100Ns = 2_000_000; // 200 ms

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IidCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    /// <summary>Activations are serialized (one completion event), like *audio-activation-lock*.</summary>
    private static readonly object ActivationLock = new();

    private static nint _completionHandler;
    private static nint _activationEvent;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public uint ActivationType;
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT carrying a VT_BLOB (x64: cbSize at 8, pointer at 16).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public nint BlobData;
    }

    private readonly OutboundPipe _pipe;
    private Thread? _thread;
    private long _startTicks;
    private nint _client;
    private nint _captureClient;
    private nint _event;
    private volatile bool _stop;

    private AudioSession(OutboundPipe pipe)
    {
        _pipe = pipe;
        _startTicks = Stopwatch.GetTimestamp();
    }

    public string PipeName => _pipe.Name;

    public AudioScope Scope { get; private set; } = AudioScope.None;

    /// <summary>The capture format ffmpeg's argv is retargeted to: "f32le" or "s16le".</summary>
    public string SampleFormat { get; private set; } = "f32le";

    public int SampleRate { get; private set; } = Rate;

    public int ChannelCount { get; private set; } = Channels;

    public int FrameBytes { get; private set; } = 8;

    /// <summary>How long ffmpeg took to open the pipe (ms), once it did.</summary>
    public long? ConnectMs { get; private set; }

    public long FramesWritten { get; private set; }

    public long Packets { get; private set; }

    public long DataFrames { get; private set; }

    public string? Error { get; private set; }

    /// <summary>
    /// <c>start-audio-session</c> (audio-win32.lisp:780): create the pipe server
    /// end, activate capture for <paramref name="pid"/> SYNCHRONOUSLY (the
    /// format must be known before ffmpeg's argv is final), start the feeder
    /// thread. Null only when the pipe cannot be created; a WASAPI failure
    /// still returns a session serving silence. Call from an MTA thread (the
    /// poll thread): the activated objects are used from the feeder thread too.
    /// </summary>
    public static AudioSession? Start(string pipeName, int? pid)
    {
        var pipe = OutboundPipe.Create(pipeName, 1024 * 1024);
        if (pipe is null) return null;
        var session = new AudioSession(pipe);
        NativeMethods.CoInitializeEx(0, NativeMethods.CoinitMultithreaded);
        if (pid is { } p) session.ActivateCapture(p);
        session._thread = new Thread(() =>
        {
            try
            {
                session.CaptureLoop();
            }
            catch (Exception e)
            {
                session.Error = e.Message;
                session._pipe.Close(flush: false);
            }
        })
        {
            Name = "eta-audio-capture",
            IsBackground = true,
        };
        session._thread.SetApartmentState(ApartmentState.MTA);
        session._thread.Start();
        return session;
    }

    /// <summary>
    /// <c>stop-audio-session</c>: set the stop flag and poke the pipe so a
    /// thread still waiting for ffmpeg wakes; the loop's exit closes the pipe,
    /// which is the audio EOF ffmpeg needs to finalize.
    /// </summary>
    public void Stop()
    {
        _stop = true;
        OutboundPipe.Poke(_pipe.Name);
    }

    /// <summary>Test/diagnostic hook: wait for the feeder thread to finish.</summary>
    public bool Join(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    /// <summary>
    /// <c>activate-session-capture</c>: game-only process loopback first, then
    /// the endpoint mix; on total failure the scope stays None (silence).
    /// </summary>
    private void ActivateCapture(int pid)
    {
        try
        {
            lock (ActivationLock)
            {
                var ev = NativeMethods.CreateEvent(0, false, false, 0);
                try
                {
                    var (client, capture) = ActivateProcessLoopback(pid, ev);
                    _client = client;
                    _captureClient = capture;
                    _event = ev;
                    Scope = AudioScope.Game;
                    SampleRate = Rate;
                    ChannelCount = Channels;
                    FrameBytes = Channels * 4;
                    SampleFormat = "f32le";
                }
                catch (Exception)
                {
                    NativeMethods.CloseHandle(ev);
                    throw;
                }
            }
        }
        catch (Exception first)
        {
            Error = first.Message;
            try
            {
                var (client, capture, rate, channels, frameBytes, format) = ActivateEndpointLoopback();
                _client = client;
                _captureClient = capture;
                Scope = AudioScope.Desktop;
                SampleRate = rate;
                ChannelCount = channels;
                FrameBytes = frameBytes;
                SampleFormat = format;
            }
            catch (Exception second)
            {
                Error = $"{first.Message}; fallback: {second.Message}";
            }
        }
    }

    /// <summary>
    /// <c>activate-process-loopback</c> (audio-win32.lisp:458):
    /// ActivateAudioInterfaceAsync("VAD\Process_Loopback", IAudioClient,
    /// PROPVARIANT{VT_BLOB, AUDIOCLIENT_ACTIVATION_PARAMS{PROCESS_LOOPBACK, pid,
    /// INCLUDE_TARGET_PROCESS_TREE}}), wait up to 3 s for the completion, then
    /// Initialize float32 stereo 48 kHz with LOOPBACK | EVENTCALLBACK.
    /// </summary>
    private static (nint Client, nint Capture) ActivateProcessLoopback(int pid, nint ev)
    {
        EnsureCompletionHandler();
        var activated = _activationEvent;
        var p = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = LoopbackModeIncludeTree,
        };
        var prop = new PropVariantBlob
        {
            Vt = VtBlob,
            BlobSize = (uint)sizeof(ActivationParams),
            BlobData = (nint)(&p),
        };
        var format = FloatFormat(Rate, Channels);
        var hr = NativeMethods.ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", IidIAudioClient,
            (nint)(&prop), _completionHandler, out var operation);
        if (Com.Failed(hr)) throw new InvalidOperationException($"ActivateAudioInterfaceAsync failed (#x{hr:X})");
        try
        {
            if (NativeMethods.WaitForSingleObject(activated, 3000) != NativeMethods.WaitObject0)
                throw new InvalidOperationException("audio activation timed out");
            int activateHr;
            nint client;
            var hr2 = ((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)Com.Method(operation, 3))(operation, &activateHr, &client);
            if (Com.Failed(hr2) || Com.Failed(activateHr))
                throw new InvalidOperationException($"audio activation failed (#x{hr2:X} / #x{activateHr:X})");
            try
            {
                return (client, SetupCaptureClient(client, &format, StreamFlagsLoopback | StreamFlagsEventCallback, ev));
            }
            catch (Exception)
            {
                Com.Release(client);
                throw;
            }
        }
        finally
        {
            Com.Release(operation);
        }
    }

    /// <summary>
    /// <c>activate-endpoint-loopback</c> (audio-win32.lisp:528): the default
    /// render endpoint in loopback, in its own mix format (polled, no event).
    /// </summary>
    private static (nint Client, nint Capture, int Rate, int Channels, int FrameBytes, string Format) ActivateEndpointLoopback()
    {
        var hr = NativeMethods.CoCreateInstance(ClsidMMDeviceEnumerator, 0, NativeMethods.ClsctxAll, IidIMMDeviceEnumerator, out var enumerator);
        if (Com.Failed(hr)) throw new InvalidOperationException($"CoCreateInstance(MMDeviceEnumerator) failed (#x{hr:X})");
        nint device = 0, client = 0;
        WaveFormatEx* format = null;
        try
        {
            nint dev;
            var hr2 = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Com.Method(enumerator, 4))(enumerator, EDataFlowRender, ERoleMultimedia, &dev);
            if (Com.Failed(hr2)) throw new InvalidOperationException($"no default render device (#x{hr2:X})");
            device = dev;
            nint ac;
            var iid = IidIAudioClient;
            hr2 = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Com.Method(device, 3))(device, &iid, NativeMethods.ClsctxAll, 0, &ac);
            if (Com.Failed(hr2)) throw new InvalidOperationException($"IMMDevice::Activate(IAudioClient) failed (#x{hr2:X})");
            client = ac;
            WaveFormatEx* fmt;
            hr2 = ((delegate* unmanaged[Stdcall]<nint, WaveFormatEx**, int>)Com.Method(client, 8))(client, &fmt);
            if (Com.Failed(hr2)) throw new InvalidOperationException($"GetMixFormat failed (#x{hr2:X})");
            format = fmt;
            var sampleFormat = MixFormatSampleFormat(fmt->FormatTag, fmt->BitsPerSample)
                ?? throw new InvalidOperationException($"unsupported mix format (tag {fmt->FormatTag}, {fmt->BitsPerSample} bits)");
            var capture = SetupCaptureClient(client, fmt, StreamFlagsLoopback, 0);
            var result = (client, capture, (int)fmt->SamplesPerSec, (int)fmt->Channels, (int)fmt->BlockAlign, sampleFormat);
            Com.Release(device);
            Com.Release(enumerator);
            NativeMethods.CoTaskMemFree((nint)format);
            return result;
        }
        catch (Exception)
        {
            Com.Release(client);
            Com.Release(device);
            Com.Release(enumerator);
            if (format is not null) NativeMethods.CoTaskMemFree((nint)format);
            throw;
        }
    }

    /// <summary>
    /// <c>mix-format-sample-format</c>: 32-bit float (or EXTENSIBLE) -> f32le,
    /// 16-bit PCM (or EXTENSIBLE) -> s16le, else unsupported.
    /// </summary>
    internal static string? MixFormatSampleFormat(ushort tag, ushort bits) =>
        bits == 32 && tag is WaveFormatIeeeFloat or WaveFormatExtensible ? "f32le"
        : bits == 16 && tag is WaveFormatPcm or WaveFormatExtensible ? "s16le"
        : null;

    /// <summary>
    /// <c>setup-capture-client</c>: Initialize(SHARED, flags, 200 ms, 0, format),
    /// SetEventHandle when evented, GetService(IAudioCaptureClient), Start.
    /// </summary>
    private static nint SetupCaptureClient(nint client, WaveFormatEx* format, uint streamFlags, nint ev)
    {
        var hr = ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormatEx*, Guid*, int>)Com.Method(client, 3))(
            client, AudclntShared, streamFlags, BufferDuration100Ns, 0, format, null);
        if (Com.Failed(hr)) throw new InvalidOperationException($"IAudioClient::Initialize failed (#x{hr:X})");
        if (ev != 0)
            ((delegate* unmanaged[Stdcall]<nint, nint, int>)Com.Method(client, 13))(client, ev);
        nint service;
        var iid = IidIAudioCaptureClient;
        hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Method(client, 14))(client, &iid, &service);
        if (Com.Failed(hr)) throw new InvalidOperationException($"GetService(IAudioCaptureClient) failed (#x{hr:X})");
        Com.CallNoArgs(client, 10); // Start
        return service;
    }

    private static WaveFormatEx FloatFormat(int rate, int channels) => new()
    {
        FormatTag = WaveFormatIeeeFloat,
        Channels = (ushort)channels,
        SamplesPerSec = (uint)rate,
        AvgBytesPerSec = (uint)(rate * channels * 4),
        BlockAlign = (ushort)(channels * 4),
        BitsPerSample = 32,
        CbSize = 0,
    };

    /// <summary>
    /// <c>audio-capture-loop</c> (audio-win32.lisp:659): connect, re-anchor the
    /// pacing epoch, then forward packets (and silence) until stopped.
    /// </summary>
    private void CaptureLoop()
    {
        var co = NativeMethods.CoInitializeEx(0, NativeMethods.CoinitMultithreaded);
        try
        {
            if (!_pipe.Connect())
                throw new InvalidOperationException("ffmpeg never opened the audio pipe");
            var freq = Stopwatch.Frequency;
            ConnectMs = Lisp.Round(1000 * (Stopwatch.GetTimestamp() - _startTicks), freq);
            // Re-anchor to the pipe-connect instant = video time 0 within a frame.
            _startTicks = Stopwatch.GetTimestamp();
            if (_stop) return;
            var zerosFrames = (int)Lisp.Floor(SampleRate, 10); // 100 ms
            var zeros = new byte[zerosFrames * FrameBytes];
            long written = 0;
            var start = _startTicks;
            fixed (byte* z = zeros)
            {
                while (!_stop)
                {
                    // Process loopback signals its event; endpoint loopback
                    // events are unreliable without our own render stream,
                    // so that path polls.
                    if (_event != 0)
                        NativeMethods.WaitForSingleObject(_event, 50);
                    else
                        Thread.Sleep(20);
                    if (_captureClient != 0)
                    {
                        var (ok, frames) = DrainPackets(z, zerosFrames);
                        if (!ok) break;
                        written += frames;
                    }
                    // Top up with silence when the source goes quiet.
                    var elapsed = Stopwatch.GetTimestamp() - start;
                    var expected = Lisp.Floor(elapsed * SampleRate, freq);
                    var behind = expected - written;
                    if (behind > Lisp.Floor(SampleRate, 10))
                    {
                        if (!WriteSilence(behind, z, zerosFrames)) break;
                        written += behind;
                    }
                    FramesWritten = written;
                }
            }
        }
        finally
        {
            if (_client != 0)
            {
                try
                {
                    Com.CallNoArgs(_client, 11); // Stop
                }
                catch (Exception)
                {
                    // ignore-errors
                }
            }
            Com.Release(_captureClient);
            Com.Release(_client);
            _captureClient = _client = 0;
            if (_event != 0) NativeMethods.CloseHandle(_event);
            _event = 0;
            // Closing a pipe discards unread data: flush first, then EOF.
            _pipe.Close(flush: true);
            if (co >= 0) NativeMethods.CoUninitialize();
        }
    }

    private bool WriteSilence(long frames, byte* zeros, int zerosFrames)
    {
        while (frames > 0)
        {
            var chunk = (int)Math.Min(frames, zerosFrames);
            if (!_pipe.Write(zeros, chunk * FrameBytes)) return false;
            frames -= chunk;
        }
        return true;
    }

    /// <summary><c>drain-packets</c>: forward every pending WASAPI packet (SILENT ones as zeros).</summary>
    private (bool Ok, long Frames) DrainPackets(byte* zeros, int zerosFrames)
    {
        var cc = _captureClient;
        long total = 0;
        while (true)
        {
            uint pending;
            var hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Method(cc, 5))(cc, &pending);
            if (Com.Failed(hr) || pending == 0) return (true, total);
            byte* data;
            uint frames, flags;
            hr = ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Com.Method(cc, 3))(
                cc, &data, &frames, &flags, null, null);
            if (Com.Failed(hr)) return (true, total);
            Packets++;
            var silent = (flags & BufferFlagsSilent) != 0;
            if (!silent) DataFrames += frames;
            var ok = silent ? WriteSilence(frames, zeros, zerosFrames) : _pipe.Write(data, (int)(frames * FrameBytes));
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Com.Method(cc, 4))(cc, frames);
            if (!ok) return (false, total);
            total += frames;
        }
    }

    // The completion handler: a static native COM object whose
    // ActivateCompleted signals the activation event. It answers IAgileObject
    // too (it is called from an MTA worker). Never freed; refcount is a lie.

    private static void EnsureCompletionHandler()
    {
        if (_completionHandler != 0) return;
        var vtable = (nint*)NativeMemory.Alloc(4, (nuint)sizeof(nint));
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&HandlerQueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&HandlerAddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&HandlerRelease;
        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&HandlerActivateCompleted;
        var obj = (nint*)NativeMemory.Alloc(1, (nuint)sizeof(nint));
        *obj = (nint)vtable;
        // The auto-reset *activation-event* the handler signals; one is
        // enough because activations are serialized by ActivationLock.
        _activationEvent = NativeMethods.CreateEvent(0, false, false, 0);
        _completionHandler = (nint)obj;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HandlerQueryInterface(nint self, Guid* riid, nint* result)
    {
        if (*riid == IidIUnknown || *riid == IidIAgileObject || *riid == IidCompletionHandler)
        {
            *result = self;
            return 0;
        }
        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint HandlerAddRef(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint HandlerRelease(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HandlerActivateCompleted(nint self, nint operation)
    {
        var ev = _activationEvent;
        if (ev != 0) NativeMethods.SetEvent(ev);
        return 0;
    }
}
