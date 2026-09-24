using System.Text.Json;
using RappyRuns.Core.Game;
using RappyRuns.Core.Sexp;
using static RappyRuns.Tests.Game.GameTestKit;
using static RappyRuns.Tests.Game.MockMemory;

namespace RappyRuns.Tests.Game;

/// <summary>tests-detect.lisp run-payload-tests.</summary>
public class PayloadTests
{
    private static Plist P(string sexp) => Plist.From(SexpReader.ReadOne(sexp))!;

    private static JsonElement Parsed() => JsonDocument.Parse(RunPayload.RunJson(P("""
        (:quest-slug "ep1-towards-the-future" :quest-name "Towards the Future"
         :episode 1 :time-ms 754321 :party-size 1 :pb t
         :difficulty "Ultimate" :death-count 2 :submitter-section-id "Skyly"
         :players ((:name "Ryu" :class "HUcast" :level 142 :section-id "Skyly" :guild-card "42001234"))
         :telemetry (:frames ((0 945 300 0 0 1 2 10.0 -3.1 0 0 0 1 12 0))
                     :events ((:t 12 :type "death"))
                     :death-count 2 :meseta-charged 400 :kills 55 :tp-used 120
                     :traps-used (:dt 0 :ft 2 :ct 0)
                     :items-used ((:monomate . 1))
                     :techs-cast (("Resta" . 3))
                     :time-by-state ((1 . 60000))
                     :weapons ((:id "00010000" :display "Charge Vulcan +9" :type :weapon :seconds 700 :attacks 512 :techs 0))))
        """))).RootElement;

    private static JsonElement Telemetry() => Parsed().GetProperty("telemetry");

    [Fact(DisplayName = "payload difficulty")]
    public void Difficulty() => Assert.Equal("Ultimate", Parsed().GetProperty("difficulty").GetString());

    [Fact(DisplayName = "payload death count")]
    public void DeathCount() => Assert.Equal(2, Parsed().GetProperty("death_count").GetInt32());

    [Fact(DisplayName = "payload episode")]
    public void Episode() => Assert.Equal(1, Parsed().GetProperty("episode").GetInt32());

    [Fact(DisplayName = "payload submitter section")]
    public void Section() => Assert.Equal("Skyly", Parsed().GetProperty("submitter_section_id").GetString());

    [Fact(DisplayName = "payload player level")]
    public void PlayerLevel() => Assert.Equal(142, Parsed().GetProperty("players")[0].GetProperty("level").GetInt32());

    [Fact(DisplayName = "payload player section")]
    public void PlayerSection() => Assert.Equal("Skyly", Parsed().GetProperty("players")[0].GetProperty("section_id").GetString());

    [Fact(DisplayName = "payload player guild card")]
    public void PlayerGuildCard() => Assert.Equal("42001234", Parsed().GetProperty("players")[0].GetProperty("guild_card").GetString());

    [Fact(DisplayName = "payload telemetry present")]
    public void TelemetryPresent() => Assert.Equal(JsonValueKind.Object, Telemetry().ValueKind);

    [Fact(DisplayName = "payload frame keys")]
    public void FrameKeys() =>
        Assert.Equal(Core.Game.Telemetry.FrameKeys, Telemetry().GetProperty("frame_keys").EnumerateArray().Select(k => k.GetString()!));

    [Fact(DisplayName = "payload one frame")]
    public void OneFrame() => Assert.Equal(1, Telemetry().GetProperty("frames").GetArrayLength());

    [Fact(DisplayName = "payload items snake_cased")]
    public void ItemsSnakeCased() => Assert.Equal(1, Telemetry().GetProperty("items_used").GetProperty("monomate").GetInt32());

    [Fact(DisplayName = "payload traps skip zeroes")]
    public void TrapsSkipZeroes()
    {
        var traps = Telemetry().GetProperty("traps_used");
        Assert.Equal(2, traps.GetProperty("ft").GetInt32());
        Assert.False(traps.TryGetProperty("dt", out _));
    }

    [Fact(DisplayName = "payload weapon display")]
    public void WeaponDisplay() => Assert.Equal("Charge Vulcan +9", Telemetry().GetProperty("weapons")[0].GetProperty("display").GetString());

    [Fact(DisplayName = "payload event")]
    public void Event() => Assert.Equal("death", Telemetry().GetProperty("events")[0].GetProperty("type").GetString());

