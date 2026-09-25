namespace RappyRuns.Core;

/// <summary>
/// Temp-file-then-rename writes that survive a power cut (S50). NTFS may put
/// a rename on disk before the data it names, so a crash right after an
/// unflushed write + rename can leave the real name pointing at an empty or
/// partial file. Every write here flushes the data to disk before the rename.
/// </summary>
/// <remarks>
/// Not for the recording rename (ffmpeg's output, <c>Win32FfmpegBackend.RenameFile</c>):
/// that file is written by another process, can be gigabytes, and is renamed
/// on the poll thread, where a flush of that size would stall run tracking.
/// </remarks>
public static class DurableFile
{
    /// <summary>
    /// Creates (or truncates) <paramref name="path"/>, lets
    /// <paramref name="write"/> fill it, and flushes it to disk before
    /// returning, so a rename that follows cannot reach the disk first.
    /// </summary>
    public static void WriteFlushed(string path, Action<Stream> write)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        write(stream);
        stream.Flush(flushToDisk: true);
    }

    /// <summary><see cref="WriteFlushed(string, Action{Stream})"/> with the given bytes.</summary>
    public static void WriteFlushed(string path, byte[] bytes) => WriteFlushed(path, s => s.Write(bytes));

    /// <summary>
    /// Replaces <paramref name="path"/> with new contents: writes
    /// <paramref name="tempPath"/> through <paramref name="write"/>, flushes
    /// it, then renames it over <paramref name="path"/>. On failure the error
    /// is thrown, <paramref name="path"/> is left as it was,
    /// and the temp file is deleted (best effort).
    /// </summary>
    public static void Replace(string path, string tempPath, Action<Stream> write)
    {
        try
        {
            WriteFlushed(tempPath, write);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
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
