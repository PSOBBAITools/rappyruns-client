using RappyRuns.Core.Media;
using RappyRuns.Core.Sexp;
using static RappyRuns.Tests.Media.MediaFixtures;

namespace RappyRuns.Tests.Media;

/// <summary>
/// Port of <c>run-recorder-tests</c>' pure-function checks
/// (client/tests/tests-recorder.lisp:399-949): file naming, best run, trim
/// duration, every argv family, probes, rects and the low-memory profile.
/// </summary>
public class FfmpegArgsTests
{
    private static List<string> Args(string? encoder = null, CaptureMonitor? monitor = null, WgcCapturePlan? wgc = null,
        string? audio = null, bool gpuChain = false, bool lowMemory = false, string output = "out.mp4") =>
        FfmpegArgs.Build(new FfmpegCaptureOptions
        {
            WindowTitle = "T",
            OutputPath = output,
            VideoEncoder = encoder,
            Monitor = monitor,
            Wgc = wgc,
            AudioPipe = audio,
            GpuChain = gpuChain,
            LowMemory = lowMemory,
            EncoderThreads = FfmpegArgs.EncoderThreadCount(),
        });

    private static string Vf(IReadOnlyList<string> args) => After(args, "-vf")!;

    private static int Pos(string s, string sub) => s.IndexOf(sub, StringComparison.Ordinal);

    [Fact]
    public void PureHelpers()
    {
        Check.That("sanitize-filename strips reserved characters", RecordingFiles.SanitizeFilename("a:b/c\"d") == "a-b-c-d");
        Check.That("video filename prefers the in-game quest name",
            RecordingFiles.RunVideoFilename(P("quest-slug", "ep1-towards-the-future", "quest-name", "Towards the Future",
                "time-ms", 599123, "finished-at", FinishedAt)).Contains("Towards the Future 9'59.123", StringComparison.Ordinal));
        Check.That("best-session-run picks the longest run",
            SessionRuns.BestSessionRun([P("quest-slug", "short", "time-ms", 10), P("quest-slug", "long", "time-ms", 20)])!
                .Get("QUEST-SLUG")!.AsString == "long");
        Check.That("best-session-run prefers a completed run over a longer aborted one",
            SessionRuns.BestSessionRun([P("quest-slug", "seg", "time-ms", 10), P("quest-slug", "abort", "time-ms", 20, "aborted", true)])!
                .Get("QUEST-SLUG")!.AsString == "seg");
        Check.That("best-session-run falls back to the longest aborted run",
            SessionRuns.BestSessionRun([P("quest-slug", "ab1", "time-ms", 10, "aborted", true), P("quest-slug", "ab2", "time-ms", 20, "aborted", true)])!
                .Get("QUEST-SLUG")!.AsString == "ab2");
        Check.That("the trim keeps the run plus its tail", SessionRuns.SessionVideoDurationMs(699123) == 701123);
        Check.That("a capture with nothing completed is not trimmed", SessionRuns.SessionVideoDurationMs(null) is null);
    }

