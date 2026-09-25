using RappyRuns.Core;

namespace RappyRuns.Tests;

public class DurableFileTests
{
    [Fact(DisplayName = "durable file: replace writes the temp file and renames it over the target (S50)")]
    public void ReplaceWritesAndRenames()
    {
        using var dir = new TempDir("rr-durable");
        var path = dir.File("config.sexp");
        var temp = path + ".tmp";
        File.WriteAllText(path, "old");
        DurableFile.Replace(path, temp, s => s.Write("new"u8));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(temp));
    }

    [Fact(DisplayName = "durable file: a failed write leaves the target untouched (S50)")]
    public void FailedWriteKeepsTarget()
    {
        using var dir = new TempDir("rr-durable");
        var path = dir.File("config.sexp");
        File.WriteAllText(path, "old");
        Assert.Throws<IOException>(() => DurableFile.Replace(path, path + ".tmp", _ => throw new IOException("disk full")));
        Assert.Equal("old", File.ReadAllText(path));
    }

    [Fact(DisplayName = "durable file: write-flushed creates or truncates (S50)")]
    public void WriteFlushedTruncates()
    {
        using var dir = new TempDir("rr-durable");
        var path = dir.File("init.lua.new");
        File.WriteAllText(path, "a much longer old body");
        DurableFile.WriteFlushed(path, "short"u8.ToArray());
        Assert.Equal("short", File.ReadAllText(path));
    }

    [Fact(DisplayName = "durable file: move-flushed renames a finished file over the target (S50)")]
    public void MoveFlushedOverwrites()
    {
        using var dir = new TempDir("rr-durable");
        var from = dir.File("run.tmp.mp4");
        var to = dir.File("run.mp4");
        File.WriteAllText(from, "video");
        File.WriteAllText(to, "stale");
        DurableFile.MoveFlushed(from, to);
        Assert.Equal("video", File.ReadAllText(to));
        Assert.False(File.Exists(from));
    }
}
