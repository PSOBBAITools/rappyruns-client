using System.Runtime.InteropServices;

namespace RappyRuns.Win.PinShare;

/// <summary>Kernel32 calls for the exchange files and the addon folder (pinshare-win32.lisp:16-32).</summary>
internal static partial class NativeMethods
{
    public const uint InvalidFileAttributes = 0xFFFFFFFF;
    public const uint FileAttributeReparsePoint = 0x400;
    public const uint MovefileReplaceExisting = 1;
    public const int ErrorPathNotFound = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetFileAttributes(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string from, string to, uint flags);
}
