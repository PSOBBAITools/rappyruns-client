using System.Text.Json.Nodes;
using RappyRuns.Core.Api;
using RappyRuns.Core.Config;
using RappyRuns.Core.Game;
using RappyRuns.Core.Ghost;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Media;
using RappyRuns.Core.Store;
using RappyRuns.Host;
using RappyRuns.Tests.Api;
using RappyRuns.Tests.Media;
using static RappyRuns.Tests.Game.GameTestKit;

namespace RappyRuns.Tests.Host;

/// <summary>
/// The composition root's poll loop end to end, without a game, a network or
/// ffmpeg: a scripted PSOBB process plays Towards the Future, and the run must
/// come out queued, recorded (video offset stamped before the enqueue),
/// submitted through the real API client and adapters, and celebrated.
/// </summary>
public sealed class PollLoopSmokeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rr-host-" + Guid.NewGuid().ToString("N"));

    public PollLoopSmokeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A PSOBB process whose memory the test swaps frame by frame.</summary>
    private sealed class FakeProcess(IMemoryReader inner) : IProcessMemoryReader
    {
        public IMemoryReader Inner { get; set; } = inner;
        public bool Alive { get; set; } = true;

        public byte[]? ReadBlock(long address, int size) => Inner.ReadBlock(address, size);

        public string? WindowTitle => "Ephinea: Phantasy Star Online Blue Burst";
        public int Pid => 4242;
        public int? LastReadError => null;
        public bool IsAlive => Alive;
        public string? ImagePath => @"C:\Games\Ephinea\PsoBB.exe";

        public void Dispose()
        {
        }
    }

    private sealed class FakeConnector(IProcessMemoryReader? process) : IGameConnector
    {
        public int Attempts { get; private set; }

        public IProcessMemoryReader? TryAttach()
        {
            Attempts++;
            return process;
        }

        public PsobbRejection? Rejection => null;
    }

    private sealed record Rig(
        PollLoop Loop,
        ConfigStore Config,
        RunQueue Queue,
        FakeHandler Server,
        ManualGameClock Clock,
        FakeProcess? Process,
        MockBackend Backend,
        List<RunToast> Toasts,
        List<PollTick> Ticks,
        List<string?> GameExe);

    /// <summary>A submitter that blocks until released (an unreachable server).</summary>
    private sealed class BlockingSubmitter : IRunSubmitter
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public async Task<SubmitResult> SubmitAsync(RappyRuns.Core.Sexp.Plist entry, string token, CancellationToken cancellationToken)
        {
            Entered.Set();
            await Task.Run(() => Release.Wait(cancellationToken), cancellationToken);
            return SubmitResult.ApiError("offline");
        }
    }

    /// <summary>A guest registration that blocks until released (the pass is past its start, before it reads the queue).</summary>
    private sealed class BlockingRegistrar(IAnonymousRegistrar inner) : IAnonymousRegistrar
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public async Task<string> RegisterAnonymousAsync(string label, CancellationToken cancellationToken)
        {
            Entered.Set();
            await Task.Run(() => Release.Wait(cancellationToken), cancellationToken);
            return await inner.RegisterAnonymousAsync(label, cancellationToken);
        }
    }

    private static readonly TimeSpan Drain = TimeSpan.FromSeconds(10);

    private Rig Build(bool attached = true, bool record = true, bool manageRecordings = true, IRunSubmitter? submitter = null,
        Func<IAnonymousRegistrar, IAnonymousRegistrar>? registrar = null)
    {
        var config = ConfigStore.Open(_dir);
        config.ServerUrl = "https://s.example";
        var server = new FakeHandler(r => (r.Method, new Uri(r.Url).AbsolutePath) switch
        {
            ("POST", "/api/register-anonymous") => (201, """{"token":"anon-1","username":"guest-1"}"""),
            ("POST", "/api/runs") => (201, """{"id":42,"url":"https://s.example/runs/42","standing":{"rank":1,"parties":1}}"""),
            _ => (404, "{}"),
        });
        var api = new ApiClient(new HttpTransport(server), new ConfigAuthSettings(config));
        var network = new QueueNetwork(api, RunPayload.RunJson, () => "diagnostics");
        var clock = new ManualGameClock();
        var catalog = BuiltinCatalog();
        var frames = new GameFrameProcessor(new Detector(catalog, clock), new RunLogs(clock), clock);
        var queue = new RunQueue(config.QueuePath);
        var backend = new MockBackend();
        var recorder = new Recorder(backend, new RecorderEnvironment
        {
            Settings = () => new RecordingSettings { RecordEnabled = record, HwEncode = false },
            RecordDir = () => _dir + Path.DirectorySeparatorChar,
            FfmpegPath = () => "ffmpeg.exe",
            Timestamp = () => clock.Now,
            TicksPerSecond = clock.TicksPerSecond,
            Sleep = t => clock.AdvanceMs((long)t.TotalMilliseconds),
        });
        recorder.OnKeep = (path, run, untrimmed) => queue.LinkVideoFile(run, path, untrimmed);
        var process = attached ? new FakeProcess(LobbyReader()) : null;
        var toasts = new List<RunToast>();
        var ticks = new List<PollTick>();
        var gameExe = new List<string?>();
        var loop = new PollLoop(new PollLoopServices
        {
            Config = config,
            Clock = clock,
            Connector = new FakeConnector(process),
            Frames = frames,
            Catalog = catalog,
            Recorder = recorder,
            Queue = queue,
            Registrar = registrar?.Invoke(network) ?? network,
            Submitter = submitter ?? network,
            Uploader = network,
            Ghost = new GhostSession(),
            ManageRecordingsFolder = manageRecordings,
            SetGameExe = gameExe.Add,
            Toast = toasts.Add,
            Tick = ticks.Add,
            MachineName = "TESTPC",
            Wait = _ => false,
        });
        return new Rig(loop, config, queue, server, clock, process, backend, toasts, ticks, gameExe);
    }

    private static void Frame(Rig rig, IMemoryReader memory, long ms = 33)
    {
        rig.Process!.Inner = memory;
        rig.Clock.AdvanceMs(ms);
        rig.Loop.Iterate();
    }

    [Fact(DisplayName = "a scripted TTF run is recorded, queued, submitted as a guest and celebrated")]
    public void TtfRunEndToEnd()
    {
        var rig = Build();

        rig.Loop.Iterate(); // search: attaches (and disarms)
        Assert.Equal(@"C:\Games\Ephinea\PsoBB.exe", rig.GameExe.Last());
        Frame(rig, LobbyReader());
        Frame(rig, TtfReader(start: 1));
        Assert.True(rig.Loop.Busy || rig.Queue.Entries.Count == 0);
        Assert.Equal(1, rig.Backend.Count("start")); // the capture began on the quest edge
        Frame(rig, TtfReader(start: 1), ms: 60_000);
        Frame(rig, TtfReader(start: 1, end: 1));
        Assert.True(rig.Loop.WaitForSubmissions(Drain)); // the submit worker

        var entry = Assert.Single(rig.Queue.Entries);
        Assert.Equal(RunStatus.Submitted, entry.Status);
        Assert.Equal(42, entry.ServerId);
        Assert.Equal("https://s.example/runs/42", entry.Url);
        Assert.Equal("ep1-towards-the-future", entry.QuestSlug);
        Assert.InRange(entry.TimeMs ?? 0, 59_000, 61_000);
        // Stamped by the recorder on the shared plist BEFORE the enqueue (core §22 #45).
        Assert.NotNull(entry.Get(RunKeys.VideoOffsetMs).AsLong);

        // No account: a guest registered first and the run went out with its token.
        Assert.Equal("anon-1", rig.Config.AnonToken);
        var posts = rig.Server.Requests.Where(r => r.Url.EndsWith("/api/runs", StringComparison.Ordinal)).ToList();
        var post = Assert.Single(posts);
        Assert.Equal("Bearer anon-1", post.Authorization);
        var body = JsonNode.Parse(post.Body!)!;
        Assert.Equal("ep1-towards-the-future", body["quest"]?.GetValue<string>());
        Assert.Equal(entry.TimeMs, body["time_ms"]?.GetValue<long>());

        // First run on the board: the toast names the quest and links the run page.
        var toast = Assert.Single(rig.Toasts);
        Assert.Equal(Strings.Default.Tr(Language.En, "toast-first-title"), toast.Title);
        Assert.Equal("https://s.example/runs/42", toast.Url);

        // The 4 Hz status tick ran while attached.
        Assert.Contains(rig.Ticks, t => t.Attached);
    }

    [Fact(DisplayName = "the game exiting mid-run queues the aborted run and clears the Pin Share exe (decision 2b)")]
    public void DetachQueuesAbortedRun()
    {
        var rig = Build(record: false);
        rig.Loop.Iterate();
        Frame(rig, LobbyReader());
        Frame(rig, TtfReader(start: 1));
        Frame(rig, TtfReader(start: 1), ms: 20_000);
        rig.Process!.Alive = false;
        rig.Loop.Iterate(); // detach

        var entry = Assert.Single(rig.Queue.Entries);
        Assert.True(entry.Is(RunKeys.Aborted));
        Assert.Null(rig.GameExe.Last());
    }

    [Fact(DisplayName = "Retry submits the queue while no game is running")]
    public void RetryWithoutGame()
    {
        var rig = Build(attached: false);
        var queued = rig.Queue.Enqueue(new RappyRuns.Core.Sexp.Plist()
            .With("QUEST-SLUG", "ep1-towards-the-future")
            .With("TIME-MS", 61_000L)
            .With("FINISHED-AT", 3_967_948_800L));
        Assert.Equal(RunStatus.Queued, queued.Status);

        rig.Loop.Iterate(); // searching, nothing requested
        Assert.DoesNotContain(rig.Server.Requests, r => r.Url.EndsWith("/api/runs", StringComparison.Ordinal));

        rig.Loop.RequestRetry();
        rig.Loop.Iterate();
        Assert.True(rig.Loop.WaitForSubmissions(Drain));
        Assert.Equal(RunStatus.Submitted, Assert.Single(rig.Queue.Entries).Status);
        Assert.Contains(rig.Ticks, t => !t.Attached);
    }

    [Fact(DisplayName = "Retry on an empty queue registers no guest account (deviation from Lisp)")]
    public void RetryEmptyQueueIsQuiet()
    {
        var rig = Build(attached: false);
        rig.Loop.RequestRetry();
        rig.Loop.Iterate();
        Assert.True(rig.Loop.WaitForSubmissions(Drain));
        Assert.Empty(rig.Server.Requests);
        Assert.Equal("", rig.Config.AnonToken);
    }

    [Fact(DisplayName = "an unreachable server never blocks the poll thread: the run is timed, submitted on the worker")]
    public void SubmissionDoesNotBlockFrames()
    {
        var submitter = new BlockingSubmitter();
        var rig = Build(record: false, submitter: submitter);
        rig.Config.AnonToken = "anon-1"; // no registration round trip
        rig.Loop.Iterate();
        Frame(rig, LobbyReader());
        Frame(rig, TtfReader(start: 1));
        Frame(rig, TtfReader(start: 1), ms: 30_000);
        Frame(rig, TtfReader(start: 1, end: 1)); // completes: enqueued here, submit requested
        var entry = Assert.Single(rig.Queue.Entries);
        Assert.Equal(RunStatus.Queued, entry.Status);
        Assert.True(submitter.Entered.Wait(Drain), "the worker picked the run up");

        // The worker hangs on the server; frames and a retry request keep flowing.
        Frame(rig, LobbyReader());
        rig.Loop.RequestRetry();
        Frame(rig, LobbyReader());
        Assert.False(rig.Loop.WaitForSubmissions(TimeSpan.FromMilliseconds(50)));

        submitter.Release.Set();
        Assert.True(rig.Loop.WaitForSubmissions(Drain));
        Assert.Equal(RunStatus.Failed, Assert.Single(rig.Queue.Entries).Status);
        Assert.Empty(rig.Toasts);
    }

    [Fact(DisplayName = "a run completed while a Retry pass starts is celebrated by that pass; the backlog is not (PR #330 review)")]
    public void ToastFollowsTheEntryNotThePass()
    {
        BlockingRegistrar? blocking = null;
        var rig = Build(attached: false, registrar: inner => blocking = new BlockingRegistrar(inner));
        rig.Queue.Enqueue(new RappyRuns.Core.Sexp.Plist()
            .With("QUEST-SLUG", "ep1-towards-the-future")
            .With("TIME-MS", 62_000L)); // backlog from an earlier session
        rig.Loop.RequestRetry(); // a pass without celebration...
        Assert.True(blocking!.Entered.Wait(Drain));

        // ...and before it reads the queue, a run completes (its own request coalesces).
        rig.Loop.HandleCompletedRuns([new RappyRuns.Core.Sexp.Plist()
            .With("QUEST-SLUG", "ep1-towards-the-future")
            .With("TIME-MS", 61_000L)]);
        blocking.Release.Set();
        Assert.True(rig.Loop.WaitForSubmissions(Drain));

        Assert.All(rig.Queue.Entries, e => Assert.Equal(RunStatus.Submitted, e.Status));
        Assert.Equal(2, rig.Server.Requests.Count(r => r.Url.EndsWith("/api/runs", StringComparison.Ordinal)));
        Assert.Single(rig.Toasts); // the fresh run only
    }

    [Fact(DisplayName = "stop cancels a hanging submission pass instead of waiting for it")]
    public void StopAbandonsHangingSubmission()
    {
        var submitter = new BlockingSubmitter();
        var rig = Build(attached: false, submitter: submitter);
        rig.Config.AnonToken = "anon-1";
        rig.Queue.Enqueue(new RappyRuns.Core.Sexp.Plist()
            .With("QUEST-SLUG", "ep1-towards-the-future")
            .With("TIME-MS", 61_000L));
        rig.Loop.RequestRetry();
        Assert.True(submitter.Entered.Wait(Drain));
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(rig.Loop.Stop(TimeSpan.FromSeconds(1)));
        Assert.True(started.Elapsed < PollLoop.SubmitStopWait + TimeSpan.FromSeconds(2));
        Assert.True(rig.Loop.WaitForSubmissions(Drain));
        Assert.Equal(RunStatus.Queued, Assert.Single(rig.Queue.Entries).Status);
    }

    [Theory(DisplayName = "a developer copy never cleans or sweeps the shared recordings folder")]
    [InlineData(true)]
    [InlineData(false)]
    public void DevCopyLeavesRecordingsAlone(bool manage)
    {
        var rig = Build(attached: false, manageRecordings: manage);
        rig.Config.Set(ConfigKeys.RecordMaxTotalGb, RappyRuns.Core.Sexp.SexpNode.Int(1));
        rig.Backend.Stale = [Path.Combine(_dir, "stale.mkv")];
        rig.Backend.Recordings = [new RecordingFile(Path.Combine(_dir, "big.mp4"), 2L * 1024 * 1024 * 1024, 100)];
        rig.Loop.Startup();
        rig.Loop.Iterate();
        Assert.Equal(manage ? 2 : 0, rig.Backend.Count("delete"));
    }

    [Fact(DisplayName = "shutdown stops the recorder and clears the game exe")]
    public void ShutdownUnwinds()
    {
        var rig = Build();
        rig.Loop.Iterate();
        Frame(rig, LobbyReader());
        Frame(rig, TtfReader(start: 1));
        Assert.Equal(1, rig.Backend.Count("start"));
        rig.Loop.Shutdown();
        Assert.True(rig.Backend.Count("stop") + rig.Backend.Count("kill") >= 1);
        Assert.Null(rig.GameExe.Last());
    }
}

internal static class PlistTestExtensions
{
    public static RappyRuns.Core.Sexp.Plist With(this RappyRuns.Core.Sexp.Plist plist, string key, object value)
    {
        plist.Set(key, value switch
        {
            string s => RappyRuns.Core.Sexp.SexpNode.Str(s),
            long l => RappyRuns.Core.Sexp.SexpNode.Int(l),
            _ => throw new ArgumentException("unsupported"),
        });
        return plist;
    }
}