    [Fact]
    public void X264Args()
    {
        var args = Args();
        Check.That("ffmpeg args use fragmented mp4", args.Contains("+frag_keyframe+empty_moov"));
        Check.That("ffmpeg args set the poll framerate", args.Contains("30"));
        var probe = args.IndexOf("-probesize");
        var grab = args.IndexOf("gdigrab");
        Check.That("video input probes minimally (A/V sync anchor)", probe >= 0 && grab >= 0 && args[probe + 1] == "32" && probe < grab);
        Check.That("ffmpeg args encode at crf 29", After(args, "-crf") == "29");
        Check.That("ffmpeg args cap the encoder threads (game shares the CPU)",
            After(args, "-threads") == FfmpegArgs.EncoderThreadCount().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check.That("ffmpeg args disable B-frames (zero-based video timestamps)", After(args, "-bf") == "0");
        var vf = Vf(args);
        Check.That("ffmpeg args cap the height at 1080 without upscaling", vf.Contains("scale=-2", StringComparison.Ordinal) && vf.Contains("min(1080", StringComparison.Ordinal));
        Check.That("x264 args convert to yuv420p on the scale with bt709 tags",
            vf.Contains("out_color_matrix=bt709", StringComparison.Ordinal) && vf.Contains("out_range=tv", StringComparison.Ordinal) &&
            vf.Contains(",format=yuv420p,", StringComparison.Ordinal) &&
            vf.Contains("setparams=color_primaries=bt709:color_trc=iec61966-2-1", StringComparison.Ordinal) &&
            Pos(vf, "scale") < Pos(vf, ",format=yuv420p,") && Pos(vf, ",format=yuv420p,") < Pos(vf, "setparams="));
        Check.That("ffmpeg output path is the last argument", args[^1] == "out.mp4");
        Check.That("video-only args carry no audio input", !args.Contains("s16le"));
    }

    [Fact]
    public void HardwareEncoderArgs()
    {
        var args = Args(encoder: "h264_amf");
        Check.That("hw args use the requested encoder, not libx264",
            args.Contains("h264_amf") && !args.Contains("libx264") && !args.Contains("-crf"));
        Check.That("hw args set the VBR bitrate target", After(args, "-b:v") == FfmpegArgs.HwBitrate);
        Check.That("hw args still disable B-frames", After(args, "-bf") == "0");
        var vf = Vf(args);
        Check.That("hw args convert to nv12 after the scale",
            Pos(vf, "scale") >= 0 && Pos(vf, "format=nv12") >= 0 && Pos(vf, "scale") < Pos(vf, "format=nv12"));
        var fs = Vf(Args(encoder: "h264_nvenc", monitor: new CaptureMonitor(0, null, 1920, 1080)));
        Check.That("hw fullscreen args keep the nv12 tail after hwdownload",
            Pos(fs, "hwdownload") >= 0 && Pos(fs, "format=nv12") >= 0 && Pos(fs, "hwdownload") < Pos(fs, "format=nv12"));
        var sw = Args(encoder: null);
        Check.That("no video-encoder keeps the libx264 argv unchanged", sw.Contains("libx264") && !sw.Contains("-b:v"));
        var probe = FfmpegArgs.HwEncoderProbeArgs("h264_qsv");
        Check.That("probe args test the encoder against the null muxer", probe.Contains("h264_qsv") && probe.Contains("null") && probe[^1] == "-");
    }

    [Fact]
    public void GdigrabProbe()
    {
        var args = FfmpegArgs.GdigrabProbeArgs("My Game");
        Check.That("gdigrab probe grabs the window through blackdetect into null",
            args.Contains("gdigrab") && args.Contains("title=My Game") &&
            args.Any(a => a.Contains("blackdetect", StringComparison.Ordinal)) && args.Contains("null") && args[^1] == "-");
        Check.That("gdigrab probe logs at info (blackdetect reports there)", After(args, "-loglevel") == "info");
        Check.That("gdigrab probe grabs a bounded frame count", After(args, "-frames:v") == FfmpegArgs.GdigrabProbeFrames.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check.That("a blackdetect report on stderr reads as black",
            CaptureGeometry.BlackdetectReportsBlack("[blackdetect @ 0x1] black_start:0 black_end:0.266 black_duration:0.266"));
        Check.That("stream chatter without a report reads as non-black",
            !CaptureGeometry.BlackdetectReportsBlack("Input #0, gdigrab, from 'title=T': Stream #0:0: Video: bmp"));
        Check.That("missing stderr reads as non-black (the caller must fail it)", !CaptureGeometry.BlackdetectReportsBlack(null));
        var usable = new GdigrabVerdict(42, 1280, 960, GdigrabResult.Usable);
        Check.That("a matching usable verdict enables gdigrab", CaptureGeometry.GdigrabVerdictUsable(usable, 42, 1280, 960));
        Check.That("an hwnd mismatch (window recreated) keeps ddagrab", !CaptureGeometry.GdigrabVerdictUsable(usable, 43, 1280, 960));
        Check.That("a size mismatch (display mode switch) keeps ddagrab", !CaptureGeometry.GdigrabVerdictUsable(usable, 42, 1920, 1080));
        Check.That("a black verdict keeps ddagrab",
            !CaptureGeometry.GdigrabVerdictUsable(usable with { Result = GdigrabResult.Black }, 42, 1280, 960));
        Check.That("a failed verdict keeps ddagrab",
            !CaptureGeometry.GdigrabVerdictUsable(usable with { Result = GdigrabResult.Failed }, 42, 1280, 960));
        Check.That("no verdict yet keeps ddagrab", !CaptureGeometry.GdigrabVerdictUsable(null, 42, 1280, 960));
    }

    [Fact]
    public void SecondaryAdapter()
    {
        var args = Args(monitor: new CaptureMonitor(1, 1, 1920, 1080), output: "o.mp4");
        var init = args.IndexOf("-init_hw_device");
        Check.That("secondary-adapter capture creates ddagrab's device explicitly",
            init >= 0 && args[init + 1] == "d3d11va=dda:1" && After(args, "-filter_hw_device") == "dda");
        Check.That("the device args precede the lavfi input", args.IndexOf("-init_hw_device") < args.IndexOf("lavfi"));
        Check.That("adapter 0 leaves the ddagrab argv byte-for-byte unchanged",
            Args(monitor: new CaptureMonitor(0, 0, 1920, 1080), output: "o.mp4")
                .SequenceEqual(Args(monitor: new CaptureMonitor(0, null, 1920, 1080), output: "o.mp4")));
        Check.That("a legacy plist without :adapter creates no explicit device",
            !Args(monitor: new CaptureMonitor(0, null, 1920, 1080), output: "o.mp4").Contains("-init_hw_device"));
        var vf = Vf(Args(monitor: new CaptureMonitor(0, 1, 1920, 1080), encoder: "h264_qsv", gpuChain: true, output: "o.mp4"));
        Check.That("a secondary adapter keeps hwdownload even with the QSV chain",
            vf.Contains("hwdownload", StringComparison.Ordinal) && !vf.Contains("hwmap", StringComparison.Ordinal));
    }

    [Fact]
    public void WgcArgs()
    {
        var args = Args(wgc: new WgcCapturePlan(null, FfmpegArgs.VideoPipeName, 1286, 993, new CropRect(3, 26, 1280, 960)), output: "o.mp4");
        Check.That("wgc argv reads raw bgra frames off the pipe",
            args.Contains("rawvideo") && After(args, "-pixel_format") == "bgra" && After(args, "-video_size") == "1286x993" &&
            args.Contains(@"\\.\pipe\ephinea-ta-video"));
        var vf = Vf(args);
        Check.That("wgc argv crops the client area before the scale",
            vf.Contains("crop=1280:960:3:26", StringComparison.Ordinal) && Pos(vf, "crop=") < Pos(vf, "scale"));
        Check.That("wgc argv never touches the GPU grab chains",
            !args.Contains("gdigrab") && !string.Join(' ', args).Contains("ddagrab", StringComparison.Ordinal) &&
            !vf.Contains("hwdownload", StringComparison.Ordinal) && !vf.Contains("hwmap", StringComparison.Ordinal));
        Check.That("wgc argv keeps the bt709 color tags",
            vf.Contains("out_color_matrix=bt709", StringComparison.Ordinal) && vf.Contains("setparams=", StringComparison.Ordinal));
        var whole = Vf(Args(wgc: new WgcCapturePlan(null, "p", 1280, 960), output: "o.mp4"));
        Check.That("wgc without a crop records the whole frame",
            whole.Contains("scale", StringComparison.Ordinal) && !whole.Contains("crop=", StringComparison.Ordinal));
        var hw = Args(wgc: new WgcCapturePlan(null, "p", 1280, 960), encoder: "h264_amf", output: "o.mp4");
        Check.That("wgc with a hw encoder converts to nv12", hw.Contains("h264_amf") && Vf(hw).Contains(",format=nv12,", StringComparison.Ordinal));
        Check.That("wgc crop offsets the client rect into the window",
            CaptureGeometry.WgcCropRect(new(108, 131, 1388, 1091), new(100, 100, 1396, 1099), 1296, 999) == new CropRect(8, 31, 1280, 960));
        Check.That("wgc crop is clamped to the frame and floored even",
            CaptureGeometry.WgcCropRect(new(108, 131, 1389, 1092), new(100, 100, 1396, 1099), 1296, 999) == new CropRect(8, 31, 1280, 960));
        Check.That("a degenerate client area yields no wgc crop",
            CaptureGeometry.WgcCropRect(new(100, 100, 130, 130), new(100, 100, 140, 140), 40, 40) is null);
        Check.That("the default adapter still gets the zero-copy QSV chain",
            Vf(Args(monitor: new CaptureMonitor(0, 0, 1920, 1080), encoder: "h264_qsv", gpuChain: true, output: "o.mp4"))
                .Contains("hwmap", StringComparison.Ordinal));
    }

    [Fact]
    public void EncoderThreads()
    {
        Check.That("encoder threads: half the cores", FfmpegArgs.EncoderThreadCount(8) == 4);
        Check.That("encoder threads floor at 2 on small machines", FfmpegArgs.EncoderThreadCount(2) == 2);
        Check.That("encoder threads cap at +record-max-threads+", FfmpegArgs.EncoderThreadCount(64) == FfmpegArgs.MaxThreads);
    }

    [Fact]
    public void RemuxArgs()
    {
        var args = FfmpegArgs.BuildRemux("in.mp4", "out.mp4");
        Check.That("remux args stream-copy the video with faststart",
            args.Contains("copy") && args.Contains("+faststart") && !args.Contains("libx264"));
        var af = After(args, "-af")!;
        Check.That("remux args loudness-normalize the audio", af.Contains("loudnorm", StringComparison.Ordinal) && args.Contains("aac"));
        Check.That("remux loudness target matches +record-loudness-lufs+ (issue 84)",
            af.Contains($"loudnorm=I={FfmpegArgs.LoudnessLufs}:", StringComparison.Ordinal));
        Check.That("remux applies no timestamp correction (sync fixed at the source)",
            !af.Contains("atrim", StringComparison.Ordinal) && !args.Contains("-itsoffset"));
        Check.That("remux reads the input and writes the output last", args.Contains("in.mp4") && args[^1] == "out.mp4");
    }

    [Fact]
    public void AudioArgs()
    {
        var pipe = FfmpegArgs.AudioPipeName;
        var withAudio = Args(audio: pipe);
        Check.That("audio args add the pipe input and aac", withAudio.Contains(pipe) && withAudio.Contains("aac"));
        Check.That("live capture args carry no loudnorm (it throttles the video)",
            !withAudio.Any(a => a.Contains("loudnorm", StringComparison.Ordinal)));
        Check.That("stripping audio args restores the video-only argv", Args().SequenceEqual(FfmpegArgs.StripAudioArgs(withAudio, pipe)));
        var retargeted = FfmpegArgs.RetargetAudioArgs(withAudio, "f32le", 44100, 2);
        Check.That("retargeting rewrites the audio format tokens",
            retargeted.Contains("f32le") && retargeted.Contains("44100") && !retargeted.Contains("s16le") && !retargeted.Contains("48000"));
        Check.That("retargeting keeps the video tokens intact", retargeted.Contains("gdigrab") && retargeted.Contains("30"));
    }

    [Fact]
    public void FullscreenDetectionAndDdagrab()
    {
        Check.That("a window spanning its monitor exactly is fullscreen",
            CaptureGeometry.RectCovers(new(0, 0, 1920, 1080), new(0, 0, 1920, 1080)));
        Check.That("a fullscreen window on a secondary monitor is fullscreen",
            CaptureGeometry.RectCovers(new(1920, 0, 3840, 1080), new(1920, 0, 3840, 1080)));
        Check.That("a maximized window (work area, above the taskbar) is not",
            !CaptureGeometry.RectCovers(new(0, 0, 1920, 1032), new(0, 0, 1920, 1080)));
        Check.That("an ordinary window is not fullscreen",
            !CaptureGeometry.RectCovers(new(100, 100, 1124, 868), new(0, 0, 1920, 1080)));
        var monitor = new CaptureMonitor(1, null, 1920, 1080);
        var args = Args(monitor: monitor);
        Check.That("fullscreen args capture via ddagrab, not gdigrab",
            !args.Contains("gdigrab") && args.Contains("lavfi") && args.Any(a => a.Contains("ddagrab=output_idx=1", StringComparison.Ordinal)));
        Check.That("fullscreen args keep the framerate and hide the mouse",
            args.Any(a => a.Contains("framerate=30", StringComparison.Ordinal) && a.Contains("draw_mouse=0", StringComparison.Ordinal)));
        var vf = Vf(args);
        Check.That("fullscreen args download GPU frames before the scale cap",
            vf.Contains("hwdownload", StringComparison.Ordinal) && vf.Contains("min(1080", StringComparison.Ordinal) && Pos(vf, "hwdownload") < Pos(vf, "scale"));
        Check.That("stripping audio args restores the fullscreen video-only argv",
            args.SequenceEqual(FfmpegArgs.StripAudioArgs(Args(monitor: monitor, audio: FfmpegArgs.AudioPipeName), FfmpegArgs.AudioPipeName)));
    }

    [Fact]
    public void QsvZeroCopyChain()
    {
        var monitor = new CaptureMonitor(0, null, 2560, 1600);
        var args = Args(monitor: monitor, encoder: "h264_qsv", gpuChain: true);
        var vf = Vf(args);
        Check.That("qsv chain keeps every frame on the GPU",
            vf.Contains("hwmap=derive_device=qsv", StringComparison.Ordinal) && !vf.Contains("hwdownload", StringComparison.Ordinal));
        Check.That("qsv chain scales on the GPU to literal capped dimensions",
            vf.Contains("vpp_qsv=w=1728:h=1080:format=nv12", StringComparison.Ordinal));
        Check.That("qsv chain converts with the explicit bt709 matrix and tags",
            vf.Contains("out_color_matrix=bt709:out_range=tv", StringComparison.Ordinal) &&
            vf.Contains("setparams=color_primaries=bt709:color_trc=iec61966-2-1", StringComparison.Ordinal));
        Check.That("qsv chain still encodes with h264_qsv at the VBR target", args.Contains("h264_qsv") && args.Contains("-b:v"));
        Check.That("qsv chain still disables B-frames", After(args, "-bf") == "0");
        var fallback = Vf(Args(monitor: monitor, encoder: "h264_qsv"));
        Check.That("an unverified chain keeps the hwdownload fallback",
            fallback.Contains("hwdownload", StringComparison.Ordinal) && !fallback.Contains("hwmap", StringComparison.Ordinal));
        Check.That("the chain flag never rewires a monitor-less capture",
            Args(encoder: "h264_qsv", gpuChain: true).SequenceEqual(Args(encoder: "h264_qsv")));
    }

    [Fact]
    public void WindowedCropRects()
    {
        Check.That("crop rect: a window inside its monitor maps to monitor coords",
            CaptureGeometry.CaptureCropRect(new(160, 90, 1760, 990), new(0, 0, 1920, 1080)) == new CropRect(160, 90, 1600, 900));
        Check.That("crop rect: secondary-monitor origins subtract away",
            CaptureGeometry.CaptureCropRect(new(2080, 90, 3680, 990), new(1920, 0, 3840, 1080)) == new CropRect(160, 90, 1600, 900));
        Check.That("crop rect clamps a half-dragged-off window to the monitor",
            CaptureGeometry.CaptureCropRect(new(-100, -50, 924, 718), new(0, 0, 1920, 1080)) == new CropRect(0, 0, 924, 718));
        Check.That("crop rect floors odd sizes to even",
            CaptureGeometry.CaptureCropRect(new(100, 100, 1123, 867), new(0, 0, 1920, 1080)) == new CropRect(100, 100, 1022, 766));
        Check.That("crop rect rejects a sliver (minimized/degenerate window)",
            CaptureGeometry.CaptureCropRect(new(0, 0, 32, 32), new(0, 0, 1920, 1080)) is null);
        var monitor = new CaptureMonitor(1, null, 1920, 1080, new CropRect(160, 90, 1600, 900));
        var args = Args(monitor: monitor);
        var vf = Vf(args);
        Check.That("windowed capture rides ddagrab with a crop before the scale",
            !args.Contains("gdigrab") && args.Any(a => a.Contains("ddagrab=output_idx=1", StringComparison.Ordinal)) &&
            vf.Contains("crop=1600:900:160:90", StringComparison.Ordinal) &&
            Pos(vf, "hwdownload") < Pos(vf, "crop=") && Pos(vf, "crop=") < Pos(vf, "scale"));
        var qsv = Vf(Args(monitor: monitor, encoder: "h264_qsv", gpuChain: true, output: "o.mp4"));
        Check.That("a crop suppresses the zero-copy chain (no crop_qsv here)",
            qsv.Contains("hwdownload", StringComparison.Ordinal) && qsv.Contains("crop=1600:900:160:90", StringComparison.Ordinal) &&
            !qsv.Contains("hwmap", StringComparison.Ordinal) && qsv.Contains("format=nv12", StringComparison.Ordinal));
    }

    [Fact]
    public void ScaleDimensionsAndChainProbe()
    {
        Check.That("scale dimensions cap at 1080 keeping aspect", FfmpegArgs.RecordScaleDimensions(3200, 1800) == (1920, 1080));
        Check.That("scale dimensions leave small sources alone", FfmpegArgs.RecordScaleDimensions(1440, 900) == (1440, 900));
        Check.That("scale dimensions stay even", FfmpegArgs.RecordScaleDimensions(1367, 899) == (1366, 898));
        var chain = FfmpegArgs.HwGpuChainProbeArgs();
        Check.That("gpu chain probe args exercise ddagrab through h264_qsv",
            chain.Any(a => a.Contains("ddagrab", StringComparison.Ordinal)) && chain.Any(a => a.Contains("hwmap", StringComparison.Ordinal)) &&
            chain.Contains("h264_qsv") && chain.Contains("null") && chain[^1] == "-");
        Check.That("gpu chain probe exercises the capture chain's color options",
            chain.Any(a => a.Contains("vpp_qsv=w=1280:h=720:format=nv12:out_color_matrix=bt709:out_range=tv", StringComparison.Ordinal)));
    }

    [Fact]
    public void LowMemoryProfile()
    {
        Check.That("8 GB machines are low-memory, 16 GB and unknown are not",
            FfmpegArgs.LowMemoryMachine(8L << 30) && !FfmpegArgs.LowMemoryMachine(16L << 30) && !FfmpegArgs.LowMemoryMachine(null));
        var hw = Args(encoder: "h264_qsv", lowMemory: true, output: "o.mp4");
        Check.That("low-memory hw args use the reduced VBR profile",
            After(hw, "-b:v") == FfmpegArgs.HwBitrateLow && hw.Contains(FfmpegArgs.HwMaxrateLow));
        Check.That("low-memory x264 args raise the CRF",
            After(Args(lowMemory: true, output: "o.mp4"), "-crf") == FfmpegArgs.CrfLow.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check.That("roomy machines keep the standard rates", Args(encoder: "h264_qsv", output: "o.mp4").Contains(FfmpegArgs.HwBitrate));
    }

    // Beyond the Lisp suite: the spec's verbatim examples (media spec §1.6 (a), §1.8).

    [Fact]
    public void SpecExampleA_GdigrabX264Audio()
    {
        var args = FfmpegArgs.Build(new FfmpegCaptureOptions
        {
            WindowTitle = "Ephinea: Phantasy Star Online Blue Burst",
            OutputPath = @"C:\Users\u\Videos\RappyRuns\rec-tmp-20260924-213000-0a1b2c3d.mp4",
            AudioPipe = FfmpegArgs.AudioPipeName,
            EncoderThreads = FfmpegArgs.EncoderThreadCount(8),
        });
        const string expected = """-y -loglevel error -probesize 32 -analyzeduration 0 -f gdigrab -framerate 30 -draw_mouse 0 -i "title=Ephinea: Phantasy Star Online Blue Burst" -f s16le -ar 48000 -ac 2 -thread_queue_size 1024 -i \\.\pipe\ephinea-ta-audio -c:v libx264 -preset veryfast -threads 4 -crf 29 -bf 0 -pix_fmt yuv420p -vf scale=-2:trunc(min(1080\,ih)/2)*2:flags=fast_bilinear:out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=color_primaries=bt709:color_trc=iec61966-2-1 -c:a aac -b:a 160k -movflags +frag_keyframe+empty_moov C:\Users\u\Videos\RappyRuns\rec-tmp-20260924-213000-0a1b2c3d.mp4""";
        Assert.Equal(expected, CommandLine.Build("x", args)[2..]);
    }

    [Fact]
    public void RemuxTrimFormatting()
    {
        Assert.Equal("702.345", After(FfmpegArgs.BuildRemux("i", "o", 702345), "-t"));
        Assert.Equal("7.005", After(FfmpegArgs.BuildRemux("i", "o", 7005), "-t"));
        Assert.Equal("0.000", After(FfmpegArgs.BuildRemux("i", "o", 0), "-t"));
        Assert.Null(After(FfmpegArgs.BuildRemux("i", "o"), "-t"));
    }

    [Fact]
    public void NoteRunVideoTimingKeepsKeyOrderLikeNconc()
    {
        var run = Run(timeMs: 1000);
        var end = SessionRuns.NoteRunVideoTiming(5000, null, [run]);
        Assert.Equal(5000, end);
        Assert.Equal(["QUEST-SLUG", "TIME-MS", "FINISHED-AT", "VIDEO-OFFSET-MS"], run.Keys.ToArray());
        Assert.Equal(4000, run.Get("VIDEO-OFFSET-MS")!.AsLong);
        Assert.Equal(5000, SessionRuns.NoteRunVideoTiming(3000, 5000, [Run(timeMs: 10)]));
        Assert.Equal(6000, SessionRuns.NoteRunVideoTiming(6000, 5000, [Run(aborted: true)]));
        var nilOffset = Run(timeMs: 1);
        nilOffset.Set("VIDEO-OFFSET-MS", SexpNode.Nil);
        SessionRuns.NoteRunVideoTiming(100, null, [nilOffset]);
        // The Lisp NCONCs a second :video-offset-ms behind the NIL one, and
        // getf still finds the NIL first; a Plist keeps one key, same reading.
        Assert.True(nilOffset.Get("VIDEO-OFFSET-MS")!.IsNil);
    }
}
