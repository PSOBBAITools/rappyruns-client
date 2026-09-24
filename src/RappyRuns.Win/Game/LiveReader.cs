using System.Runtime.InteropServices;
using RappyRuns.Core.Game;
using static RappyRuns.Win.Game.NativeMethods;

namespace RappyRuns.Win.Game;

/// <summary>
/// Reads the PSOBB process (win32.lisp:202 live-reader). PSOBB is 32-bit, so
/// every address fits in 32 bits and is read cross-bitness from this 64-bit
/// process with ReadProcessMemory. The handle has only PROCESS_VM_READ |
/// PROCESS_QUERY_INFORMATION. Reads go straight into a pinned managed array
/// (the poll loop moves tens of KB through here 30× a second).
/// </summary>
public sealed unsafe class LiveReader : IProcessMemoryReader
{
    private nint _handle;

    internal LiveReader(nint handle, int pid, string? windowTitle)
    {
        _handle = handle;
        Pid = pid;
        WindowTitle = windowTitle;
    }

    public int Pid { get; }

    /// <summary>The window's exact title (the recorder's gdigrab title= input), captured at attach.</summary>
    public string? WindowTitle { get; }

    public int? LastReadError { get; private set; }

    public byte[]? ReadBlock(long address, int size)
    {
        // Addresses are the 32-bit target's; anything outside is unreadable (a
        // garbage pointer chase), never wrapped around.
        if (size < 0 || address < 0 || address + size > 0x1_0000_0000L || _handle == 0) return null;
        var result = new byte[size];
        if (size == 0) return result;
        bool ok;
        nuint read;
        fixed (byte* p = result)
        {
            ok = ReadProcessMemory(_handle, (nint)address, p, (nuint)size, out read);
        }
        if (ok && read == (nuint)size) return result;
        // Captured before anything else can clobber it. A single failed read
        // is normal mid-warp; the poll loop decides when reads are "dead".
        LastReadError = ok ? 299 : Marshal.GetLastPInvokeError();
        return null;
    }

    /// <summary>GetExitCodeProcess == STILL_ACTIVE (win32.lisp:323 reader-alive-p).</summary>
    public bool IsAlive => _handle != 0 && GetExitCodeProcess(_handle, out var code) && code == StillActive;

    /// <summary>win32.lisp:384 process-image-path: QueryFullProcessImageNameW (1024 chars), or null.</summary>
    public string? ImagePath
    {
        get
        {
            if (_handle == 0) return null;
            const int maxChars = 1024;
            var buffer = stackalloc char[maxChars];
            uint length = maxChars;
            return QueryFullProcessImageName(_handle, 0, buffer, ref length) && length > 0 && length <= maxChars
                ? new string(buffer, 0, (int)length)
                : null;
        }
    }

    /// <summary>win32.lisp:407 reader-quest-loaded-p: one read of the quest pointer.</summary>
    public bool QuestLoaded => PsobbReader.QuestLoaded(this);

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) CloseHandle(handle);
    }
}
