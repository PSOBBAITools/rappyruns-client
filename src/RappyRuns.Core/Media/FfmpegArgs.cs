using System.Globalization;

namespace RappyRuns.Core.Media;

/// <summary>Inputs of <see cref="FfmpegArgs.Build"/> (<c>build-ffmpeg-args</c>'s keywords).</summary>
public sealed record FfmpegCaptureOptions
{
    /// <summary>gdigrab's <c>title=</c> input: the window's actual GetWindowTextW text (exact match).</summary>
    public string? WindowTitle { get; init; }

    public required string OutputPath { get; init; }

    /// <summary>The audio pipe (<see cref="FfmpegArgs.AudioPipeName"/>) when game audio rides along, else null.</summary>
    public string? AudioPipe { get; init; }

    /// <summary>ddagrab capture of this monitor; null with no <see cref="Wgc"/> means gdigrab.</summary>
    public CaptureMonitor? Monitor { get; init; }

    /// <summary>WGC raw-video pipe input; wins over every other source.</summary>
    public WgcCapturePlan? Wgc { get; init; }

    /// <summary>A <see cref="FfmpegArgs.HwEncoderCandidates"/> name, or null for libx264.</summary>
    public string? VideoEncoder { get; init; }

    /// <summary>The probe-verified QSV zero-copy chain (only meaningful with h264_qsv).</summary>
    public bool GpuChain { get; init; }

    public bool LowMemory { get; init; }

    public int Framerate { get; init; } = FfmpegArgs.Framerate;

    /// <summary>
    /// libx264 -threads. The Lisp computes <c>(encoder-thread-count)</c> inside
    /// the builder from NUMBER_OF_PROCESSORS; here it is an input so the argv
    /// is a pure function (default: the same computation).
    /// </summary>
    public int EncoderThreads { get; init; } = FfmpegArgs.EncoderThreadCount();
}

/// <summary>
/// Every ffmpeg argv the recorder spawns (media spec §1.5, §1.6, §1.8), byte
/// for byte the Lisp client's (recording.lisp:127-969). The comments in the
/// Lisp explain each option's field history; the short version is kept here
/// on the members, because every one of them fixed a real recording.
/// </summary>
public static class FfmpegArgs
{
    /// <summary>+record-framerate+.</summary>
    public const int Framerate = 30;

    /// <summary>+record-preset+.</summary>
    public const string Preset = "veryfast";

    /// <summary>+record-crf+ (29, not 28: pays for the dropped B-frames, run 92).</summary>
    public const int Crf = 29;

    /// <summary>+record-crf-low+: the low-memory x264 CRF.</summary>
    public const int CrfLow = 31;

    /// <summary>+record-max-threads+.</summary>
    public const int MaxThreads = 8;

    /// <summary>+record-max-height+: downscale cap, never an upscale.</summary>
    public const int MaxHeight = 1080;

    /// <summary>
    /// +hw-encoder-candidates+ in probe order. h264_mf is deliberately absent:
    /// MediaFoundation silently falls back to a SOFTWARE MFT (recording.lisp:174).
    /// </summary>
    public static readonly IReadOnlyList<string> HwEncoderCandidates = ["h264_nvenc", "h264_amf", "h264_qsv"];

    public const string HwBitrate = "3500k";
    public const string HwMaxrate = "7M";
    public const string HwBufsize = "14M";
    public const string HwBitrateLow = "2500k";
    public const string HwMaxrateLow = "5M";
    public const string HwBufsizeLow = "10M";

    /// <summary>+low-memory-threshold-gb+: under 12 GiB of RAM records at the low profile.</summary>
    public const int LowMemoryThresholdGb = 12;

    /// <summary>+record-loudness-lufs+ (issue 84: -16 -> -20 -> -24).</summary>
    public const int LoudnessLufs = -24;

    /// <summary>+gdigrab-probe-frames+.</summary>
    public const int GdigrabProbeFrames = 8;

    /// <summary><c>audio-pipe-name</c>: fixed, which is why only one client instance may record.</summary>
    public const string AudioPipeName = @"\\.\pipe\ephinea-ta-audio";

    /// <summary><c>video-pipe-name</c> (wgc-win32.lisp:261).</summary>
    public const string VideoPipeName = @"\\.\pipe\ephinea-ta-video";