    [Fact(DisplayName = "payload omits unranked and private for a board run")]
    public void OmitsUnrankedPrivate()
    {
        Assert.False(Parsed().TryGetProperty("unranked", out _));
        Assert.False(Parsed().TryGetProperty("private", out _));
    }

    [Fact(DisplayName = "payload omits account_mode without a verdict")]
    public void OmitsAccountMode() => Assert.False(Parsed().TryGetProperty("account_mode", out _));

    private static JsonElement Sandbox() => JsonDocument.Parse(RunPayload.RunJson(P(
        "(:quest-slug \"ep1-test\" :time-ms 60000 :party-size 1 :players () :account-mode \"sandbox\" :my-name-color 4289434659)"))).RootElement;

    [Fact(DisplayName = "payload account_mode rides when detected")]
    public void AccountModeRides() => Assert.Equal("sandbox", Sandbox().GetProperty("account_mode").GetString());

    [Fact(DisplayName = "the run payload is exactly the known fields - no name colour")]
    public void ExactKeySet() => Assert.Equal(
        new[] { "account_mode", "notes", "party_size", "players", "quest", "time_ms" },
        Sandbox().EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

    private static JsonElement Tracking() => JsonDocument.Parse(RunPayload.RunJson(P(
        "(:quest-slug \"ep1-test\" :time-ms 60000 :party-size 1 :players () :unranked t :run-private t)"))).RootElement;

    [Fact(DisplayName = "payload unranked rides when stamped")]
    public void UnrankedRides() => Assert.True(Tracking().GetProperty("unranked").GetBoolean());

    [Fact(DisplayName = "payload private rides with the tracking sub-setting")]
    public void PrivateRides() => Assert.True(Tracking().GetProperty("private").GetBoolean());

    [Fact(DisplayName = "payload notes mention record only")]
    public void NotesRecordOnly() => Assert.Contains("record only", Tracking().GetProperty("notes").GetString());
}

/// <summary>tests-detect.lisp run-detect-telemetry-tests.</summary>
public class DetectTelemetryTests
{
    private static Plist Run()
    {
        var (d, clock) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader());
        d.StepWith(TtfReader(start: 1));
        clock.AdvanceMs(50);
        d.StepWith(TtfReader(start: 1));
        clock.AdvanceMs(50);
        return d.StepWith(TtfReader(start: 1, end: 1))[0];
    }

    [Fact(DisplayName = "run has difficulty")]
    public void Difficulty() => Assert.Equal("Normal", Run().Str("difficulty"));

    [Fact(DisplayName = "run has death count")]
    public void DeathCount() => Assert.Equal(0, Run().Num("death-count"));

    [Fact(DisplayName = "run has telemetry")]
    public void HasTelemetry() => Assert.IsType<SList>(Run().Get("telemetry"));

    [Fact(DisplayName = "telemetry recorded a frame")]
    public void TelemetryFrame() => Assert.NotEmpty(Plist.From(Run().Get("telemetry"))!.Get("frames")!.Elements);

    [Fact(DisplayName = "player carries level")]
    public void PlayerLevel() => Assert.Equal(1, Run().Players()[0].Num("level"));
}

/// <summary>tests-detect.lisp run-detect-tests.</summary>
public class DetectTests
{
    private sealed record TtfFlow(
        DetectorState AfterLobby, DetectorState BeforeStart, DetectorState AfterStart, List<Plist> BeforeEnd,
        Plist? Run, DetectorState AfterRun, DetectorState StaysLoaded, DetectorState Rearmed);

    private static TtfFlow FullFlow()
    {
        var (d, clock) = NewDetector();
        d.StepWith(LobbyReader());
        var afterLobby = d.State;
        d.StepWith(TtfReader());
        var beforeStart = d.State;
        d.StepWith(TtfReader(start: 1));
        var afterStart = d.State;
        clock.AdvanceMs(50);
        var beforeEnd = d.StepWith(TtfReader(start: 1));
        clock.AdvanceMs(50);
        var run = d.StepWith(TtfReader(start: 1, end: 1)).FirstOrDefault();
        var afterRun = d.State;
        d.StepWith(TtfReader(start: 1, end: 1));
        var staysLoaded = d.State;
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        return new TtfFlow(afterLobby, beforeStart, afterStart, beforeEnd, run, afterRun, staysLoaded, d.State);
    }

