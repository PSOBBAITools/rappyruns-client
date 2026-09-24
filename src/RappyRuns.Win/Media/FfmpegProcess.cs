using RappyRuns.Core.Media;

namespace RappyRuns.Win.Media;

/// <summary>
/// A spawned ffmpeg (<c>ffmpeg-capture</c>, ffmpeg-win32.lisp:128): the
/// process with its stdin pipe (a lone "q" stops it cleanly), the audio and WGC
/// sessions feeding it, and the file its stderr goes to.
/// </summary>
internal sealed unsafe class FfmpegProcess : ICaptureHandle
{
    private readonly object _gate = new();
    private nint _process;
    private nint _thread;
    private nint _stdinWrite;
    private bool _closed;

    private FfmpegProcess(nint process, nint thread, nint stdinWrite, uint pid, string? stderrPath)
    {
        _process = process;
        _thread = thread;
        _stdinWrite = stdinWrite;
        Pid = pid;
        StderrPath = stderrPath;
    }

    public uint Pid { get; }

    public AudioSession? Audio { get; set; }

    public WgcSession? Wgc { get; set; }

    /// <summary>Where the child's stderr goes, or null (not captured / already transcribed).</summary>
    public string? StderrPath { get; set; }

    /// <summary>
    /// <c>spawn-process</c> (ffmpeg-win32.lisp:181): CreateProcessW with the
    /// CommandLineToArgvW-quoted command line, stdin wired to an anonymous pipe
    /// we keep the write end of, stderr to <paramref name="stderrPath"/> when it
    /// can be created, stdout unset. EVERY ffmpeg runs CREATE_NO_WINDOW |
    /// BELOW_NORMAL_PRIORITY_CLASS - x264 at normal priority stole the game's
    /// frame time. Throws "could not start PROGRAM (Windows error N)" on failure
    /// (the recorder's 4551 check reads that exact shape).
    /// </summary>
    public static FfmpegProcess Spawn(string program, IReadOnlyList<string> args, string? stderrPath = null)
    {
        var attributes = new NativeMethods.SecurityAttributes
        {
            Length = (uint)sizeof(NativeMethods.SecurityAttributes),
            InheritHandle = 1,
        };
        if (!NativeMethods.CreatePipe(out var stdinRead, out var stdinWrite, ref attributes, 0))
            throw new InvalidOperationException($"CreatePipe failed (Windows error {NativeMethods.LastError})");
        // CreatePipe made both ends inheritable; only the child's may be.
        NativeMethods.SetHandleInformation(stdinWrite, NativeMethods.HandleFlagInherit, 0);
        nint stderrHandle = 0;
        if (stderrPath is not null)
        {
            try
            {
                var h = NativeMethods.CreateFileInheritable(stderrPath, NativeMethods.GenericWrite, NativeMethods.FileShareRead,
                    ref attributes, NativeMethods.CreateAlways, NativeMethods.FileAttributeNormal, 0);
                if (!NativeMethods.IsInvalid(h)) stderrHandle = h;
            }
            catch (Exception)
            {
                // stderr capture is diagnostics, never worth failing over.
            }
        }
        try
        {
            var startup = new NativeMethods.StartupInfoW
            {
                Cb = (uint)sizeof(NativeMethods.StartupInfoW),
                Flags = NativeMethods.StartfUseStdHandles,
                StdInput = stdinRead,
                StdError = stderrHandle,
            };
            var commandLine = (CommandLine.Build(program, args) + "\0").ToCharArray();
            bool ok;
            NativeMethods.ProcessInformation info;
            fixed (char* cmd = commandLine)
            {
                ok = NativeMethods.CreateProcessW(null, cmd, 0, 0, true,
                    NativeMethods.CreateNoWindow | NativeMethods.BelowNormalPriorityClass,
                    0, null, ref startup, out info);
            }
            if (!ok)
                throw new InvalidOperationException($"could not start {program} (Windows error {NativeMethods.LastError})");
            // The child inherited its own copies; drop ours.
            NativeMethods.CloseHandle(stdinRead);
            if (stderrHandle != 0) NativeMethods.CloseHandle(stderrHandle);
            return new FfmpegProcess(info.Process, info.Thread, stdinWrite, info.ProcessId,
                stderrHandle != 0 ? stderrPath : null);
        }
        catch (Exception)
        {
            NativeMethods.CloseHandle(stdinRead);
            NativeMethods.CloseHandle(stdinWrite);
            if (stderrHandle != 0) NativeMethods.CloseHandle(stderrHandle);
            throw;
        }
    }

    /// <summary><c>capture-alive-p</c>: GetExitCodeProcess says STILL_ACTIVE.</summary>
    public bool IsAlive
    {
        get
        {
            lock (_gate)
            {
                if (_closed) return false;
                return NativeMethods.GetExitCodeProcess(_process, out var code) && code == NativeMethods.StillActive;
            }
        }
    }

    /// <summary>Exited with status 0.</summary>
    public bool Succeeded
    {
        get
        {
            lock (_gate)
            {
                if (_closed) return false;
                return NativeMethods.GetExitCodeProcess(_process, out var code) && code == 0;
            }
        }
    }

    /// <summary>
    /// <c>write-quit</c>: "q\n" on stdin - ffmpeg finishes the output cleanly.
    /// Two bytes always fit the pipe buffer, so this never blocks. A no-op once
    /// closed (the delayed stop thread may fire after the close; the Lisp
    /// would write to a stale handle value).
    /// </summary>
    public void WriteQuit()
    {
        lock (_gate)
        {
            if (_closed) return;
            var bytes = stackalloc byte[2];
            bytes[0] = (byte)'q';
            bytes[1] = (byte)'\n';
            NativeMethods.WriteFile(_stdinWrite, bytes, 2, out _, 0);
        }
    }

    /// <summary>TerminateProcess(h, 1).</summary>
    public void Terminate()
    {
        lock (_gate)
        {
            if (!_closed) NativeMethods.TerminateProcess(_process, 1);
        }
    }

    /// <summary>Wait up to <paramref name="milliseconds"/> for the process to exit.</summary>
    public bool WaitForExit(uint milliseconds)
    {
        nint process;
        lock (_gate)
        {
            if (_closed) return true;
            process = _process;
        }
        return NativeMethods.WaitForSingleObject(process, milliseconds) == NativeMethods.WaitObject0;
    }

    /// <summary><c>close-capture-handles</c>, idempotent.</summary>
    public void CloseHandles()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            NativeMethods.CloseHandle(_stdinWrite);
            NativeMethods.CloseHandle(_thread);
            NativeMethods.CloseHandle(_process);
            _stdinWrite = _thread = _process = 0;
        }
    }
}