    /// <summary>
    /// +record-color-tags-filter+: frame properties, not codec -color_* flags,
    /// which the bundled ffmpeg 8 encoders silently ignore (run 1368).
    /// </summary>
    public const string ColorTagsFilter = "setparams=color_primaries=bt709:color_trc=iec61966-2-1";

    /// <summary>
    /// <c>record-scale-filter</c>: fast_bilinear, NOT lanczos (lanczos dropped a
    /// live capture from 25 to 18 fps and desynced audio). The <c>\,</c> is a
    /// literal backslash-comma: the comma escape inside a filtergraph.
    /// </summary>
    public static readonly string ScaleFilter =
        $"scale=-2:trunc(min({MaxHeight}\\,ih)/2)*2:flags=fast_bilinear:out_color_matrix=bt709:out_range=tv";

    /// <summary>
    /// <c>logical-processor-count</c> (recording.lisp:134): NUMBER_OF_PROCESSORS,
    /// or 4 when unreadable. CL parse-integer tolerates surrounding whitespace
    /// and a sign.
    /// </summary>
    public static int LogicalProcessorCount()
    {
        var raw = Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS");
        return raw is not null && int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : 4;
    }

    /// <summary>
    /// <c>encoder-thread-count</c> (recording.lisp:140): half the logical
    /// processors, at least 2, at most 8 - x264's default 1.5x cores stole
    /// frame time from the game even at below-normal priority.
    /// </summary>
    public static int EncoderThreadCount(int? cores = null) =>
        Math.Max(2, Math.Min(MaxThreads, (int)Lisp.Floor(cores ?? LogicalProcessorCount(), 2)));

    /// <summary>
    /// <c>low-memory-machine-p</c> (recording.lisp:234): physical RAM under 12
    /// GiB. Unknown (null) is NOT low - never degrade on an unreadable machine.
    /// </summary>
    public static bool LowMemoryMachine(long? physicalBytes) =>
        physicalBytes is { } b && b < LowMemoryThresholdGb * (1L << 30);