    [Fact(DisplayName = "idle after lobby")]
    public void IdleAfterLobby() => Assert.Equal(DetectorState.Idle, FullFlow().AfterLobby);

    [Fact(DisplayName = "still idle before start register")]
    public void IdleBeforeStart() => Assert.Equal(DetectorState.Idle, FullFlow().BeforeStart);

    [Fact(DisplayName = "in-quest after start register")]
    public void InQuestAfterStart() => Assert.Equal(DetectorState.InQuest, FullFlow().AfterStart);

    [Fact(DisplayName = "no run before end register")]
    public void NoRunBeforeEnd() => Assert.Empty(FullFlow().BeforeEnd);

    [Fact(DisplayName = "run emitted on end register")]
    public void RunEmitted() => Assert.NotNull(FullFlow().Run);

    [Fact(DisplayName = "run slug")]
    public void RunSlug() => Assert.Equal("ep1-towards-the-future", FullFlow().Run!.Str("quest-slug"));

    [Fact(DisplayName = "run time >= 100ms")]
    public void RunTime() => Assert.True(FullFlow().Run!.Num("time-ms") >= 100);

    [Fact(DisplayName = "run party")]
    public void RunParty() => Assert.Equal(2, FullFlow().Run!.Num("party-size"));

    [Fact(DisplayName = "run players")]
    public void RunPlayers() => Assert.Equal(
        [("Ryu", "HUcast"), ("Elly", "FOnewearl")],
        FullFlow().Run!.Players().Select(p => (p.Str("name"), p.Str("class"))));

    [Fact(DisplayName = "run not PB")]
    public void RunNotPb() => Assert.False(FullFlow().Run!.Truthy("pb"));

    [Fact(DisplayName = "detector reset after run")]
    public void ResetAfterRun() => Assert.Equal(DetectorState.Idle, FullFlow().AfterRun);

    [Fact(DisplayName = "no restart while completed quest stays loaded")]
    public void NoRestart() => Assert.Equal(DetectorState.Idle, FullFlow().StaysLoaded);

    [Fact(DisplayName = "re-armed after lobby visit")]
    public void Rearmed() => Assert.Equal(DetectorState.InQuest, FullFlow().Rearmed);

    [Fact(DisplayName = "mid-quest attach stays idle")]
    public void MidQuestAttach()
    {
        var (d, _) = NewDetector();
        d.StepWith(TtfReader(start: 1));
        Assert.Equal(DetectorState.Idle, d.State);
    }

    // ------------------------------------------------------------ account mode

    private const long SandboxColor = PsobbTables.SandboxNameColor;
    private const long White = PsobbTables.NormalNameColor;

