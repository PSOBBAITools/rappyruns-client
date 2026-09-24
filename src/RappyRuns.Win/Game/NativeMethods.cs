using System.Runtime.InteropServices;

namespace RappyRuns.Win.Game;

/// <summary>
/// Win32 bindings for attaching to PSOBB (win32.lisp:14-485). Functions whose
/// failure is diagnosed set SetLastError so the error is captured immediately
/// after the call (spec core §22 #32 - anything in between can clobber it).
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessQueryInformation = 0x0400;
    public const uint StillActive = 259;

    // ---------------------------------------------------------------- user32

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    // ---------------------------------------------------------------- kernel32

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(nint process, nint baseAddress, void* buffer, nuint size, out nuint bytesRead);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* exeName, ref uint size);

    // ---------------------------------------------------------------- wintrust / crypt32

    public const uint WtdUiNone = 2;
    public const uint WtdChoiceFile = 1;
    public const uint WtdStateActionVerify = 1;
    public const uint WtdStateActionClose = 2;
    public const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    public const uint TrustENoSignature = 0x800B0100;
    public const uint CertNameSimpleDisplayType = 4;

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2 {00AAC56B-CD44-11d0-8CC2-00C04FC295EE}.</summary>
    public static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    public struct WintrustFileInfo
    {
        public uint CbStruct;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WintrustData
    {
        public uint CbStruct;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public WintrustFileInfo* FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    /// <summary>Leading fields of CRYPT_PROVIDER_CERT (only read from wintrust's own allocations).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CryptProviderCertPrefix
    {
        public uint CbStruct;
        public nint Cert;
    }

    /// <summary>Leading fields of CRYPT_PROVIDER_SGNR.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CryptProviderSgnrPrefix
    {
        public uint CbStruct;
        public uint VerifyAsOfLow;
        public uint VerifyAsOfHigh;
        public uint CsCertChain;
        public CryptProviderCertPrefix* PasCertChain;
    }

    /// <summary>Returns a LONG HRESULT; compared as uint against the 0x8... constants.</summary>
    [LibraryImport("wintrust.dll")]
    public static partial int WinVerifyTrust(nint hwnd, Guid* actionId, WintrustData* data);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    public static partial CryptProviderSgnrPrefix* WTHelperGetProvSignerFromChain(nint provData, uint idxSigner,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint idxCounterSigner);

    [LibraryImport("crypt32.dll", EntryPoint = "CertGetNameStringW")]
    public static partial uint CertGetNameString(nint certContext, uint type, uint flags, nint typePara, char* nameString, uint cchNameString);
}
