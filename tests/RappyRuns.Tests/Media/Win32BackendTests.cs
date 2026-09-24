using RappyRuns.Core.Media;
using RappyRuns.Win.Media;

namespace RappyRuns.Tests.Media;

/// <summary>
/// The live backend's process and file plumbing, exercised with cmd.exe (no
/// ffmpeg, window or audio device needed). The capture paths themselves were
/// smoke-tested by hand against a real window (see the migration report).
/// </summary>
[Collection("recording-log")]
public class Win32BackendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rr-media-win-" + Guid.NewGuid().ToString("N"));

    public Win32BackendTests()
    {
        Directory.CreateDirectory(_dir);
        // Keep stderr transcripts out of the machine's real recording log.
        RecordingLog.PathOverride = Path.Combine(_dir, "recording.log");
    }

    public void Dispose()
    {
        RecordingLog.PathOverride = TestLog.Path;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Win32FfmpegBackend Backend() => new(() => 0, () => true, null);

    [Fact]
    public void SpawnFailureKeepsTheWindowsErrorShape()
    {
        var result = Backend().StartRemux(Path.Combine(_dir, "no-such-ffmpeg.exe"), ["-i", "x", Path.Combine(_dir, "out.mp4")]);
        Assert.Null(result.Handle);
        Assert.Matches(@"^could not start .*no-such-ffmpeg\.exe \(Windows error 2\)$", result.Error);
    }

    [Fact]
    public void ExitCodesAliveAndStderrFile()
    {
        var backend = Backend();
        var output = Path.Combine(_dir, "o.mp4");
        // cmd /c "echo boom 1>&2 & exit 3": stderr goes to <output>.stderr.txt.
        var result = backend.StartRemux(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe"),
            ["/c", "echo boom 1>&2 & exit 3", output]);
        var handle = Assert.IsAssignableFrom<ICaptureHandle>(result.Handle);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (backend.IsAlive(handle) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.False(backend.IsAlive(handle));
        Assert.False(backend.Succeeded(handle));
        // Share everything: a process another test spawns at the same moment
        // can inherit the stderr handle for a moment (bInheritHandles), and a
        // plain ReadAllText would then refuse to open the file.
        using (var stream = new FileStream(RecordingFiles.StderrFileFor(output), FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            Assert.Contains("boom", reader.ReadToEnd());
        backend.Close(handle);
        backend.Close(handle); // idempotent
        // Deletion can likewise wait for such an inherited handle to close.
        var gone = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(RecordingFiles.StderrFileFor(output)) && DateTime.UtcNow < gone)
        {
            Thread.Sleep(50);
            backend.Close(handle);
        }
        Assert.False(File.Exists(RecordingFiles.StderrFileFor(output)));
    }

    [Fact]
    public void KillTerminatesARunningProcess()
    {
        var backend = Backend();
        var result = backend.StartRemux(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe"),
            ["/c", "ping -n 30 127.0.0.1 >nul", Path.Combine(_dir, "k.mp4")]);
        var handle = result.Handle!;
        Assert.True(backend.IsAlive(handle));
        backend.Kill(handle);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (backend.IsAlive(handle) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.False(backend.IsAlive(handle));
        backend.Close(handle);
    }

    [Fact]
    public void FileListingsSplitTmpFromKept()
    {
        foreach (var name in new[] { "rec-tmp-1.mp4", "rec-tmp-2.mp4", "kept (2).mp4", "Kept.MP4", "notes.txt", "x.mp4x" })
            File.WriteAllText(Path.Combine(_dir, name), "12345");
        var backend = Backend();
        Assert.Equal(["rec-tmp-1.mp4", "rec-tmp-2.mp4"], backend.ListStaleFiles(_dir).Select(p => Path.GetFileName(p)!).Order().ToArray());
        var kept = backend.ListRecordings(_dir);
        Assert.Equal(["Kept.MP4", "kept (2).mp4"], kept.Select(f => Path.GetFileName(f.Path)).Order(StringComparer.Ordinal).ToArray());
        Assert.All(kept, f => Assert.Equal(5, f.SizeBytes));
        Assert.All(kept, f => Assert.True(f.WriteDate > Lisp.EncodeUniversalTime(new DateTime(2020, 1, 1))));
        Assert.Empty(backend.ListStaleFiles(Path.Combine(_dir, "missing")));
        backend.RenameFile(Path.Combine(_dir, "rec-tmp-1.mp4"), Path.Combine(_dir, "rec-tmp-2.mp4"));
        Assert.False(File.Exists(Path.Combine(_dir, "rec-tmp-1.mp4")));
        backend.DeleteFile(Path.Combine(_dir, "rec-tmp-2.mp4"));
        backend.DeleteFile(Path.Combine(_dir, "rec-tmp-2.mp4"));
        Assert.False(File.Exists(Path.Combine(_dir, "rec-tmp-2.mp4")));
    }

    [Fact]
    public void MixFormatMapping()
    {
        Assert.Equal("f32le", AudioSession.MixFormatSampleFormat(3, 32));
        Assert.Equal("f32le", AudioSession.MixFormatSampleFormat(0xFFFE, 32));
        Assert.Equal("s16le", AudioSession.MixFormatSampleFormat(1, 16));
        Assert.Equal("s16le", AudioSession.MixFormatSampleFormat(0xFFFE, 16));
        Assert.Null(AudioSession.MixFormatSampleFormat(1, 24));
        Assert.Null(AudioSession.MixFormatSampleFormat(3, 16));
    }

    [Fact]
    public void MachineProbeReadsMemory()
    {
        var machine = MachineProbe.Current();
        Assert.True(machine.PhysicalMemoryBytes > 0);
        Assert.Equal("Windows NT", machine.SoftwareType);
        Assert.EndsWith(") ", machine.SoftwareVersion);
    }
}