    private static string? ModeOfRun(long color, long partner = White)
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1, nameColor: color, partnerColor: partner));
        return d.StepWith(TtfReader(start: 1, end: 1, nameColor: color, partnerColor: partner))[0].Str("account-mode");
    }

    [Fact(DisplayName = "the sandbox name colour stamps the run sandbox")]
    public void SandboxStamps() => Assert.Equal("sandbox", ModeOfRun(SandboxColor));

    [Fact(DisplayName = "a white name stamps the run normal")]
    public void WhiteStamps() => Assert.Equal("normal", ModeOfRun(White));

    [Fact(DisplayName = "an unrecognised colour is no verdict - not normal, not sandbox")]
    public void UnrecognisedNoVerdict() => Assert.Null(ModeOfRun(0xFFFF0000));

    [Fact(DisplayName = "one bit off the sandbox hue is no verdict")]
    public void OneBitOff() => Assert.Null(ModeOfRun(SandboxColor ^ 1));

    [Fact(DisplayName = "a colour the game never fills in is no verdict")]
    public void NeverFilled() => Assert.Null(ModeOfRun(0));

    [Fact(DisplayName = "a sandbox-coloured party member does not make my run sandbox")]
    public void PartnerSandbox() => Assert.Equal("normal", ModeOfRun(White, SandboxColor));

    [Fact(DisplayName = "and a white one does not make a sandbox run normal")]
    public void PartnerWhite() => Assert.Equal("sandbox", ModeOfRun(SandboxColor, White));

    private static Plist ColorSequence(long startColor, long middleColor, long endColor)
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1, nameColor: startColor));
        d.StepWith(TtfReader(start: 1, nameColor: middleColor));
        return d.StepWith(TtfReader(start: 1, end: 1, nameColor: endColor))[0];
    }

    [Fact(DisplayName = "a colour missing on the start frame is read from a later one")]
    public void MissingOnStart()
    {
        var run = ColorSequence(0, SandboxColor, SandboxColor);
        Assert.Equal("sandbox", run.Str("account-mode"));
        Assert.Equal(SandboxColor, run.Num("my-name-color"));
    }

    [Fact(DisplayName = "an unrecognised colour at the start does not block the real one")]
    public void UnrecognisedAtStart() =>
        Assert.Equal("sandbox", ColorSequence(0xDEADBEEF, SandboxColor, SandboxColor).Str("account-mode"));

    [Fact(DisplayName = "a white reading at the start is corrected by a sandbox one")]
    public void WhiteCorrected() => Assert.Equal("sandbox", ColorSequence(White, SandboxColor, SandboxColor).Str("account-mode"));

    [Fact(DisplayName = "but a white reading is not lost to an unrecognised one")]
    public void WhiteNotLost() => Assert.Equal("normal", ColorSequence(White, 0xDEADBEEF, 0).Str("account-mode"));

    [Fact(DisplayName = "a sandbox reading is not overwritten mid-load")]
    public void SandboxFinal() => Assert.Equal("sandbox", ColorSequence(SandboxColor, White, White).Str("account-mode"));

    [Fact(DisplayName = "every run of one load lands on the same board")]
    public void SameBoard()
    {
        var (d, _) = NewDetector(CatalogWith(SegmentDef()));
        var modes = new List<string?>();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1, nameColor: 0));
        d.StepWith(TtfReader(start: 1, nameColor: SandboxColor));
        modes.AddRange(d.StepWith(TtfReader(start: 1, seg: 1, nameColor: SandboxColor)).Select(r => r.Str("account-mode")));
        modes.AddRange(d.StepWith(TtfReader(start: 1, seg: 1, end: 1, nameColor: SandboxColor)).Select(r => r.Str("account-mode")));
        Assert.Equal(2, modes.Count);
        Assert.All(modes, m => Assert.Equal("sandbox", m));
    }

    [Fact(DisplayName = "an account change between quests lands each run on its board")]
    public void AccountChange()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1, nameColor: SandboxColor));
        var first = d.StepWith(TtfReader(start: 1, end: 1, nameColor: SandboxColor))[0];
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        var second = d.StepWith(TtfReader(start: 1, end: 1))[0];
        Assert.Equal("sandbox", first.Str("account-mode"));
        Assert.Equal("normal", second.Str("account-mode"));
    }

    [Fact(DisplayName = "an abandoned run carries its mode too")]
    public void AbandonedCarriesMode()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1, nameColor: SandboxColor));
        d.AgeTrackers(20_000);
        var run = d.StepWith(LobbyReader()).FirstOrDefault();
        Assert.NotNull(run);
        Assert.True(run.Truthy("aborted"));
        Assert.Equal("sandbox", run.Str("account-mode"));
    }

    // ------------------------------------------------------------ PB category

    [Fact(DisplayName = "charged PB at start alone stays No PB")]
    public void ChargedAtStart()
    {
        var (d, clock) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1, pb: 80.0f));
        clock.AdvanceMs(20);
        Assert.False(d.StepWith(TtfReader(start: 1, end: 1, pb: 80.0f))[0].Truthy("pb"));
    }

    private static Plist PbSequence(params (float Pb, bool Warping)[] frames)
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        foreach (var (pb, warping) in frames) d.StepWith(TtfReader(start: 1, pb: pb, warping: warping));
        return d.StepWith(TtfReader(start: 1, end: 1))[0];
    }

    [Fact(DisplayName = "PB discharge -> PB category")]
    public void Discharge() => Assert.True(PbSequence((100.0f, false), (2.0f, false)).Truthy("pb"));

    [Fact(DisplayName = "partial gauge zeroed (telepipe) stays No PB")]
    public void Telepipe() => Assert.False(PbSequence((60.0f, false), (0.0f, false)).Truthy("pb"));

    [Fact(DisplayName = "full gauge zeroed across a warp stays No PB")]
    public void AcrossWarp() => Assert.False(PbSequence((100.0f, false), (100.0f, true), (0.0f, false)).Truthy("pb"));

    [Fact(DisplayName = "no discharge -> No PB")]
    public void NoDischarge() => Assert.False(PbSequence((0f, false)).Truthy("pb"));

    // ------------------------------------------------------------ segments

    private sealed record SegmentFlow(int BothActive, List<Plist> First, int StillRunning, List<Plist> Second, List<Plist> After);

    private static SegmentFlow Segments()
    {
        var (d, clock) = NewDetector(CatalogWith(SegmentDef()));
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        var both = d.ActiveCount;
        clock.AdvanceMs(50);
        var first = d.StepWith(TtfReader(start: 1, seg: 1));
        var still = d.ActiveCount;
        clock.AdvanceMs(50);
        var second = d.StepWith(TtfReader(start: 1, seg: 1, end: 1));
        var after = d.StepWith(TtfReader(start: 1, seg: 1, end: 1));
        return new SegmentFlow(both, first, still, second, after);
    }

    [Fact(DisplayName = "segment: both trackers active")]
    public void SegmentBoth() => Assert.Equal(2, Segments().BothActive);

    [Fact(DisplayName = "segment run emitted first")]
    public void SegmentFirst() => Assert.Equal(["ep1-towards-the-future-2-rooms"], Segments().First.Select(r => r.Str("quest-slug")));

    [Fact(DisplayName = "full clear still running")]
    public void FullStillRunning() => Assert.Equal(1, Segments().StillRunning);

    [Fact(DisplayName = "full clear emitted second")]
    public void FullSecond() => Assert.Equal(["ep1-towards-the-future"], Segments().Second.Select(r => r.Str("quest-slug")));

    [Fact(DisplayName = "full clear time > segment possible")]
    public void FullTime() => Assert.True(Segments().Second[0].Num("time-ms") >= 100);

    [Fact(DisplayName = "segment does not restart while quest loaded")]
    public void SegmentNoRestart() => Assert.Empty(Segments().After);

    // ------------------------------------------------------------ warp-in and NPCs

    [Fact(DisplayName = "warp-in: idle on Pioneer 2")]
    public void WarpInIdle() => Assert.Equal(DetectorState.Idle, WarpIn().OnP2);

    [Fact(DisplayName = "warp-in: started once on the field")]
    public void WarpInStarted() => Assert.Equal(DetectorState.InQuest, WarpIn().OnField);

    [Fact(DisplayName = "warp-in quest completes")]
    public void WarpInCompletes() => Assert.Equal("ep1-endless-nightmare-1", WarpIn().Run?.Str("quest-slug"));

    private static (DetectorState OnP2, DetectorState OnField, Plist? Run) WarpIn()
    {
        var (d, _) = NewDetector();
        var p2 = GameRegions(players: [PlayerBlock("Ryu", classId: 2, floor: 0)], questName: "Endless Nightmare #1", questNumber: 108);
        var field = GameRegions(players: [PlayerBlock("Ryu", classId: 2, floor: 1)], questName: "Endless Nightmare #1", questNumber: 108,
            registerValues: [(30, 1)]);
        d.StepWith(LobbyReader());
        d.StepWith(p2);
        var onP2 = d.State;
        d.StepWith(field);
        var onField = d.State;
        var run = d.StepWith(field).FirstOrDefault();
        return (onP2, onField, run);
    }

    private static MockReader NpcRegions(int myFloor, params (int, int)[] registers) => GameRegions(
        players:
        [
            PlayerBlock("Ryu", classId: 2, floor: myFloor, guildCard: "42001234"),
            PlayerBlock("Mr.X", classId: 1, floor: 1, guildCard: "Mr.X"),
        ],
        questName: "Endless Nightmare #1", questNumber: 108, registerValues: registers);

    [Fact(DisplayName = "warp-in: a landed NPC alone does not start")]
    public void NpcAloneNoStart()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(NpcRegions(0));
        Assert.Equal(DetectorState.Idle, d.State);
    }

    [Fact(DisplayName = "warp-in: NPC excluded from the party")]
    public void NpcExcluded()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(NpcRegions(0));
        d.StepWith(NpcRegions(1));
        var run = d.StepWith(NpcRegions(1, (30, 1)))[0];
        Assert.Equal(1, run.Num("party-size"));
        Assert.Equal(["Ryu"], run.Players().Select(p => p.Str("name")));
    }

    [Fact(DisplayName = "unknown quest stays idle")]
    public void UnknownQuest()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(GameRegions(players: [PlayerBlock("Ryu", classId: 2, floor: 1)], questName: "Gallon's Shop", questNumber: 9999,
            registerValues: [(12, 1)]));
        Assert.Equal(DetectorState.Idle, d.State);
    }

    [Fact(DisplayName = "game gone -> idle")]
    public void GameGone()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        d.Step(null);
        Assert.Equal(DetectorState.Idle, d.State);
    }

    // ------------------------------------------------------------ abort

    [Fact(DisplayName = "abort: quick lobby return emits nothing")]
    public void QuickAbort()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        Assert.Empty(d.StepWith(LobbyReader()));
    }

    private static (Plist? Run, DetectorState State) Aborted()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        d.AgeTrackers(20_000);
        var run = d.StepWith(LobbyReader()).FirstOrDefault();
        return (run, d.State);
    }

    [Fact(DisplayName = "abort: lobby return emits aborted run")]
    public void AbortEmits() => Assert.NotNull(Aborted().Run);

    [Fact(DisplayName = "abort: marked aborted")]
    public void AbortMarked() => Assert.True(Aborted().Run!.Get("aborted") is SSymbol { IsTSymbol: true });

    [Fact(DisplayName = "abort: slug")]
    public void AbortSlug() => Assert.Equal("ep1-towards-the-future", Aborted().Run!.Str("quest-slug"));

    [Fact(DisplayName = "abort: time >= 20s")]
    public void AbortTime() => Assert.True(Aborted().Run!.Num("time-ms") >= 20000);

    [Fact(DisplayName = "abort: quest name captured at start")]
    public void AbortName() => Assert.Equal("Towards the Future", Aborted().Run!.Str("quest-name"));

    [Fact(DisplayName = "abort: party captured")]
    public void AbortParty() => Assert.Equal(2, Aborted().Run!.Num("party-size"));

    [Fact(DisplayName = "abort: telemetry attached")]
    public void AbortTelemetry() => Assert.True(Aborted().Run!.Truthy("telemetry"));

    [Fact(DisplayName = "abort: detector reset")]
    public void AbortReset() => Assert.Equal(DetectorState.Idle, Aborted().State);

    [Fact(DisplayName = "abort: game exit emits aborted run")]
    public void GameExitAbort()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        d.AgeTrackers(20_000);
        Assert.True(d.Step(null)[0].Truthy("aborted"));
    }

    [Fact(DisplayName = "abort: nothing re-emitted after completion")]
    public void NothingAfterCompletion()
    {
        var (d, _) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(start: 1));
        d.AgeTrackers(20_000);
        d.StepWith(TtfReader(start: 1, end: 1));
        Assert.Empty(d.StepWith(LobbyReader()));
    }
}

