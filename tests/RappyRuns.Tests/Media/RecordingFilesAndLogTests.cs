using System.Text;
using System.Text.RegularExpressions;
using RappyRuns.Core.Media;
using static RappyRuns.Tests.Media.MediaFixtures;

namespace RappyRuns.Tests.Media;

/// <summary>
/// File naming/locations (media spec §1.11) and the recording log (§1.12).
/// Not in the Lisp suite beyond default-record-dir-choice; pinned here because
/// both are read by people and by the server's diagnostics.
/// </summary>
[Collection("recording-log")]
public class RecordingFilesAndLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rr-media-files-" + Guid.NewGuid().ToString("N"));

    public RecordingFilesAndLogTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void TmpPathAndToken()
    {
        var token = RecordingFiles.RecordingToken();
        Assert.Matches("^[0-9a-f]{8}$", token);
        Assert.Equal(Path.Combine(@"C:\v\", "rec-tmp-20260924-213005-0a1b2c3d.mp4"),
            RecordingFiles.RecordingTmpPath(@"C:\v\", new DateTime(2026, 9, 24, 21, 30, 5), "0a1b2c3d"));
    }

    [Fact]
    public void RunVideoFilenameFallbacks()
    {
        Assert.Equal("ep1-test-quest 9'59.123 (2026-07-04 2130).mp4", RecordingFiles.RunVideoFilename(Run()));
        Assert.Equal("run 0'00.000 (2026-07-04 2130).mp4", RecordingFiles.RunVideoFilename(P("time-ms", 0, "finished-at", FinishedAt)));
        Assert.Equal("Q-A 65'00.001 (2026-07-04 2130).mp4",
            RecordingFiles.RunVideoFilename(P("quest-name", "Q:A", "time-ms", 3900001, "finished-at", FinishedAt)));
        var now = RecordingFiles.RunVideoFilename(P("quest-slug", "s", "time-ms", 1000), () => FinishedAt);
        Assert.Equal("s 0'01.000 (2026-07-04 2130).mp4", now);
    }

    [Fact]
    public void DeduplicatePathCountsLikeExplorer()
    {
        var path = Path.Combine(_dir, "a b.mp4");
        Assert.Equal(path, RecordingFiles.DeduplicatePath(path));
        File.WriteAllText(path, "");
        Assert.Equal(Path.Combine(_dir, "a b (2).mp4"), RecordingFiles.DeduplicatePath(path));
        File.WriteAllText(Path.Combine(_dir, "a b (2).mp4"), "");
        Assert.Equal(Path.Combine(_dir, "a b (3).mp4"), RecordingFiles.DeduplicatePath(path));
    }

    [Fact]
    public void RecordDirChoiceAndResolution()
    {
        Assert.Equal(RecordDirChoice.UseNew, RecordingFiles.DefaultRecordDirChoice(false, false));
        Assert.Equal(RecordDirChoice.UseNew, RecordingFiles.DefaultRecordDirChoice(false, true));
        Assert.Equal(RecordDirChoice.UseNew, RecordingFiles.DefaultRecordDirChoice(true, true));
        Assert.Equal(RecordDirChoice.Migrate, RecordingFiles.DefaultRecordDirChoice(true, false));

        Assert.Equal(@"D:\clips\", RecordingFiles.ResolveRecordDir(@"  D:\clips  "));
        var home = Path.Combine(_dir, "home");
        Assert.Equal(Path.Combine(home, "Videos", "RappyRuns") + "\\", RecordingFiles.ResolveRecordDir("", home));
        Directory.CreateDirectory(Path.Combine(home, "Videos", "EphineaTA"));
        File.WriteAllText(Path.Combine(home, "Videos", "EphineaTA", "x.mp4"), "");
        Assert.Equal(Path.Combine(home, "Videos", "RappyRuns") + "\\", RecordingFiles.ResolveRecordDir(null, home));
        Assert.True(File.Exists(Path.Combine(home, "Videos", "RappyRuns", "x.mp4")));
    }

    [Fact]
    public void FfmpegPathResolution()
    {
        Assert.Equal(@"C:\x\ffmpeg.exe", RecordingFiles.ResolveFfmpegPath(@" C:\x\ffmpeg.exe "));
        Assert.Equal("ffmpeg.exe", RecordingFiles.ResolveFfmpegPath("", _dir));
        Directory.CreateDirectory(Path.Combine(_dir, "ffmpeg"));
        File.WriteAllText(Path.Combine(_dir, "ffmpeg", "ffmpeg.exe"), "");
        Assert.Equal(Path.Combine(_dir, "ffmpeg", "ffmpeg.exe"), RecordingFiles.ResolveFfmpegPath(null, _dir));
        Assert.Equal(@"C:\v\a.mp4.stderr.txt", RecordingFiles.StderrFileFor(@"C:\v\a.mp4"));
    }

    [Fact]
    public void LogLinesRotationAndTail()
    {
        var log = Path.Combine(_dir, "ephinea-ta-recording.log");
        RecordingLog.PathOverride = log;
        RecordingLog.Write("capture check: no PSOBB window");
        RecordingLog.Write("ffmpeg stderr (x):\nline two");
        var bytes = File.ReadAllBytes(log);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Matches(new Regex(@"^\d\d-\d\d \d\d:\d\d:\d\d capture check: no PSOBB window\r\n\d\d-\d\d \d\d:\d\d:\d\d ffmpeg stderr \(x\):\r\nline two\r\n$"), text);
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
        Assert.EndsWith("ffmpeg stderr (x):\nline two\n", RecordingLog.FileTail(log, 65536));
        Assert.Equal("two\n", RecordingLog.FileTail(log, 4));

        File.WriteAllBytes(log, new byte[RecordingLog.MaxBytes + 1]);
        RecordingLog.Write("after rotation");
        Assert.True(File.Exists(Path.ChangeExtension(log, ".old")));
        Assert.Matches(@"^\d\d-\d\d \d\d:\d\d:\d\d after rotation\r\n$", File.ReadAllText(log));

        Assert.Null(RecordingLog.FileTail(Path.Combine(_dir, "missing.log"), 10));
        File.WriteAllText(Path.Combine(_dir, "empty.txt"), "");
        Assert.Null(RecordingLog.FileTail(Path.Combine(_dir, "empty.txt"), 10));
    }

    [Fact]
    public void DiagnosticsReportShape()
    {
        var log = Path.Combine(_dir, "ephinea-ta-recording.log");
        RecordingLog.PathOverride = log;
        var machine = new MachineInfo(8L << 30, 8, "Windows NT", "Windows 11: 10.0 (build 26200) ");
        var hw = new HwEncoderStatus { Encoder = "h264_amf", State = HwProbeState.Done };
        Assert.Equal(
            "client 0.60.0\nos Windows NT Windows 11: 10.0 (build 26200) \nram-gb 8 cores 8\n" +
            "hw-encoder h264_amf gpu-chain NIL low-memory T\n" +
            $"--- recording log tail ({log}, exists NIL) ---\n(no recording log)",
            RecordingLog.DiagnosticsReport("0.60.0", machine, hw));
        RecordingLog.Write("hello");
        var report = RecordingLog.DiagnosticsReport("dev", new MachineInfo(null, 4, null, null), new HwEncoderStatus());
        Assert.StartsWith("client dev\nos NIL NIL\nram-gb NIL cores 4\nhw-encoder NIL gpu-chain NIL low-memory NIL\n", report);
        Assert.Contains($"({log}, exists T) ---\n", report);
        Assert.EndsWith(" hello\n", report);
        Assert.Equal("session: client 0.60.0, Windows NT Windows 11: 10.0 (build 26200) , ram-gb 64, cores 32, ffmpeg C:\\f.exe",
            RecordingLog.SessionLine("0.60.0", new MachineInfo(64L << 30, 32, "Windows NT", "Windows 11: 10.0 (build 26200) "), @"C:\f.exe"));
    }

    [Fact]
    public void ProbeLogLines()
    {
        Assert.Equal("hw encoder probe: using h264_amf", HwEncoderStatus.ProbeLogLine("h264_amf", HwProbeState.Done));
        Assert.Equal("hw encoder probe: using libx264 (no hardware encoder) (provisional - ffmpeg would not start)",
            HwEncoderStatus.ProbeLogLine(null, HwProbeState.SpawnFailed));
        Assert.Equal("gpu chain probe: fullscreen captures stay on the GPU (hwmap -> vpp_qsv)", HwEncoderStatus.GpuChainLogLine(true));
        Assert.Equal("gpu chain probe: fullscreen captures keep the hwdownload fallback", HwEncoderStatus.GpuChainLogLine(false));
    }

    [Fact]
    public void LispPrintHelpers()
    {
        Assert.Equal("\"\\\\\\\\.\\\\DISPLAY1\"", Lisp.Prin1(@"\\.\DISPLAY1"));
        Assert.Equal("(0 0 1920 1080)", new ScreenRect(0, 0, 1920, 1080).ToString());
        Assert.Equal(2, Lisp.Round(5, 2));   // 2.5 -> 2 (half to even)
        Assert.Equal(4, Lisp.Round(7, 2));   // 3.5 -> 4
        Assert.Equal(-2, Lisp.Round(-5, 2)); // -2.5 -> -2
        Assert.Equal(-3, Lisp.Floor(-5, 2));
        Assert.Equal(1, Lisp.Mod(-5, 2));
    }
}
