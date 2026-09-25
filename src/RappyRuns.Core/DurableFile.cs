namespace RappyRuns.Core;

/// <summary>
/// Temp-file-then-rename writes that survive a power cut (S50). NTFS may put
/// a rename on disk before the data it names, so a crash right after an
/// unflushed write + rename can leave the real name pointing at an empty or
/// partial file. Every write here flushes the data to disk before the rename.
/// </summary>
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
    /// it, then renames it over <paramref name="path"/>. A failure before the
    /// rename leaves <paramref name="path"/> untouched (the temp file may
    /// remain; callers that care delete it).
    /// </summary>
    public static void Replace(string path, string tempPath, Action<Stream> write)
    {
        WriteFlushed(tempPath, write);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Renames a file another writer finished (<paramref name="from"/>, e.g.
    /// ffmpeg's output) over <paramref name="to"/>, flushing its data to disk
    /// first.
    /// </summary>
    public static void MoveFlushed(string from, string to)
    {
        using (var stream = new FileStream(from, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            stream.Flush(flushToDisk: true);
        File.Move(from, to, overwrite: true);
    }
}