/// <summary>tests-detect.lisp run-anguish-tests.</summary>
public class AnguishTests
{
    [Fact(DisplayName = "read-f64 decodes 1.82")]
    public void ReadF64()
    {
        var bytes = Zeros(8);
        PutF64(bytes, 0, 1.82);
        Assert.True(Math.Abs(1.82 - new MockReader((100, bytes)).ReadF64(100)!.Value) < 1e-9);
    }

    [Fact(DisplayName = "scale 1.0 -> no anguish")]
    public void Scale1() => Assert.Null(PsobbTables.AnguishLevel(1.0));

    [Fact(DisplayName = "scale NIL -> no anguish")]
    public void ScaleNil() => Assert.Null(PsobbTables.AnguishLevel(null));

    [Fact(DisplayName = "scale 1.30 -> Anguish 1")]
    public void Scale130() => Assert.Equal(1, PsobbTables.AnguishLevel(1.30));

    [Fact(DisplayName = "scale 1.82 -> Anguish 2")]
    public void Scale182() => Assert.Equal(2, PsobbTables.AnguishLevel(1.82));

    [Fact(DisplayName = "scale 2.50 -> Anguish 3")]
    public void Scale250() => Assert.Equal(3, PsobbTables.AnguishLevel(2.50));

    [Fact(DisplayName = "odd scale matches nearest level")]
    public void OddScale() => Assert.Equal(2, PsobbTables.AnguishLevel(2.0));

