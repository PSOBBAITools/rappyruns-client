using System.Text.Json;
using RappyRuns.Core.Media;

namespace RappyRuns.Tests.Media;

/// <summary>
/// Golden parity with the Lisp client (desktop/tools/export-media-golden.lisp
/// run on SBCL): every ffmpeg argv across the capture-source decision table
/// must match byte for byte (media spec §1.6), plus the remux/trim argv, the
/// rect math, the command-line quoting and retention.
/// </summary>
public class MediaGoldenTests
{
    private static readonly JsonElement G = Golden.Load("media/recording.json");

    private static List<string> Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToList();

    private static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    private static ScreenRect Rect(JsonElement e)
    {
        var r = Ints(e);
        return new ScreenRect(r[0], r[1], r[2], r[3]);
    }

    private static CropRect? Crop(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return null;
        var r = Ints(e);
        return new CropRect(r[0], r[1], r[2], r[3]);
    }

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

    [Fact]
    public void FfmpegArgvDecisionTable()
    {
        var cores = G.GetProperty("cores").GetInt32();
        var cases = G.GetProperty("ffmpeg").EnumerateArray().ToList();
        Assert.Equal(256, cases.Count);
        foreach (var c in cases)
        {
            var m = c.GetProperty("monitor");
            var w = c.GetProperty("wgc");
            var monitor = m.ValueKind == JsonValueKind.Null
                ? null
                : new CaptureMonitor(m.GetProperty("outputIdx").GetInt32(),
                    m.GetProperty("adapter").ValueKind == JsonValueKind.Null ? null : m.GetProperty("adapter").GetInt32(),
                    m.GetProperty("width").GetInt32(), m.GetProperty("height").GetInt32(), Crop(m.GetProperty("crop")));
            var wgc = w.ValueKind == JsonValueKind.Null
                ? null
                : new WgcCapturePlan(null, w.GetProperty("pipe").GetString()!, w.GetProperty("width").GetInt32(),
                    w.GetProperty("height").GetInt32(), Crop(w.GetProperty("crop")));
            var audio = Str(c.GetProperty("audioPipe"));
            var args = FfmpegArgs.Build(new FfmpegCaptureOptions
            {
                WindowTitle = "Ephinea: Phantasy Star Online Blue Burst",
                OutputPath = @"C:\Users\u\Videos\RappyRuns\rec-tmp-20260924-213000-0a1b2c3d.mp4",
                AudioPipe = audio,
                Monitor = monitor,
                Wgc = wgc,
                VideoEncoder = Str(c.GetProperty("encoder")),
                GpuChain = c.GetProperty("gpuChain").GetBoolean(),
                LowMemory = c.GetProperty("lowMemory").GetBoolean(),
                EncoderThreads = FfmpegArgs.EncoderThreadCount(cores),
            });
            var label = $"{c.GetProperty("source").GetString()} enc={Str(c.GetProperty("encoder"))} chain={c.GetProperty("gpuChain")} audio={audio is not null} low={c.GetProperty("lowMemory")}";
            Assert.True(Strings(c.GetProperty("args")).SequenceEqual(args), label + "\n" + string.Join(' ', args));
            Assert.Equal(c.GetProperty("commandLine").GetString(),
                CommandLine.Build(@"C:\Program Files\Rappy Runs\ffmpeg\ffmpeg.exe", args));
            if (audio is not null)
            {
                Assert.True(Strings(c.GetProperty("stripped")).SequenceEqual(FfmpegArgs.StripAudioArgs(args, audio)), label + " strip");
                Assert.True(Strings(c.GetProperty("retargeted")).SequenceEqual(FfmpegArgs.RetargetAudioArgs(args, "f32le", 48000, 2)), label + " retarget");
            }
        }
    }

    [Fact]
    public void RemuxArgv()
    {
        foreach (var c in G.GetProperty("remux").EnumerateArray())
        {
            var d = c.GetProperty("durationMs");
            var args = FfmpegArgs.BuildRemux(c.GetProperty("input").GetString()!, c.GetProperty("output").GetString()!,
                d.ValueKind == JsonValueKind.Null ? null : d.GetInt64());
            Assert.Equal(Strings(c.GetProperty("args")), args);
        }
    }

