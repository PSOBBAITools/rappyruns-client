using RappyRuns.Core.Game;
using RappyRuns.Core.Sexp;
using static RappyRuns.Tests.Game.GameTestKit;

namespace RappyRuns.Tests.Game;

/// <summary>
/// The poll-frame orchestration (main.lisp:237-377, spec core §17.3) and the
/// PLAN.md decision-2 deviations (read grace, runs at game exit). No Lisp suite
/// covers the poll loop; these pin the contract the lead wires to.
/// </summary>
public class GameFrameTests
{
    /// <summary>A mock process whose memory can be made unreadable.</summary>
    private sealed class FakeProcess(IMemoryReader inner) : IProcessMemoryReader
    {
        public IMemoryReader Inner { get; set; } = inner;
        public bool Unreadable { get; set; }
        public int Reads { get; private set; }

        public byte[]? ReadBlock(long address, int size)
        {
            Reads++;
            return Unreadable ? null : Inner.ReadBlock(address, size);
        }

        public string? WindowTitle => "Ephinea: Phantasy Star Online Blue Burst";
        public int Pid => 4242;
        public int? LastReadError => Unreadable ? 299 : null;
        public bool IsAlive => true;
        public string? ImagePath => null;

        public void Dispose()
        {
        }
    }

    private sealed class Hooks : IFrameHooks
    {
        public List<string> Calls { get; } = [];
        public List<Plist> Completed { get; } = [];
        public bool ThrowInGhost { get; set; }

        public void RecorderStep(DetectorState state, IReadOnlyList<Plist> runs, string? windowTitle) =>
            Calls.Add($"recorder:{runs.Count}");

        public void GhostFetch(Snapshot? snapshot)
        {
            Calls.Add("ghost-fetch");
            if (ThrowInGhost) throw new InvalidOperationException("boom");
        }

        public void PinSetFetch(Snapshot? snapshot) => Calls.Add("pin-set-fetch");

        public void GhostRaceStep(Detector detector, Snapshot? snapshot) => Calls.Add("ghost-race");

        public void CompletedRuns(IReadOnlyList<Plist> runs)
        {
            Calls.Add($"completed:{runs.Count}");
            Completed.AddRange(runs);
        }
    }

    private static (GameFrameProcessor Frames, ManualGameClock Clock, FakeProcess Process, Hooks Hooks, List<string> Log) Setup(
        long graceMs = 1000, bool triggerLog = false, TriggerLog? log = null)
    {
        var clock = new ManualGameClock();
        var detector = new Detector(BuiltinCatalog(), clock);
        var lines = new List<string>();
        var frames = new GameFrameProcessor(detector, new RunLogs(clock), clock,
            new GameFrameOptions { ReadGraceMs = graceMs, TriggerLogEnabled = () => triggerLog }, log, lines.Add);
        return (frames, clock, new FakeProcess(LobbyReader()), new Hooks(), lines);
    }

    private static FrameResult Frame(GameFrameProcessor frames, ManualGameClock clock, FakeProcess process, Hooks hooks, IMemoryReader? memory = null,
        long ms = 33)
    {
        if (memory is not null) process.Inner = memory;
        clock.AdvanceMs(ms);
        return frames.Step(process, hooks);
    }

    [Fact(DisplayName = "hooks run in poll-frame-step order; completed runs only when a run finished")]
    public void HookOrder()
    {
        var (frames, clock, p, hooks, _) = Setup();
        frames.Attach();
        Frame(frames, clock, p, hooks, LobbyReader());
        Frame(frames, clock, p, hooks, TtfReader(start: 1));
        hooks.Calls.Clear();
        var result = Frame(frames, clock, p, hooks, TtfReader(start: 1, end: 1));
        Assert.Single(result.Runs);
        Assert.Equal(["recorder:1", "ghost-fetch", "pin-set-fetch", "ghost-race", "completed:1"], hooks.Calls);
        hooks.Calls.Clear();
        Frame(frames, clock, p, hooks);
        Assert.Equal(["recorder:0", "ghost-fetch", "pin-set-fetch", "ghost-race"], hooks.Calls);
    }

