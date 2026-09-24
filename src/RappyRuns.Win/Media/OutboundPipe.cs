namespace RappyRuns.Win.Media;

/// <summary>
/// The server end of a byte-mode, blocking, single-instance outbound named
/// pipe that ffmpeg opens as an input (the audio pipe, audio-win32.lisp:787,
/// and the WGC video pipe, wgc-win32.lisp:416). Raw Win32 rather than
/// NamedPipeServerStream so the creation flags, buffer size, the
/// ERROR_PIPE_CONNECTED handling and FlushFileBuffers-before-close are exactly
/// the Lisp ones.
/// </summary>
internal sealed unsafe class OutboundPipe
{
    private nint _handle;

    private OutboundPipe(string name, nint handle)
    {
        Name = name;
        _handle = handle;
    }

    public string Name { get; }

    /// <summary>
    /// CreateNamedPipeW(name, PIPE_ACCESS_OUTBOUND, byte|blocking, 1 instance,
    /// out buffer, in buffer 0, timeout 0); null when it cannot be created
    /// (another instance holding the fixed name, for one).
    /// </summary>
    public static OutboundPipe? Create(string name, uint outBufferBytes)
    {
        var handle = NativeMethods.CreateNamedPipe(name, NativeMethods.PipeAccessOutbound, 0, 1, outBufferBytes, 0, 0, 0);
        return NativeMethods.IsInvalid(handle) ? null : new OutboundPipe(name, handle);
    }

    /// <summary>The Win32 error of the last failed <see cref="Create"/> on this thread.</summary>
    public static int LastCreateError => NativeMethods.LastError;

    /// <summary>Block until ffmpeg opens the pipe; ERROR_PIPE_CONNECTED (it already did) is success too.</summary>
    public bool Connect() =>
        NativeMethods.ConnectNamedPipe(_handle, 0) || NativeMethods.LastError == NativeMethods.ErrorPipeConnected;

    /// <summary>WriteFile everything; false once the reader (ffmpeg) is gone.</summary>
    public bool Write(byte* data, int bytes)
    {
        var handle = _handle;
        if (handle == 0) return false;
        return NativeMethods.WriteFile(handle, data, (uint)bytes, out var written, 0) && written == (uint)bytes;
    }

    /// <summary>
    /// FlushFileBuffers then CloseHandle = ffmpeg's EOF on this input. Closing
    /// a pipe DISCARDS unread data, so the flush comes first (it returns at
    /// once when the pipe is already broken). Idempotent.
    /// </summary>
    public void Close(bool flush)
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle == 0) return;
        if (flush)
        {
            try
            {
                NativeMethods.FlushFileBuffers(handle);
            }
            catch (Exception)
            {
                // Best effort; the close below still gives ffmpeg its EOF.
            }
        }
        NativeMethods.CloseHandle(handle);
    }

    /// <summary>
    /// Wake a thread blocked in <see cref="Connect"/> by briefly connecting to
    /// the pipe ourselves (<c>stop-audio-session</c> / <c>stop-wgc-session</c>).
    /// </summary>
    public static void Poke(string name)
    {
        var client = NativeMethods.CreateFile(name, NativeMethods.GenericRead, 0, 0, NativeMethods.OpenExisting, 0, 0);
        if (!NativeMethods.IsInvalid(client)) NativeMethods.CloseHandle(client);
    }
}