    /// <summary><c>hw-encoder-probe-args</c> (recording.lisp:240).</summary>
    public static IReadOnlyList<string> HwEncoderProbeArgs(string encoder) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", "color=black:size=256x256:rate=30",
        "-frames:v", "8", "-c:v", encoder, "-f", "null", "-",
    ];

    /// <summary>
    /// <c>hw-gpu-chain-probe-args</c> (recording.lisp:248): the exact QSV
    /// zero-copy options <see cref="Build"/> uses, exercised at startup.
    /// </summary>
    public static IReadOnlyList<string> HwGpuChainProbeArgs() =>
    [
        "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", "ddagrab=output_idx=0:framerate=30:draw_mouse=0",
        "-frames:v", "8",
        "-vf", "hwmap=derive_device=qsv,vpp_qsv=w=1280:h=720:format=nv12:out_color_matrix=bt709:out_range=tv",
        "-c:v", "h264_qsv",
        "-f", "null", "-",
    ];

    /// <summary>
    /// <c>gdigrab-probe-args</c> (recording.lisp:663): -loglevel info because
    /// blackdetect reports there and stderr IS the probe's output.
    /// </summary>
    public static IReadOnlyList<string> GdigrabProbeArgs(string? windowTitle) =>
    [
        "-hide_banner", "-loglevel", "info", "-nostats",
        "-f", "gdigrab",
        "-framerate", Framerate.ToString(CultureInfo.InvariantCulture),
        "-draw_mouse", "0",
        "-i", "title=" + Lisp.Opt(windowTitle),
        "-frames:v", GdigrabProbeFrames.ToString(CultureInfo.InvariantCulture),
        "-vf", "blackdetect=d=0:pix_th=0.10",
        "-f", "null", "-",
    ];

    /// <summary>
    /// <c>record-scale-dimensions</c> (recording.lisp:265): the literal even
    /// WxH vpp_qsv gets. CL round is half-to-even (exact rational here).
    /// </summary>
    public static (int Width, int Height) RecordScaleDimensions(int width, int height, int cap = MaxHeight)
    {
        if (height <= cap)
            return (2 * (int)Lisp.Floor(width, 2), 2 * (int)Lisp.Floor(height, 2));
        return (2 * (int)Lisp.Round((long)width * cap, 2L * height), 2 * (int)Lisp.Floor(cap, 2));
    }

    /// <summary>
    /// <c>video-input-args</c> (recording.lisp:706). A secondary-adapter monitor
    /// gets ddagrab's device created explicitly; adapter 0 leaves the argv as is.
    /// </summary>
    public static List<string> VideoInputArgs(string? windowTitle, CaptureMonitor? monitor, WgcCapturePlan? wgc, int framerate)
    {
        var args = new List<string>();
        if (wgc is null && CaptureGeometry.SecondaryAdapter(monitor) is { } adapter)
        {
            args.Add("-init_hw_device");
            args.Add(string.Create(CultureInfo.InvariantCulture, $"d3d11va=dda:{adapter}"));
            args.Add("-filter_hw_device");
            args.Add("dda");
        }
        var rate = framerate.ToString(CultureInfo.InvariantCulture);
        if (wgc is not null)
        {
            args.AddRange([
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", string.Create(CultureInfo.InvariantCulture, $"{wgc.Width}x{wgc.Height}"),
                "-framerate", rate,
                "-i", wgc.Pipe,
            ]);
        }
        else if (monitor is not null)
        {
            args.AddRange([
                "-f", "lavfi",
                "-i", string.Create(CultureInfo.InvariantCulture, $"ddagrab=output_idx={monitor.OutputIdx}:framerate={framerate}:draw_mouse=0"),
            ]);
        }
        else
        {
            args.AddRange([
                "-f", "gdigrab",
                "-framerate", rate,
                "-draw_mouse", "0",
                "-i", "title=" + Lisp.Opt(windowTitle),
            ]);
        }
        return args;
    }

    /// <summary>
    /// <c>video-encoder-args</c> (recording.lisp:750): a GPU encoder at VBR or
    /// libx264 CRF; -bf 0 on both (the B-frame reorder delay skewed A/V sync
    /// per player, run 92).
    /// </summary>
    public static List<string> VideoEncoderArgs(string? videoEncoder, bool lowMemory, int encoderThreads)
    {
        if (videoEncoder is not null)
        {
            return
            [
                "-c:v", videoEncoder,
                "-b:v", lowMemory ? HwBitrateLow : HwBitrate,
                "-maxrate", lowMemory ? HwMaxrateLow : HwMaxrate,
                "-bufsize", lowMemory ? HwBufsizeLow : HwBufsize,
                "-bf", "0",
            ];
        }
        return
        [
            "-c:v", "libx264",
            "-preset", Preset,
            "-threads", encoderThreads.ToString(CultureInfo.InvariantCulture),
            "-crf", (lowMemory ? CrfLow : Crf).ToString(CultureInfo.InvariantCulture),
            "-bf", "0",
            "-pix_fmt", "yuv420p",
        ];
    }

    /// <summary>
    /// <c>capture-filter-chain</c> (recording.lisp:771): crop, scale (or the
    /// QSV zero-copy vpp), a YUV format right after the converting filter, and
    /// the bt709 tags. The zero-copy chain needs a cropless default-adapter
    /// monitor, a HW encoder and the verified chain flag.
    /// </summary>
    public static string CaptureFilterChain(CaptureMonitor? monitor, WgcCapturePlan? wgc, string? videoEncoder, bool gpuChain)
    {
        var crop = monitor?.Crop;
        string head;
        if (monitor is not null && crop is null && videoEncoder is not null && gpuChain &&
            CaptureGeometry.SecondaryAdapter(monitor) is null)
        {
            var (w, h) = RecordScaleDimensions(monitor.Width, monitor.Height);
            head = string.Create(CultureInfo.InvariantCulture,
                $"hwmap=derive_device=qsv,vpp_qsv=w={w}:h={h}:format=nv12:out_color_matrix=bt709:out_range=tv");
        }
        else
        {
            string @base;
            if (wgc?.Crop is { } wc)
                @base = string.Create(CultureInfo.InvariantCulture, $"crop={wc.Width}:{wc.Height}:{wc.X}:{wc.Y},{ScaleFilter}");
            else if (crop is { } c)
                @base = string.Create(CultureInfo.InvariantCulture, $"hwdownload,format=bgra,crop={c.Width}:{c.Height}:{c.X}:{c.Y},{ScaleFilter}");
            else if (monitor is not null)
                @base = "hwdownload,format=bgra," + ScaleFilter;
            else
                @base = ScaleFilter;
            head = @base + (videoEncoder is not null ? ",format=nv12" : ",format=yuv420p");
        }
        return head + "," + ColorTagsFilter;
    }

    /// <summary>
    /// <c>build-ffmpeg-args</c> (recording.lisp:825): the live capture argv
    /// without the program. Fragmented MP4 so a killed ffmpeg still leaves a
    /// playable file; -probesize 32 -analyzeduration 0 so the audio pipe opens
    /// within a frame of video time 0; NO live -af (loudnorm throttled the
    /// video to 17 fps - it happens in the remux instead).
    /// </summary>
    public static List<string> Build(FfmpegCaptureOptions o)
    {
        var args = new List<string> { "-y", "-loglevel", "error", "-probesize", "32", "-analyzeduration", "0" };
        args.AddRange(VideoInputArgs(o.WindowTitle, o.Monitor, o.Wgc, o.Framerate));
        if (o.AudioPipe is not null)
            args.AddRange(["-f", "s16le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024", "-i", o.AudioPipe]);
        args.AddRange(VideoEncoderArgs(o.VideoEncoder, o.LowMemory, o.EncoderThreads));
        args.Add("-vf");
        args.Add(CaptureFilterChain(o.Monitor, o.Wgc, o.VideoEncoder, o.GpuChain));
        if (o.AudioPipe is not null)
            args.AddRange(["-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-movflags", "+frag_keyframe+empty_moov", o.OutputPath]);
        return args;
    }

    /// <summary>
    /// <c>build-remux-args</c> (recording.lisp:902): stream-copy video, loudnorm
    /// the audio, moov up front. <c>-t</c> sits AFTER <c>-i</c> (an output
    /// option: stop writing, not an input seek) and is formatted from exact
    /// milliseconds by hand - "702.345", never through a float. No timestamp
    /// correction of any kind (run 88).
    /// </summary>
    public static List<string> BuildRemux(string inputPath, string outputPath, long? durationMs = null)
    {
        var args = new List<string> { "-y", "-loglevel", "error", "-i", inputPath };
        if (durationMs is { } ms)
        {
            args.Add("-t");
            args.Add(string.Create(CultureInfo.InvariantCulture, $"{Lisp.Floor(ms, 1000)}.{Lisp.Mod(ms, 1000):000}"));
        }
        args.AddRange([
            "-c:v", "copy",
            "-af", string.Create(CultureInfo.InvariantCulture, $"loudnorm=I={LoudnessLufs}:TP=-1.5:LRA=11,aresample=48000"),
            "-c:a", "aac", "-b:a", "160k",
            "-movflags", "+faststart",
            outputPath,
        ]);
        return args;
    }

    /// <summary><c>remove-subseq</c>: the list without the first occurrence of the consecutive run.</summary>
    internal static List<string> RemoveSubseq(IReadOnlyList<string> list, IReadOnlyList<string> sub)
    {
        for (var i = 0; i + sub.Count <= list.Count; i++)
        {
            var match = true;
            for (var j = 0; j < sub.Count && match; j++) match = list[i + j] == sub[j];
            if (match)
                return [.. list.Take(i), .. list.Skip(i + sub.Count)];
        }
        return [.. list];
    }

    /// <summary>
    /// <c>strip-audio-args</c> (recording.lisp:948): the video-only fallback when
    /// the audio session cannot start (ffmpeg would hang opening an unserved pipe).
    /// </summary>
    public static List<string> StripAudioArgs(IReadOnlyList<string> args, string? audioPipe) =>
        RemoveSubseq(
            RemoveSubseq(args, ["-f", "s16le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024", "-i", Lisp.Opt(audioPipe)]),
            ["-c:a", "aac", "-b:a", "160k"]);

    /// <summary>
    /// <c>retarget-audio-args</c> (recording.lisp:957): the placeholder
    /// <c>-f s16le -ar 48000 -ac 2</c> rewritten to the audio session's actual
    /// capture format, keyed off the first "s16le" token.
    /// </summary>
    public static List<string> RetargetAudioArgs(IReadOnlyList<string> args, string sampleFormat, int rate, int channels)
    {
        var copy = args.ToList();
        var p = copy.IndexOf("s16le");
        if (p < 0) return copy;
        copy[p] = sampleFormat;
        copy[p + 2] = rate.ToString(CultureInfo.InvariantCulture);
        copy[p + 4] = channels.ToString(CultureInfo.InvariantCulture);
        return copy;
    }
}