    [Fact(DisplayName = "label Ultimate + level 2")]
    public void LabelAnguish() => Assert.Equal("Anguish 2", PsobbTables.DifficultyLabel(3, 2));

    [Fact(DisplayName = "label Ultimate alone")]
    public void LabelUltimate() => Assert.Equal("Ultimate", PsobbTables.DifficultyLabel(3, null));

    [Fact(DisplayName = "label non-Ultimate ignores anguish")]
    public void LabelNormal() => Assert.Equal("Normal", PsobbTables.DifficultyLabel(0, 2));

    [Fact(DisplayName = "snapshot anguish level")]
    public void SnapshotAnguish() => Assert.Equal(3, PsobbReader.ReadSnapshot(TtfReader(difficulty: 3, hpScale: 2.50))!.Anguish);

    [Fact(DisplayName = "snapshot without hp table -> no anguish")]
    public void SnapshotNoTable() => Assert.Null(PsobbReader.ReadSnapshot(TtfReader(difficulty: 3))!.Anguish);

    [Fact(DisplayName = "snapshot at scale 1.0 -> no anguish")]
    public void SnapshotScale1() => Assert.Null(PsobbReader.ReadSnapshot(TtfReader(difficulty: 3, hpScale: 1.0))!.Anguish);

    [Fact(DisplayName = "anguish run difficulty label")]
    public void AnguishRun()
    {
        var (d, clock) = NewDetector();
        d.StepWith(LobbyReader());
        d.StepWith(TtfReader(difficulty: 3, hpScale: 1.30, start: 1));
        clock.AdvanceMs(20);
        Assert.Equal("Anguish 1", d.StepWith(TtfReader(difficulty: 3, hpScale: 1.30, start: 1, end: 1))[0].Str("difficulty"));
    }
}

