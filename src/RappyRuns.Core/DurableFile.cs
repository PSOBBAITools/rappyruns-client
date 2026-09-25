namespace RappyRuns.Core;

/// <summary>
/// Temp-file-then-rename writes (S50). NTFS may put a rename on disk before
/// the data it names, so a crash right after an unflushed write + rename can
/// leave the real name pointing at an empty or partial file. A durable write
/// flushes the data to disk before the rename; callers on the tracking
/// thread pass <c>durable: false</c> so they never wait on the disk.
/// </summary>
/// <remarks>
/// Durable: config.sexp (<c>ConfigStore</c>) and the Pin Share addon files
/// (<c>AddonInstaller</c>). Not durable: the run queue (saved from the poll
/// thread) and the trigger log compaction (a diagnostic log). Not in this helper at
/// all: the recording rename (ffmpeg's output, <c>Win32FfmpegBackend.RenameFile</c>),
/// written by another process and possibly gigabytes.
/// </remarks>
public static class DurableFile
{
    /// <summary>
    /// Creates (or truncates) <paramref name="path"/> and lets
    /// <paramref name="write"/> fill it. With <paramref name="durable"/>, the
    /// data is flushed to disk before returning, so a rename that follows
    /// cannot reach the disk first.
    /// </summary>
    public static void Write(string path, Action<Stream> write, bool durable = true)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        write(stream);
        stream.Flush(flushToDisk: durable);
    }

    /// <summary>A durable <see cref="Write"/> of the given bytes (the addon installer's writer).</summary>
    public static void WriteFlushed(string path, byte[] bytes) => Write(path, s => s.Write(bytes));

    /// <summary>
    /// Replaces <paramref name="path"/> with new contents: writes
    /// <paramref name="tempPath"/> through <paramref name="write"/> (flushed
    /// to disk when <paramref name="durable"/>), then renames it over
    /// <paramref name="path"/>. On failure the error is thrown,
    /// <paramref name="path"/> is left as it was, and a temp file this call
    /// created is deleted (best effort; a temp file it could not open may be
    /// another writer's and is left alone).
    /// </summary>
    public static void Replace(string path, string tempPath, Action<Stream> write, bool durable = true)
    {
        var created = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                created = true;
                write(stream);
                stream.Flush(flushToDisk: durable);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch when (created)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The original error is the one to report.
            }
            throw;
        }
    }
}
