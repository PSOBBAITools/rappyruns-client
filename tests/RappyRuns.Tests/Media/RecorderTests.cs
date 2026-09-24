using System.Globalization;
using RappyRuns.Core.Media;
using RappyRuns.Core.Sexp;
using static RappyRuns.Tests.Media.MediaFixtures;

namespace RappyRuns.Tests.Media;

/// <summary>
/// Port of <c>run-recorder-tests</c>' state-machine checks
/// (client/tests/tests-recorder.lisp:100-398, 950-997), one Fact per Lisp
/// scenario, each Lisp <c>check</c> label kept on its assertion.
/// </summary>
public class RecorderTests
{
    private const bool Idle = false;
    private const bool InQuest = true;

    [Fact]
    public void HappyPath_QuestCompletes_VideoKeptUnderRunName()
    {
        var h = new Harness();
        h.Step(Idle);
        h.Step(InQuest);
        Check.That("recording starts on :idle -> :in-quest", h.Recorder.State == RecorderState.Recording);
        var start = h.Backend.Of("start").First();
        Check.That("ffmpeg args capture the window title", ((IReadOnlyList<string>)start.Args[1]!).Contains("title=Ephinea PSOBB"));
        Check.That("ffmpeg writes to a rec-tmp file", ((string)start.Args[2]!).Contains("rec-tmp-", StringComparison.Ordinal));
        h.Step(InQuest);
        // The full clear completes and the detector flips to :idle on the same frame.
        h.Step(Idle, Run());
        Check.That("stop is requested when the detector goes idle",
            h.Recorder.State == RecorderState.Stopping && h.Backend.Count("stop") == 1);
        h.Backend.Alive = false;
        h.Step(Idle);
        var remux = h.RemuxArgs;
        Check.That("kept capture is remuxed with the moov up front",
            h.Recorder.State == RecorderState.Remuxing && remux.Contains("+faststart"));
        Check.That("remux reads the tmp file", remux[4].Contains("rec-tmp-", StringComparison.Ordinal));
        Check.That("remux writes the final name with quest, time and date",
            remux[^1].Contains("ep1-test-quest 9'59.123 (2026-07-04 2130).mp4", StringComparison.Ordinal));
        Check.That("the trim engages without a stamped offset", remux.Contains("-t"));
        h.Step(Idle);
        var delete = h.Backend.Of("delete").FirstOrDefault();
        Check.That("successful remux deletes the tmp and skips the rename",
            delete is not null && ((string)delete.Args[0]!).Contains("rec-tmp-", StringComparison.Ordinal) && h.Backend.Count("rename") == 0);
        Check.That("recorder returns to idle after finalize",
            h.Recorder.State == RecorderState.Idle && h.Backend.Count("close") == 2);
    }

