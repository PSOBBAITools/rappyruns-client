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

    [Fact(DisplayName = "durable file: a non-durable replace writes and renames the same way (S50)")]
    public void NonDurableReplace()
    {
        using var dir = new TempDir("rr-durable");
        var path = dir.File("queue.sexp");
        File.WriteAllText(path, "old");
        DurableFile.Replace(path, path + ".tmp", s => s.Write("new"u8), durable: false);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact(DisplayName = "durable file: a failed write leaves the target untouched (S50)")]
    public void FailedWriteKeepsTarget()
    {
        using var dir = new TempDir("rr-durable");
        var path = dir.File("config.sexp");
        File.WriteAllText(path, "old");
        Assert.Throws<IOException>(() => DurableFile.Replace(path, path + ".tmp", _ => throw new IOException("disk full")));
        Assert.Equal("old", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp")); // no half-written temp left behind
    }

    [Fact(DisplayName = "durable file: a refused rename leaves the target and removes the temp file (S50)")]
    public void RefusedMoveKeepsTarget()
    {
        using var dir = new TempDir("rr-durable");
        var path = dir.File("config.sexp");
        File.WriteAllText(path, "old");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // held without delete sharing
            Assert.True(Record.Exception(() => DurableFile.Replace(path, path + ".tmp", s => s.Write("new"u8)))
                is IOException or UnauthorizedAccessException);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact(DisplayName = "durable file: a temp file it could not open is left alone (S50)")]
    public void ForeignTempKept()
    {
        using var dir = new TempDir("rr-durable");
        var path = dir.File("config.sexp");
        var temp = path + ".tmp";
        File.WriteAllText(temp, "another writer's");
        using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            Assert.ThrowsAny<IOException>(() => DurableFile.Replace(path, temp, s => s.Write("new"u8)));
        Assert.Equal("another writer's", File.ReadAllText(temp));
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
}