    [Fact(DisplayName = "a failing hook never reaches detection")]
    public void HookIsolated()
    {
        var (frames, clock, p, hooks, log) = Setup();
        hooks.ThrowInGhost = true;
        frames.Attach();
        Frame(frames, clock, p, hooks, LobbyReader());
        Frame(frames, clock, p, hooks, TtfReader(start: 1));
        Assert.Single(Frame(frames, clock, p, hooks, TtfReader(start: 1, end: 1)).Runs);
        Assert.Contains(log, l => l.Contains("boom", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "attach disarms: a quest attached mid-run is not timed")]
    public void AttachDisarms()
    {
        var (frames, clock, p, hooks, _) = Setup();
        frames.Attach();
        Frame(frames, clock, p, hooks, TtfReader(start: 1));
        Assert.Empty(Frame(frames, clock, p, hooks, TtfReader(start: 1, end: 1)).Runs);
        Assert.Equal(DetectorState.Idle, frames.Detector.State);
    }

    [Fact(DisplayName = "decision 2a: unreadable frames inside the grace keep the run going")]
    public void GraceKeepsRun()
    {
        var (frames, clock, p, hooks, _) = Setup();
        frames.Attach();
        Frame(frames, clock, p, hooks, LobbyReader());
        Frame(frames, clock, p, hooks, TtfReader(start: 1));
        clock.AdvanceMs(20_000);
        p.Unreadable = true;
        for (var i = 0; i < 25; i++)
        {
            var r = Frame(frames, clock, p, hooks);
            Assert.True(r.InReadGrace);
            Assert.Empty(r.Runs);
        }
        Assert.Equal(DetectorState.InQuest, frames.Detector.State);
        p.Unreadable = false;
        var done = Frame(frames, clock, p, hooks, TtfReader(start: 1, end: 1));
        var run = Assert.Single(done.Runs);
        Assert.False(run.Truthy("aborted"));
        Assert.True(run.Num("time-ms") > 20_000);
    }

    [Fact(DisplayName = "decision 2a: past the grace the run is abandoned and the detector disarmed")]
    public void GraceExpires()
    {
        var (frames, clock, p, hooks, _) = Setup();
        frames.Attach();
        Frame(frames, clock, p, hooks, LobbyReader());
        Frame(frames, clock, p, hooks, TtfReader(start: 1));
        clock.AdvanceMs(20_000);
        p.Unreadable = true;
        var aborted = new List<Plist>();
        for (var i = 0; i < 40; i++) aborted.AddRange(Frame(frames, clock, p, hooks).Runs);
        var run = Assert.Single(aborted);
        Assert.True(run.Truthy("aborted"));
        Assert.Contains(hooks.Completed, r => ReferenceEquals(r, run));
        Assert.False(frames.Detector.Armed);
    }

    [Fact(DisplayName = "grace 0 restores the Lisp behaviour: the first failed snapshot abandons")]
    public void NoGrace()
    {
        var (frames, clock, p, hooks, _) = Setup(graceMs: 0);
        frames.Attach();
        Frame(frames, clock, p, hooks, LobbyReader());
        Frame(frames, clock, p, hooks, TtfReader(start: 1));
        clock.AdvanceMs(20_000);
        p.Unreadable = true;
        var r = Frame(frames, clock, p, hooks);
        Assert.False(r.InReadGrace);
        Assert.True(Assert.Single(r.Runs).Truthy("aborted"));
    }

    [Fact(DisplayName = "decision 2b: game exit emits runs over 15 s as aborted and hands them on")]
    public void DetachEmits()
    {
        var (frames, clock, p, hooks, _) = Setup();
        frames.Attach();
        Frame(frames, clock, p, hooks, LobbyReader());
        Frame(frames, clock, p, hooks, TtfReader(start: 1));
        clock.AdvanceMs(16_000);
        hooks.Calls.Clear();
        var runs = frames.Detach(hooks);
        Assert.True(Assert.Single(runs).Truthy("aborted"));
        Assert.Equal(["recorder:0", "completed:1"], hooks.Calls);
        Assert.Null(frames.PreviousSnapshot);
    }

    [Fact(DisplayName = "game exit under 15 s emits nothing")]
    public void DetachShort()
    {
        var (frames, clock, p, hooks, _) = Setup();
        frames.Attach();
        Frame(frames, clock, p, hooks, LobbyReader());
        Frame(frames, clock, p, hooks, TtfReader(start: 1));
        Assert.Empty(frames.Detach(hooks));
    }

    [Fact(DisplayName = "60 unreadable frames flag 'reads failing' and log the cause once")]
    public void ReadFailing()
    {
        var (frames, clock, p, hooks, log) = Setup();
        frames.Attach();
        p.Unreadable = true;
        for (var i = 0; i < 59; i++) Frame(frames, clock, p, hooks);
        Assert.False(frames.ReadFailing);
        Frame(frames, clock, p, hooks);
        Assert.True(frames.ReadFailing);
        for (var i = 0; i < 10; i++) Frame(frames, clock, p, hooks);
        Assert.Equal(["attached but memory reads failing: pid 4242 err 299 (ERROR_PARTIAL_COPY)"], log);
        p.Unreadable = false;
        Frame(frames, clock, p, hooks, LobbyReader());
        Assert.False(frames.ReadFailing);
    }

    [Fact(DisplayName = "augment: monsters every quest frame, inventory once a second, nothing in the lobby")]
    public void Augment()
    {
        var (frames, clock, p, hooks, _) = Setup();
        frames.Attach();
        var lobby = Frame(frames, clock, p, hooks, LobbyReader());
        Assert.Null(lobby.Snapshot!.Monsters);
        Assert.Null(lobby.Snapshot.Inventory);
        var quest = TtfReader(start: 1);
        // Add an entity array (Booma + Zu) and a one-item inventory to the quest image.
        quest.Regions.AddRange(MonsterReadTests.MonsterReader().Regions);
        var globals = MockMemory.Zeros(8);
        var pointers = MockMemory.Zeros(4);
        MockMemory.PutU32(globals, 0, 0x00610000);
        MockMemory.PutU16(globals, 4, 1);
        MockMemory.PutU32(pointers, 0, 0x00600400);
        quest.Regions.Add((0x00A8D81C, globals));
        quest.Regions.Add((0x00610000, pointers));
        quest.Regions.Add(InventoryTests.ItemBlock(0x00600400, owner: 0, type: 3, group: 0, index: 0, toolCount: 4));
        var first = Frame(frames, clock, p, hooks, quest);
        var second = Frame(frames, clock, p, hooks, quest);
        var later = Frame(frames, clock, p, hooks, quest, ms: 1000);
        Assert.Equal(2, first.Snapshot!.Monsters!.Count);
        Assert.Equal(2, second.Snapshot!.Monsters!.Count);
        Assert.Equal([("monomate", 4)], first.Snapshot.Inventory!.Consumables);
        Assert.Null(second.Snapshot.Inventory);
        Assert.NotNull(later.Snapshot!.Inventory);
        Assert.Null(later.Snapshot.Camera);
    }

    [Fact(DisplayName = "trigger log is written only while enabled")]
    public void TriggerLogToggle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eta-frame-trigger-log-{Guid.NewGuid():N}.txt");
        try
        {
            var clock = new ManualGameClock();
            using var log = new TriggerLog(path, clock);
            foreach (var enabled in new[] { false, true })
            {
                var detector = new Detector(BuiltinCatalog(), clock);
                var frames = new GameFrameProcessor(detector, new RunLogs(clock), clock,
                    new GameFrameOptions { TriggerLogEnabled = () => enabled }, log);
                var p = new FakeProcess(TtfReader());
                var hooks = new Hooks();
                frames.Attach();
                Frame(frames, clock, p, hooks, TtfReader());
                Frame(frames, clock, p, hooks, TtfReader(start: 1));
            }
            log.Close();
            var lines = File.ReadAllLines(path);
            Assert.Equal(["12:00:00 \"Towards the Future\" register 12: 0 -> 1"], lines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "last kill and run logs follow the frames")]
    public void RunLogsFollow()
    {
        var (frames, clock, p, hooks, _) = Setup();
        frames.Attach();
        Frame(frames, clock, p, hooks, TtfReader());
        Assert.Equal(new RunQuest(118, "Towards the Future", 1), frames.RunLogs.RunQuest);
    }
}

/// <summary>main.lisp:104 log-account-mode.</summary>
public class AccountModeLoggerTests
{
    private static Plist Run(string? mode, long? color)
    {
        var p = new Plist();
        if (color is { } c) p.Set("my-name-color", SexpNode.Int(c));
        if (mode is not null) p.Set("account-mode", SexpNode.Str(mode));
        return p;
    }

    [Fact(DisplayName = "account-mode line on change, then again only after 300 s")]
    public void Cadence()
    {
        var logger = new AccountModeLogger();
        Assert.Equal("account-mode: sandbox (name color FFAB9423)", logger.LineFor(Run("sandbox", 0xFFAB9423), 1000));
        Assert.Null(logger.LineFor(Run("sandbox", 0xFFAB9423), 1299));
        Assert.NotNull(logger.LineFor(Run("sandbox", 0xFFAB9423), 1300));
        Assert.Equal("account-mode: no verdict (name color ?)", logger.LineFor(Run(null, null), 1301));
        Assert.Equal("account-mode: no verdict (name color 00FF0000)", logger.LineFor(Run(null, 0xFF0000), 1302));
    }
}
