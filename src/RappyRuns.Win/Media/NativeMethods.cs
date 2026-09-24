using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("RappyRuns.Tests")]

namespace RappyRuns.Win.Media;

/// <summary>
/// Hand-written P/Invokes for the recording pipeline (the Lisp FLI
/// definitions of ffmpeg-win32.lisp, wgc-win32.lisp and audio-win32.lisp).
/// COM and WinRT objects are called through raw vtable slots
/// (<see cref="Com"/>), exactly as the Lisp does, so no projection or
/// interop package is needed and the slot numbers are the field-validated ones.
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const uint StartfUseStdHandles = 0x100;
    public const uint CreateNoWindow = 0x08000000;
    public const uint BelowNormalPriorityClass = 0x4000;
    public const uint HandleFlagInherit = 1;
    public const uint GenericWrite = 0x40000000;
    public const uint GenericRead = 0x80000000;
    public const uint FileShareRead = 1;
    public const uint CreateAlways = 2;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x80;
    public const uint StillActive = 259;
    public const uint PipeAccessOutbound = 2;
    public const int ErrorPipeConnected = 535;
    public const uint WaitObject0 = 0;
    public const uint MonitorDefaultToNearest = 2;
    public const uint CoinitMultithreaded = 0;
    public const uint ClsctxAll = 0x17;
    public static readonly nint InvalidHandleValue = -1;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoW
    {
        public uint Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort CbReserved2;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoExW
    {
        public uint CbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public fixed char Device[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    // kernel32: processes, pipes, files, events.

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out nint readPipe, out nint writePipe, ref SecurityAttributes attributes, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(char* applicationName, char* commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        nint environment, char* currentDirectory, ref StartupInfoW startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(string name, uint access, uint share, nint securityAttributes,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFileInheritable(string name, uint access, uint share, ref SecurityAttributes securityAttributes,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(nint file, byte* buffer, uint bytes, out uint written, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, nint securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConnectNamedPipe(nint pipe, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    public static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    // user32: window and monitor geometry.

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, ref Point point);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MonitorInfoExW info);

    [LibraryImport("user32.dll")]
    public static partial nint SetThreadDpiAwarenessContext(nint context);

    // COM / WinRT / D3D / DXGI / audio.

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pointer);

    [LibraryImport("combase.dll")]
    public static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll")]
    public static partial int WindowsCreateString(char* source, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(in Guid iid, out nint factory);

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint numLevels, uint sdkVersion, out nint device, nint featureLevel, out nint context);

    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint inspectable);

    [LibraryImport("mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ActivateAudioInterfaceAsync(string deviceInterfacePath, in Guid riid, nint activationParams,
        nint completionHandler, out nint operation);

    /// <summary>The Win32 error a failed call left (SetLastError = true).</summary>
    public static int LastError => Marshal.GetLastPInvokeError();

    public static bool IsInvalid(nint handle) => handle == 0 || handle == InvalidHandleValue;
}

/// <summary>Raw COM vtable access, like the Lisp <c>com-method</c>/<c>com-release</c>.</summary>
internal static unsafe class Com
{
    /// <summary>Slot <paramref name="index"/> of <paramref name="obj"/>'s vtable.</summary>
    public static nint Method(nint obj, int index) => (*(nint**)obj)[index];

    public static uint Release(nint obj)
    {
        if (obj == 0) return 0;
        try
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint>)Method(obj, 2))(obj);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>QueryInterface (slot 0), or 0.</summary>
    public static nint Query(nint obj, in Guid iid)
    {
        nint result = 0;
        fixed (Guid* p = &iid)
        {
            var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(obj, 0))(obj, p, &result);
            return hr == 0 ? result : 0;
        }
    }

    /// <summary>A no-argument HRESULT method (Start, Stop, StartCapture, Close...).</summary>
    public static int CallNoArgs(nint obj, int slot) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Method(obj, slot))(obj);

    /// <summary>A getter returning an object pointer: HRESULT M(this, out T*).</summary>
    public static int GetObject(nint obj, int slot, out nint result)
    {
        nint r = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(obj, slot))(obj, &r);
        result = r;
        return hr;
    }

    public static bool Failed(int hr) => hr < 0;
}