    [Fact]
    public void ThreadsAndScale()
    {
        foreach (var pair in G.GetProperty("threads").EnumerateArray())
        {
            var p = Ints(pair);
            Assert.Equal(p[1], FfmpegArgs.EncoderThreadCount(p[0]));
        }
        var inputs = new (int W, int H)[]
        {
            (3200, 1800), (2560, 1600), (1440, 900), (1367, 899), (1920, 1080), (1921, 1081), (5120, 2160), (3840, 2160),
            (1280, 720), (1366, 768), (2560, 1080), (1080, 1920), (3000, 2000), (1000, 1081), (1001, 1082), (4096, 2160),
            (1600, 1200), (3440, 1440), (2880, 1800), (2736, 1824),
        };
        var expected = G.GetProperty("scale").EnumerateArray().Select(Ints).ToList();
        for (var i = 0; i < inputs.Length; i++)
            Assert.Equal((expected[i][0], expected[i][1]), FfmpegArgs.RecordScaleDimensions(inputs[i].W, inputs[i].H));
    }

    [Fact]
    public void Rects()
    {
        foreach (var c in G.GetProperty("cropRects").EnumerateArray())
            Assert.Equal(Crop(c.GetProperty("crop")), CaptureGeometry.CaptureCropRect(Rect(c.GetProperty("client")), Rect(c.GetProperty("monitor"))));
        foreach (var c in G.GetProperty("wgcCropRects").EnumerateArray())
            Assert.Equal(Crop(c.GetProperty("crop")), CaptureGeometry.WgcCropRect(Rect(c.GetProperty("client")), Rect(c.GetProperty("window")),
                c.GetProperty("frameWidth").GetInt32(), c.GetProperty("frameHeight").GetInt32()));
        foreach (var c in G.GetProperty("covers").EnumerateArray())
            Assert.Equal(c.GetProperty("covers").GetBoolean(), CaptureGeometry.RectCovers(Rect(c.GetProperty("window")), Rect(c.GetProperty("monitor"))));
    }

    [Fact]
    public void QuotingSanitizeDuration()
    {
        foreach (var pair in G.GetProperty("quote").EnumerateArray())
        {
            var p = Strings(pair);
            Assert.Equal(p[1], CommandLine.QuoteArg(p[0]));
        }
        foreach (var pair in G.GetProperty("sanitize").EnumerateArray())
        {
            var p = Strings(pair);
            Assert.Equal(p[1], RecordingFiles.SanitizeFilename(p[0]));
        }
        foreach (var pair in G.GetProperty("duration").EnumerateArray())
        {
            var input = pair[0].ValueKind == JsonValueKind.Null ? (long?)null : pair[0].GetInt64();
            var output = pair[1].ValueKind == JsonValueKind.Null ? (long?)null : pair[1].GetInt64();
            Assert.Equal(output, SessionRuns.SessionVideoDurationMs(input));
        }
    }

    [Fact]
    public void ProbeArgv()
    {
        var probe = G.GetProperty("probe");
        foreach (var pair in probe.GetProperty("encoders").EnumerateArray())
            Assert.Equal(Strings(pair[1]), FfmpegArgs.HwEncoderProbeArgs(pair[0].GetString()!));
        Assert.Equal(Strings(probe.GetProperty("gpuChain")), FfmpegArgs.HwGpuChainProbeArgs());
        Assert.Equal(Strings(probe.GetProperty("gdigrab")), FfmpegArgs.GdigrabProbeArgs("Ephinea: Phantasy Star Online Blue Burst"));
        Assert.Equal(probe.GetProperty("encoders").EnumerateArray().Select(p => p[0].GetString()!), FfmpegArgs.HwEncoderCandidates);
    }

    [Fact]
    public void Eviction()
    {
        foreach (var c in G.GetProperty("evict").EnumerateArray())
        {
            var files = c.GetProperty("files").EnumerateArray()
                .Select(f => new RecordingFile(f[0].GetString()!, f[1].GetInt64(), f[2].GetInt64())).ToList();
            var cap = c.GetProperty("cap");
            List<string>? Opt(string name) => c.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Strings(c.GetProperty(name));
            var expected = c.GetProperty("evicted").ValueKind == JsonValueKind.Null ? [] : Strings(c.GetProperty("evicted"));
            Assert.Equal(expected, RecordingRetention.RecordingsToEvict(files,
                cap.ValueKind == JsonValueKind.Null ? null : cap.GetInt64(), Opt("protected"), Opt("uploaded")));
        }
    }
}