/// <summary>tests-detect.lisp run-monster-clear-tests.</summary>
public class MonsterClearTests
{
    private static Snapshot MonSnapshot(params (int Id, int Hp)[] monsters) => new()
    {
        Episode = 1,
        MyIndex = 0,
        QuestPtr = 1,
        QuestName = "Monster Test",
        QuestNumber = 9001,
        Difficulty = 0,
        Players = [new PlayerState { Index = 0, Name = "Ryu", Class = "HUcast", Floor = 1 }],
        Monsters = monsters.Select(m => new MonsterState { Id = m.Id, Hp = m.Hp }).ToList(),
    };

    private static Snapshot MonLobby() => new()
    {
        Players = [new PlayerState { Index = 0, Name = "Ryu", Class = "HUcast", Floor = 0 }],
        QuestPtr = 0,
    };

    private static (Detector, ManualGameClock) New() => NewDetector(new QuestCatalog(
        [new QuestDef("monster-test-kill-boss", 1, ["Monster Test"], 9001, new WarpInTrigger(), new MonsterDeadTrigger(7))]));

    private sealed record Flow(DetectorState AfterStart, List<Plist> WrongEnemy, Plist? Run, DetectorState After);

    private static Flow Kill()
    {
        var (d, clock) = New();
        d.Step(MonLobby());
        d.Step(MonSnapshot((7, 200), (8, 50)));
        var afterStart = d.State;
        clock.AdvanceMs(20);
        var wrong = d.Step(MonSnapshot((7, 120), (8, 0)));
        clock.AdvanceMs(20);
        var run = d.Step(MonSnapshot((7, 0), (8, 0))).FirstOrDefault();
        return new Flow(afterStart, wrong, run, d.State);
    }

    [Fact(DisplayName = "in-quest after warp-in start")]
    public void InQuest() => Assert.Equal(DetectorState.InQuest, Kill().AfterStart);

    [Fact(DisplayName = "wrong enemy dying does not clear")]
    public void WrongEnemy() => Assert.Empty(Kill().WrongEnemy);

    [Fact(DisplayName = "target enemy dying emits the run")]
    public void TargetDies() => Assert.NotNull(Kill().Run);

    [Fact(DisplayName = "monster-clear run slug")]
    public void Slug() => Assert.Equal("monster-test-kill-boss", Kill().Run!.Str("quest-slug"));

    [Fact(DisplayName = "detector idle after monster-kill clear")]
    public void IdleAfter() => Assert.Equal(DetectorState.Idle, Kill().After);

    [Fact(DisplayName = "target seen only at 0 hp never clears")]
    public void ZeroHpNeverClears()
    {
        var (d, clock) = New();
        d.Step(MonLobby());
        d.Step(MonSnapshot((7, 0), (9, 80)));
        clock.AdvanceMs(20);
        Assert.Empty(d.Step(MonSnapshot((7, 0), (9, 80))));
    }
}
