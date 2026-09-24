using System.Diagnostics;
using RappyRuns.Core.Media;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Tests.Media;

/// <summary>A Lisp <c>check</c>: a labeled assertion, so each ported check keeps its label.</summary>
internal static class Check
{
    public static void That(string label, bool condition) => Assert.True(condition, label);
}

/// <summary>One side effect the mock backend saw: (kind, arguments...).</summary>
internal sealed record MockEvent(string Kind, params object?[] Args);

internal sealed record MockHandle(string Name) : ICaptureHandle;

/// <summary>
/// The Lisp <c>mock-backend</c> (tests-recorder.lisp:7): records every side
/// effect in order; knobs for liveness, start results and the remux outcome.
/// </summary>
internal sealed class MockBackend : ICaptureBackend
{
    public static readonly MockHandle CaptureToken = new("mock-capture");
    public static readonly MockHandle RemuxToken = new("mock-remux");

    public List<MockEvent> Events { get; } = [];

    public bool Alive { get; set; } = true;

    public bool StartOk { get; set; } = true;

    /// <summary>False: the remux exits as soon as spawned.</summary>
    public bool RemuxAlive { get; set; }

    public bool RemuxOk { get; set; } = true;

    public bool RemuxStartOk { get; set; } = true;

    public List<string> Stale { get; set; } = [];

    public List<RecordingFile> Recordings { get; set; } = [];

    public CaptureMonitor? Monitor { get; set; }

    public WgcCapturePlan? Wgc { get; set; }

    public IEnumerable<MockEvent> Of(string kind) => Events.Where(e => e.Kind == kind);

    public int Count(string kind) => Of(kind).Count();

    public CaptureStartResult StartCapture(string ffmpegPath, IReadOnlyList<string> args, string outputPath, string? audioPipe, int? audioPid, object? wgcSession)
    {
        Events.Add(new MockEvent("start", ffmpegPath, args, outputPath, audioPipe, audioPid));
        return StartOk ? CaptureStartResult.Ok(CaptureToken) : CaptureStartResult.Fail("mock start failure");
    }

    public bool IsAlive(ICaptureHandle capture) => ReferenceEquals(capture, RemuxToken) ? RemuxAlive : Alive;

    public void RequestStop(ICaptureHandle capture) => Events.Add(new MockEvent("stop"));

    public void Kill(ICaptureHandle capture) => Events.Add(new MockEvent("kill"));

    public void Close(ICaptureHandle capture) => Events.Add(new MockEvent("close"));

    public CaptureStartResult StartRemux(string ffmpegPath, IReadOnlyList<string> args)
    {
        Events.Add(new MockEvent("remux", ffmpegPath, args));
        return RemuxStartOk ? CaptureStartResult.Ok(RemuxToken) : CaptureStartResult.Fail("mock remux failure");
    }

    public bool Succeeded(ICaptureHandle capture) => RemuxOk;

    public void RenameFile(string from, string to) => Events.Add(new MockEvent("rename", from, to));

    public void DeleteFile(string path) => Events.Add(new MockEvent("delete", path));

    public CaptureMonitor? CaptureMonitor() => Monitor;

    public WgcCapturePlan? WgcCapture() => Wgc;

    public IReadOnlyList<string> ListStaleFiles(string dir) => Stale;

    public IReadOnlyList<RecordingFile> ListRecordings(string dir) => Recordings;
}

/// <summary>Builders mirroring tests-recorder.lisp's helpers.</summary>
internal static class MediaFixtures
{
    public const string Title = "Ephinea PSOBB";

    /// <summary>A record dir that never exists, so deduplication never kicks in.</summary>
    public static readonly string RecordDir =
        Path.Combine(Path.GetTempPath(), "rr-media-tests-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;

    /// <summary>(encode-universal-time 0 30 21 4 7 2026): local 2026-07-04 21:30.</summary>
    public static long FinishedAt => Lisp.EncodeUniversalTime(new DateTime(2026, 7, 4, 21, 30, 0));

    /// <summary><c>make-test-run</c>.</summary>
    public static Plist Run(string slug = "ep1-test-quest", long timeMs = 599123, bool aborted = false)
    {
        var sexp = new List<SexpNode>
        {
            SexpNode.Kw("quest-slug"), SexpNode.Str(slug),
            SexpNode.Kw("time-ms"), SexpNode.Int(timeMs),
            SexpNode.Kw("finished-at"), SexpNode.Int(FinishedAt),
        };
        if (aborted) sexp.AddRange([SexpNode.Kw("aborted"), SexpNode.T]);
        return Plist.From(new SList(sexp))!;
    }

    /// <summary>A plist from alternating key/value pairs (strings, longs, bools).</summary>
    public static Plist P(params object[] kv)
    {
        var items = new List<SexpNode>();
        for (var i = 0; i < kv.Length; i += 2)
        {
            items.Add(SexpNode.Kw((string)kv[i]));
            items.Add(kv[i + 1] switch
            {
                string s => SexpNode.Str(s),
                int n => SexpNode.Int(n),
                long n => SexpNode.Int(n),
                bool b => SexpNode.Bool(b),
                SexpNode node => node,
                _ => throw new ArgumentException("unsupported value"),
            });
        }
        return Plist.From(new SList(items))!;
    }

    public sealed class Harness
    {
        public MockBackend Backend { get; } = new();

        public RecordingSettings Settings { get; set; } = new() { RecordEnabled = true };

        public HwEncoderStatus Hw { get; } = new();

        public List<RecorderNotice> Notices { get; } = [];

        public Recorder Recorder { get; }

        public Harness(Action<string, Plist>? onKeep = null)
        {
            Recorder = new Recorder(Backend, new RecorderEnvironment
            {
                Settings = () => Settings,
                RecordDir = () => RecordDir,
                FfmpegPath = () => "ffmpeg.exe",
                Hw = Hw,
                EncoderThreads = () => 4,
                Notify = Notices.Add,
                Sleep = _ => { },
            })
            {
                OnKeep = onKeep,
            };
        }

        public void Step(bool inQuest, params Plist[] runs) => Recorder.Step(inQuest, runs, Title);

        public IReadOnlyList<string> StartArgs => (IReadOnlyList<string>)Backend.Of("start").First().Args[1]!;

        public IReadOnlyList<string> RemuxArgs => (IReadOnlyList<string>)Backend.Of("remux").First().Args[1]!;

        /// <summary>Pretend the capture has been running for <paramref name="seconds"/>.</summary>
        public void AgeCapture(int seconds) =>
            Recorder.CaptureStartTicks = Stopwatch.GetTimestamp() - seconds * Stopwatch.Frequency;
    }

    /// <summary>The value after the first <paramref name="flag"/>, or null.</summary>
    public static string? After(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }
}