    [Fact]
    public void CompletedRunsCarryVideoOffset()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.AgeCapture(700);
        var run = Run(timeMs: 599123);
        h.Step(InQuest, run);
        var offset = run.Get("VIDEO-OFFSET-MS")?.AsLong;
        // 700 s elapsed - 599.123 s run = ~100.9 s into the video.
        Check.That("completed runs carry the video offset of their start", offset is >= 100000 and <= 102000);
        var run2 = Run(timeMs: 1);
        run2.Set("VIDEO-OFFSET-MS", SexpNode.Int(42));
        h.Step(InQuest, run2);
        Check.That("an existing video offset is left alone", run2.Get("VIDEO-OFFSET-MS")?.AsLong == 42);
    }

    [Fact]
    public void RemuxTrimsTailAfterLastRun()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.AgeCapture(700);
        h.Step(Idle, Run(timeMs: 599123));
        h.Backend.Alive = false;
        h.Step(Idle);
        var args = h.RemuxArgs.ToList();
        var at = args.IndexOf("-t");
        Check.That("the remux trims the tail after the last run",
            at >= 0 && double.Parse(args[at + 1], CultureInfo.InvariantCulture) is >= 702 and <= 703);
        Check.That("the trim caps the output rather than seeking the input", at > args.IndexOf("-i"));
    }

    [Fact]
    public void RunOlderThanCapture_NoOffset_TrimAnyway()
    {
        var h = new Harness();
        h.Step(InQuest);
        var run = Run(timeMs: 599123);
        // Capture 5 s old, run claims 599 s: offset would be negative.
        h.AgeCapture(5);
        h.Step(Idle, run);
        h.Backend.Alive = false;
        h.Step(Idle);
        Check.That("a run older than its capture carries no offset", run.Get("VIDEO-OFFSET-MS") is null);
        var args = h.RemuxArgs.ToList();
        var at = args.IndexOf("-t");
        Check.That("the tail is trimmed anyway, off the capture's own clock",
            at >= 0 && double.Parse(args[at + 1], CultureInfo.InvariantCulture) is >= 7 and <= 8);
    }

    [Fact]
    public void AbandonedQuestVideoDeleted()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.Step(Idle);
        h.Backend.Alive = false;
        h.Step(Idle);
        Check.That("abandoned quest video is deleted",
            h.Backend.Count("delete") == 1 && h.Backend.Count("rename") == 0 && h.Recorder.State == RecorderState.Idle);
    }

    [Fact]
    public void SegmentOnlyCaptureKeptUnderSegmentName()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.Step(InQuest, Run(slug: "ep1-seg", timeMs: 120500));
        Check.That("segment completion does not stop the capture", h.Recorder.State == RecorderState.Recording);
        h.Step(Idle);
        h.Backend.Alive = false;
        h.Step(Idle);
        Check.That("segment-only capture is kept under the segment name",
            h.RemuxArgs[^1].Contains("ep1-seg 2'00.500", StringComparison.Ordinal));
    }

    [Fact]
    public void FullClearNamesTheVideo()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.Step(InQuest, Run(slug: "ep1-seg", timeMs: 120500));
        h.Step(Idle, Run(slug: "ep1-full", timeMs: 599123));
        h.Backend.Alive = false;
        h.Step(Idle);
        Check.That("full clear (longest run) names the video",
            h.RemuxArgs[^1].Contains("ep1-full 9'59.123", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletedSegmentOutranksLongerAbortedRun()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.Step(InQuest, Run(slug: "ep2-gdv-reset", timeMs: 145160));
        h.Step(Idle, Run(slug: "ep2-gdv", timeMs: 148594, aborted: true));
        h.Backend.Alive = false;
        h.Step(Idle);
        Check.That("a completed segment outranks a longer aborted run",
            h.RemuxArgs[^1].Contains("ep2-gdv-reset 2'25.160", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedStart_ErrorSurfaced_RetriedNextQuest()
    {
        var h = new Harness();
        h.Backend.StartOk = false;
        h.Step(InQuest);
        Check.That("failed start leaves the recorder idle with an error",
            h.Recorder.State == RecorderState.Idle && h.Recorder.LastError is not null);
        h.Step(InQuest);
        Check.That("failed start is not retried mid-quest", h.Backend.Count("start") == 1);
        h.Step(Idle);
        h.Step(InQuest);
        Check.That("failed start is retried on the next quest", h.Backend.Count("start") == 2);
    }

    [Fact]
    public void FfmpegDiesMidQuest()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.Backend.Alive = false;
        h.Step(InQuest);
        Check.That("ffmpeg dying mid-quest deletes the file and reports",
            h.Recorder.State == RecorderState.Idle && h.Backend.Count("delete") == 1 && h.Recorder.LastError is not null);
    }

    [Fact]
    public void UnresponsiveFfmpegKilledOnceAndKept()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.Step(Idle, Run());
        h.Recorder.StopDeadline = System.Diagnostics.Stopwatch.GetTimestamp() - 1;
        h.Step(Idle);
        Check.That("unresponsive ffmpeg is killed after the grace period", h.Backend.Count("kill") == 1);
        h.Step(Idle);
        Check.That("kill happens only once", h.Backend.Count("kill") == 1);
        h.Backend.Alive = false;
        h.Step(Idle);
        Check.That("killed capture is still kept (remuxed from the fragments)", h.Backend.Count("remux") == 1);
    }

    [Fact]
    public void FailedRemuxFallsBackToFragmentedOriginal()
    {
        var h = new Harness();
        h.Backend.RemuxOk = false;
        h.Step(InQuest);
        h.Step(Idle, Run());
        h.Backend.Alive = false;
        h.Step(Idle);
        h.Step(Idle);
        var rename = h.Backend.Of("rename").FirstOrDefault();
        Check.That("failed remux falls back to the fragmented original",
            rename is not null && ((string)rename.Args[0]!).Contains("rec-tmp-", StringComparison.Ordinal) &&
            h.Recorder.State == RecorderState.Idle);
        Check.That("failed remux drops its partial output first",
            Equals(rename!.Args[1], h.Backend.Of("delete").First().Args[0]));
        // The untrimmed fallback is ballooned (save-recording).
        Check.That("untrimmed fallback notifies and reports",
            h.Notices.Any(n => n.TitleKey == "notify-untrimmed-title") &&
            h.Recorder.LastError == "remux failed; recording kept whole - its tail is untrimmed");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OnKeepIsToldWhetherTheRecordingIsUntrimmed(bool remuxOk, bool untrimmed)
    {
        var h = new Harness();
        var kept = new List<bool>();
        h.Recorder.OnKeep = (_, _, u) => kept.Add(u);
        h.Backend.RemuxOk = remuxOk;
        h.Step(InQuest);
        h.Step(Idle, Run());
        h.Backend.Alive = false;
        h.Step(Idle);
        h.Step(Idle);
        Assert.Equal([untrimmed], kept);
    }

    [Fact]
    public void UnstartableRemuxRenamesAtOnce()
    {
        var h = new Harness();
        h.Backend.RemuxStartOk = false;
        h.Step(InQuest);
        h.Step(Idle, Run());
        h.Backend.Alive = false;
        h.Step(Idle);
        Check.That("unstartable remux falls back to a rename at once",
            h.Recorder.State == RecorderState.Idle && h.Backend.Count("rename") == 1);
    }

    [Fact]
    public void HungRemuxKilledAfterGrace()
    {
        var h = new Harness();
        h.Backend.RemuxAlive = true;
        h.Backend.RemuxOk = false;
        h.Step(InQuest);
        h.Step(Idle, Run());
        h.Backend.Alive = false;
        h.Step(Idle);
        h.Step(Idle);
        Check.That("healthy remux is not killed", h.Backend.Count("kill") == 0);
        h.Recorder.RemuxDeadline = System.Diagnostics.Stopwatch.GetTimestamp() - 1;
        h.Step(Idle);
        Check.That("hung remux is killed after the grace period", h.Backend.Count("kill") == 1);
        h.Backend.RemuxAlive = false;
        h.Step(Idle);
        Check.That("killed remux still keeps the fragmented original",
            h.Recorder.State == RecorderState.Idle && h.Backend.Count("rename") == 1);
    }

    [Fact]
    public void ShutdownMidRecordingKillsAndKeeps()
    {
        var h = new Harness();
        h.Step(InQuest);
        h.Step(InQuest, Run());
        h.Recorder.Shutdown(TimeSpan.Zero);
        Check.That("shutdown mid-recording kills and keeps the completed run",
            h.Recorder.State == RecorderState.Idle && h.Backend.Count("kill") == 1 &&
            h.Backend.Count("remux") == 1 && h.Backend.Count("rename") == 0);
    }

    [Fact]
    public void NoWindowTitleMeansNoCapture()
    {
        var h = new Harness();
        h.Recorder.Step(InQuest, [], null);
        Check.That("no window title means no capture", h.Recorder.State == RecorderState.Idle && h.Backend.Events.Count == 0);
    }

    [Fact]
    public void StaleRecordingsDeletedAtStartup()
    {
        var h = new Harness();
        h.Backend.Stale = ["a/rec-tmp-1.mp4", "a/rec-tmp-2.mp4"];
        h.Recorder.CleanupStaleRecordings();
        Check.That("stale recordings are deleted at startup",
            h.Backend.Of("delete").Select(e => (string)e.Args[0]!).SequenceEqual(["a/rec-tmp-1.mp4", "a/rec-tmp-2.mp4"]));
    }

    [Fact]
    public void DisabledRecorderDoesNothing()
    {
        var h = new Harness { Settings = new RecordingSettings { RecordEnabled = false } };
        h.Step(InQuest);
        h.Step(Idle);
        Check.That("disabled recorder does nothing", h.Recorder.State == RecorderState.Idle && h.Backend.Events.Count == 0);
    }

    [Fact]
    public void TrackingOnlySuppressesRecording()
    {
        var settings = new RecordingSettings { RecordEnabled = true, TrackingOnly = true };
        Check.That("tracking-only mode turns recording off", !settings.RecordingEnabled);
        var h = new Harness { Settings = settings };
        h.Step(InQuest);
        h.Step(Idle);
        Check.That("tracking-only recorder never starts a capture",
            h.Recorder.State == RecorderState.Idle && h.Backend.Events.Count == 0);
        Check.That("recording stays on with tracking-only off",
            new RecordingSettings { RecordEnabled = true, TrackingOnly = false }.RecordingEnabled);
    }

    [Fact]
    public void RecorderAsksBackendForCaptureMonitor()
    {
        var full = new Harness();
        full.Backend.Monitor = new CaptureMonitor(0, null, 1920, 1080);
        full.Step(InQuest);
        Check.That("recorder records a fullscreen game via ddagrab",
            full.StartArgs.Any(a => a.Contains("ddagrab=output_idx=0", StringComparison.Ordinal)));

        var windowed = new Harness();
        windowed.Backend.Monitor = new CaptureMonitor(1, null, 1920, 1080, new CropRect(152, 90, 1600, 900));
        windowed.Step(InQuest);
        Check.That("recorder records a windowed game via ddagrab + crop",
            windowed.StartArgs.Any(a => a.Contains("ddagrab=output_idx=1", StringComparison.Ordinal)) &&
            windowed.StartArgs.Any(a => a.Contains("crop=1600:900:152:90", StringComparison.Ordinal)));

        var none = new Harness();
        none.Step(InQuest);
        Check.That("recorder falls back to gdigrab without a capture monitor", none.StartArgs.Contains("gdigrab"));
    }

    [Fact]
    public void RecorderPassesAudioPipeAndPid()
    {
        var h = new Harness { Settings = new RecordingSettings { RecordEnabled = true, RecordAudio = true } };
        h.Recorder.AudioTargetPid = 1234;
        h.Step(InQuest);
        var start = h.Backend.Of("start").First();
        Check.That("recorder hands the backend the audio pipe and pid",
            (string?)start.Args[3] == FfmpegArgs.AudioPipeName && (int?)start.Args[4] == 1234);

        var noPid = new Harness { Settings = new RecordingSettings { RecordEnabled = true, RecordAudio = true } };
        noPid.Recorder.AudioTargetPid = null;
        noPid.Step(InQuest);
        Check.That("no attached game pid means no audio pipe", noPid.Backend.Of("start").First().Args[3] is null);

        var off = new Harness { Settings = new RecordingSettings { RecordEnabled = true, RecordAudio = false } };
        off.Recorder.AudioTargetPid = 1234;
        off.Step(InQuest);
        Check.That("audio can be disabled in config", off.Backend.Of("start").First().Args[3] is null);
    }

    // Beyond the Lisp suite: the recorder's own notification and WGC routing rules (media spec §1.3).

    [Fact]
    public void WgcPlanWinsOverMonitorAndIsHandedToTheBackend()
    {
        var h = new Harness();
        h.Backend.Monitor = new CaptureMonitor(0, 0, 1920, 1080);
        h.Backend.Wgc = new WgcCapturePlan("session", FfmpegArgs.VideoPipeName, 1296, 999, new CropRect(8, 31, 1280, 960));
        h.Step(InQuest);
        Check.That("wgc argv is used when a plan exists",
            h.StartArgs.Contains("rawvideo") && !h.StartArgs.Any(a => a.Contains("ddagrab", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoticesFireOncePerStreakOrProcess()
    {
        var h = new Harness();
        h.Backend.StartOk = false;
        h.Step(InQuest);
        h.Step(Idle);
        h.Step(InQuest);
        Check.That("one capture-failed balloon per failure streak",
            h.Notices.Count(n => n.TitleKey == "notify-capture-failed-title") == 1 &&
            h.Notices[0].TextKey == "notify-capture-failed-text");
        h.Backend.StartOk = true;
        h.Backend.Monitor = new CaptureMonitor(0, 0, 1920, 1080, new CropRect(0, 0, 1280, 960));
        h.Step(Idle);
        h.Step(InQuest);
        Check.That("a windowed ddagrab capture warns about overlap once",
            h.Notices.Count(n => n.TitleKey == "notify-overlap-title") == 1);
    }

    [Fact]
    public void SmartAppControlBlockGetsItsOwnText()
    {
        var backend = new BlockingBackend();
        var notices = new List<RecorderNotice>();
        var recorder = new Recorder(backend, new RecorderEnvironment
        {
            RecordDir = () => RecordDir,
            FfmpegPath = () => "ffmpeg.exe",
            Notify = notices.Add,
        });
        recorder.Step(InQuest, [], Title);
        Check.That("error 4551 selects the blocked text",
            notices.Single().TextKey == "notify-capture-blocked-text" &&
            recorder.LastError == "could not start ffmpeg.exe (Windows error 4551)");
    }

    [Fact]
    public void SpawnFailedProbeRetriesAndWarnsSoftwareEncode()
    {
        var probes = 0;
        var notices = new List<RecorderNotice>();
        var hw = new HwEncoderStatus { State = HwProbeState.SpawnFailed };
        var backend = new MockBackend();
        var recorder = new Recorder(backend, new RecorderEnvironment
        {
            RecordDir = () => RecordDir,
            FfmpegPath = () => "ffmpeg.exe",
            Hw = hw,
            StartHwEncoderProbe = () => probes++,
            Notify = notices.Add,
        });
        recorder.Step(InQuest, [], Title);
        Check.That("a :spawn-failed verdict re-probes at capture start", probes == 1);
        Check.That("software fallback is announced once",
            notices.Count(n => n.TitleKey == "notify-software-encode-title" && n.Icon == NoticeIcon.Info) == 1);
    }

    [Fact]
    public void SweepOnlyWhileIdle()
    {
        var h = new Harness();
        h.Backend.Recordings = [new RecordingFile("a.mp4", 500, 100), new RecordingFile("b.mp4", 500, 200)];
        Check.That("idle sweep deletes the oldest over the cap",
            h.Recorder.SweepRecordings(600, [], []).SequenceEqual(["a.mp4"]) && h.Backend.Count("delete") == 1);
        h.Step(InQuest);
        Check.That("no sweep while recording", h.Recorder.SweepRecordings(1, [], []).Count == 0);
        Check.That("no cap, no sweep", new Harness().Recorder.SweepRecordings(null, [], []).Count == 0);
    }

    private sealed class BlockingBackend : ICaptureBackend
    {
        public CaptureStartResult StartCapture(string ffmpegPath, IReadOnlyList<string> args, string outputPath, string? audioPipe, int? audioPid, object? wgcSession) =>
            CaptureStartResult.Fail("could not start ffmpeg.exe (Windows error 4551)");

        public bool IsAlive(ICaptureHandle capture) => false;

        public void RequestStop(ICaptureHandle capture) { }

        public void Kill(ICaptureHandle capture) { }

        public void Close(ICaptureHandle capture) { }

        public CaptureStartResult StartRemux(string ffmpegPath, IReadOnlyList<string> args) => CaptureStartResult.Fail("no");

        public bool Succeeded(ICaptureHandle capture) => false;

        public void RenameFile(string from, string to) { }

        public void DeleteFile(string path) { }

        public IReadOnlyList<string> ListStaleFiles(string dir) => [];

        public IReadOnlyList<RecordingFile> ListRecordings(string dir) => [];
    }
}
