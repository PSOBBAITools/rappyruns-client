using System.Text.Json;
using RappyRuns.Core.Game;
using static RappyRuns.Tests.Game.GameTestKit;
using static RappyRuns.Tests.Game.MockMemory;

namespace RappyRuns.Tests.Game;

/// <summary>tests-quests.lisp run-server-defs-tests.</summary>
public class ServerDefsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private const string GdvServerQuests = """
        [{"slug":"ep2-gdv-reset","episode":2,"game_number":944,
          "start":{"type":"floor-switch","floor":5,"switch":0},
          "end":{"type":"floor-switch","floor":5,"switch":2}},
         {"slug":"ep1-some-catalog-quest","episode":1}]
        """;

    private static (QuestCatalog Catalog, int Count, int Builtin) Merged()
    {
        var catalog = BuiltinCatalog();
        var builtin = catalog.Builtin.Count;
        var count = catalog.SetServerQuests(J(GdvServerQuests));
        return (catalog, count, builtin);
    }

    [Fact(DisplayName = "set-server-quest-defs counts only timeable entries")]
    public void CountsTimeable() => Assert.Equal(1, Merged().Count);

    [Fact(DisplayName = "server def merged into active defs")]
    public void Merged_() => Assert.Contains(Merged().Catalog.Defs, d => d.Slug == "ep2-gdv-reset");

    [Fact(DisplayName = "builtin defs still present after merge")]
    public void BuiltinStillPresent()
    {
        var (catalog, _, builtin) = Merged();
        Assert.Equal(builtin + 1, catalog.Defs.Count);
    }

    private static QuestDef Gdv() => Merged().Catalog.Defs.Single(d => d.Slug == "ep2-gdv-reset");

    [Fact(DisplayName = "server def start trigger converted")]
    public void StartConverted() => Assert.Equal(new FloorSwitchTrigger(5, 0), Gdv().Start);

    [Fact(DisplayName = "server def end trigger converted")]
    public void EndConverted() => Assert.Equal(new FloorSwitchTrigger(5, 2), Gdv().End);

    [Fact(DisplayName = "server def keeps game number")]
    public void KeepsNumber() => Assert.Equal(944, Gdv().Number);

    [Fact(DisplayName = "empty refetch drops server defs, keeps builtin")]
    public void EmptyRefetch()
    {
        var (catalog, _, builtin) = Merged();
        catalog.SetServerQuests(J("[]"));
        Assert.Equal(builtin, catalog.Defs.Count);
    }
}

/// <summary>tests-quests.lisp run-gdv-segment-test.</summary>
public class GdvSegmentTests
{
    /// <summary>tests-quests.lisp:50 gdv-reader: MAE Gal Da Val, start floor 5 switch 0, room 2 = switch 2, full = register 254.</summary>
    private static MockReader GdvReader(int start = 0, int room2 = 0, int full = 0)
    {
        var on = new List<(int, int)>();
        if (start > 0) on.Add((5, 0));
        if (room2 > 0) on.Add((5, 2));
        var reader = GameRegions(episodeRaw: 1, players: [PlayerBlock("Ryu", classId: 2, floor: 5)],
            questName: "Maximum Attack E: Gal Da Val", questNumber: 944, registerValues: [(254, full)]);
        reader.Regions.Insert(0, (0x00AC9FA0, FloorSwitchArray(on.ToArray())));
        return reader;
    }

    private sealed record Flow(int Both, List<Core.Sexp.Plist> Reset, int StillRunning, List<Core.Sexp.Plist> Full);

    private static Flow Run()
    {
        var catalog = BuiltinCatalog();
        catalog.SetServerQuests(JsonDocument.Parse("""
            [{"slug":"ep2-gdv-reset","episode":2,"game_number":944,
              "start":{"type":"floor-switch","floor":5,"switch":0},
              "end":{"type":"floor-switch","floor":5,"switch":2}}]
            """).RootElement);
        var (d, clock) = NewDetector(catalog);
        d.StepWith(LobbyReader());
        d.StepWith(GdvReader());
        d.StepWith(GdvReader(start: 1));
        var both = d.ActiveCount;
        clock.AdvanceMs(50);
        var reset = d.StepWith(GdvReader(start: 1, room2: 1));
        var still = d.ActiveCount;
        clock.AdvanceMs(50);
        var full = d.StepWith(GdvReader(start: 1, room2: 1, full: 1));
        return new Flow(both, reset, still, full);
    }

    [Fact(DisplayName = "GDV: both full-clear and reset tracked")]
    public void Both() => Assert.Equal(2, Run().Both);

    [Fact(DisplayName = "GDV reset emitted at room 2")]
    public void Reset() => Assert.Equal(["ep2-gdv-reset"], Run().Reset.Select(r => r.Str("quest-slug")));

    [Fact(DisplayName = "GDV full clear still running")]
    public void StillRunning() => Assert.Equal(1, Run().StillRunning);

    [Fact(DisplayName = "GDV full clear emitted at register 254")]
    public void Full() => Assert.Equal(["ep2-maximum-attack-e-gal-da-val"], Run().Full.Select(r => r.Str("quest-slug")));
}

/// <summary>tests-quests.lisp run-trigger-log-tests.</summary>
public class TriggerLogTests
{
    /// <summary>The Lisp test's sequence on a throwaway log; returns what each check looks at.</summary>
    private static (bool Created, int? Changes, string Text) Sequence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eta-test-trigger-log-{Guid.NewGuid():N}.txt");
        try
        {
            using var log = new TriggerLog(path, new ManualGameClock());
            log.Start();
            var created = File.Exists(path);
            var set2 = Zeros(32 * 18);
            set2[32 * 5] = 0x80 >> 2;
            var prev = new Snapshot { QuestName = "GDV", QuestPtr = 1, FloorSwitches = Zeros(32 * 18), Registers = Zeros(1024) };
            var next = new Snapshot { QuestName = "GDV", QuestPtr = 1, FloorSwitches = set2, Registers = Zeros(1024) };
            var changes = log.LogChanges(prev, next);
            log.Close();
            return (created, changes, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "start-trigger-log creates the file at once")]
    public void CreatesFile()
    {
        var (created, _, text) = Sequence();
        Assert.True(created);
        Assert.StartsWith("=== trigger logging started 12:00:00 ===\nPlay the segment; each register / floor-switch change is listed below.\n", text);
    }

    [Fact(DisplayName = "diff records the flipped switch")]
    public void RecordsFlip() => Assert.Equal(1, Sequence().Changes);

    [Fact(DisplayName = "log names floor 5 switch 2")]
    public void NamesSwitch()
    {
        var text = Sequence().Text;
        Assert.Contains("floor 5 switch 2", text);
        Assert.Contains("12:00:00 \"GDV\" floor 5 switch 2: off -> on\n", text);
    }

    [Fact(DisplayName = "trigger-log line format: registers, switches, kills; ~s quoting")]
    public void LineFormat()
    {
        var oldRegs = Zeros(1024);
        var newRegs = Zeros(1024);
        PutU16(newRegs, 4 * 12, 1);
        var prev = new Snapshot
        {
            QuestName = "A \"B\"", QuestPtr = 7, Registers = oldRegs, FloorSwitches = Zeros(576),
            Monsters = [new MonsterState { Id = 5475, Hp = 10, Name = "Gal Gryphon", Unitxt = 77 }],
        };
        var next = prev with
        {
            Registers = newRegs, FloorSwitches = FloorSwitchArray((1, 9)),
            Monsters = [new MonsterState { Id = 5475, Hp = 0, Name = null, Unitxt = 77 }],
        };
        Assert.Equal(
        [
            "10:11:12 \"A \\\"B\\\"\" register 12: 0 -> 1",
            "10:11:12 \"A \\\"B\\\"\" floor 1 switch 9: off -> on",
            "10:11:12 \"A \\\"B\\\"\" monster 5475 killed (?, unitxt 77)",
        ], TriggerLog.ChangeLines(prev, next, "10:11:12")!);
        Assert.Null(TriggerLog.ChangeLines(prev, next with { QuestPtr = 8 }, "x"));
    }
}

/// <summary>tests-quests.lisp run-quest-rule-tests.</summary>
public class QuestRuleTests
{
    private static Snapshot Q(long ptr, params MonsterState[] monsters) => new() { QuestPtr = ptr, Monsters = monsters };

    private static MonsterState M(int id, int hp, string? name = null, long? unitxt = null) => new() { Id = id, Hp = hp, Name = name, Unitxt = unitxt };

    private static List<MonsterState> Killed() => RunLogs.NewlyKilledMonsters(
        Q(1, M(7, 200, "Booma", 44), M(8, 50, "Rag", 45)),
        Q(1, M(7, 0, "Booma", 44), M(8, 50, "Rag", 45)));

    [Fact(DisplayName = "one enemy transitioned alive->dead")]
    public void OneKilled() => Assert.Single(Killed());

    [Fact(DisplayName = "the killed enemy is id 7")]
    public void KilledIs7() => Assert.Equal(7, Killed()[0].Id);

    [Fact(DisplayName = "an enemy only ever at 0 hp is not a kill")]
    public void ZeroHpNotKill() => Assert.Empty(RunLogs.NewlyKilledMonsters(Q(1, M(9, 0)), Q(1, M(9, 0))));

    private static RunLogs AfterKill()
    {
        var logs = new RunLogs(new ManualGameClock());
        logs.UpdateLastKill(Q(1, M(7, 200, "Booma", 44)), Q(1, M(7, 0, "Booma", 44)));
        return logs;
    }

    [Fact(DisplayName = "last-kill records the id")]
    public void LastKillId() => Assert.Equal(7, AfterKill().LastKill?.Id);

    [Fact(DisplayName = "last-kill records the name")]
    public void LastKillName() => Assert.Equal("Booma", AfterKill().LastKill?.Name);

    [Fact(DisplayName = "leaving the quest clears last-kill")]
    public void LeavingClears()
    {
        var logs = AfterKill();
        logs.UpdateLastKill(Q(1), Q(0));
        Assert.Null(logs.LastKill);
    }

    [Fact(DisplayName = "a quest reload clears last-kill")]
    public void ReloadClears()
    {
        var logs = AfterKill();
        logs.UpdateLastKill(Q(1, M(7, 100)), Q(2, M(7, 0)));
        Assert.Null(logs.LastKill);
    }

    private static JsonElement J(Trigger t) => JsonDocument.Parse(t.ToJson()).RootElement;

    [Fact(DisplayName = "monster trigger type")]
    public void MonsterType() => Assert.Equal("monster", J(new MonsterDeadTrigger(1234)).GetProperty("type").GetString());

    [Fact(DisplayName = "monster trigger id")]
    public void MonsterId() => Assert.Equal(1234, J(new MonsterDeadTrigger(1234)).GetProperty("monster").GetInt32());

    [Fact(DisplayName = "floor-switch type")]
    public void FloorSwitchType() => Assert.Equal("floor-switch", J(new FloorSwitchTrigger(5, 2)).GetProperty("type").GetString());

    [Fact(DisplayName = "floor-switch floor")]
    public void FloorSwitchFloor() => Assert.Equal(5, J(new FloorSwitchTrigger(5, 2)).GetProperty("floor").GetInt32());

    [Fact(DisplayName = "floor-switch switch")]
    public void FloorSwitchSwitch() => Assert.Equal(2, J(new FloorSwitchTrigger(5, 2)).GetProperty("switch").GetInt32());

    [Fact(DisplayName = "register type")]
    public void RegisterType() => Assert.Equal("register", J(new RegisterTrigger(254)).GetProperty("type").GetString());

    [Fact(DisplayName = "register value")]
    public void RegisterValue() => Assert.Equal(254, J(new RegisterTrigger(254)).GetProperty("register").GetInt32());

    [Fact(DisplayName = "warp-in trigger type")]
    public void WarpInType() => Assert.Equal("warp-in", J(new WarpInTrigger()).GetProperty("type").GetString());

    [Fact(DisplayName = "nil trigger -> nil")]
    public void NilTrigger() => Assert.Null(((Trigger?)null)?.ToJson());

    [Fact(DisplayName = "trigger survives json round-trip")]
    public void RoundTrip()
    {
        var body = JsonDocument.Parse("{\"end\":" + new MonsterDeadTrigger(1234).ToJson() + "}").RootElement;
        Assert.Equal(1234, body.GetProperty("end").GetProperty("monster").GetInt32());
        Assert.Equal(new MonsterDeadTrigger(1234), Trigger.FromJson(body.GetProperty("end")));
    }

    [Fact(DisplayName = "trigger json and sexp forms round-trip for every type")]
    public void AllTypesRoundTrip()
    {
        Trigger[] all = [new RegisterTrigger(254), new FloorSwitchTrigger(5, 2), new WarpInTrigger(), new MonsterDeadTrigger(5475)];
        foreach (var t in all)
        {
            Assert.Equal(t, Trigger.FromJson(JsonDocument.Parse(t.ToJson()).RootElement));
            Assert.Equal(t, Trigger.FromSexp(t.ToSexp()));
        }
    }
}

/// <summary>tests-quests.lisp run-room-picker-tests.</summary>
public class RoomPickerTests
{
    private static Snapshot RoomSnap(long ptr, int floor, int room, IReadOnlyList<MonsterState> monsters, byte[] switches) => new()
    {
        QuestPtr = ptr, MyIndex = 0, Episode = 1, QuestNumber = 9001, QuestName = "Room Test", Map = 1,
        Players = [new PlayerState { Index = 0, Floor = floor, Room = room }],
        Monsters = monsters, FloorSwitches = switches,
    };

    private static MonsterState M(int id, int hp, string name, long unitxt) => new() { Id = id, Hp = hp, Name = name, Unitxt = unitxt };

    [Fact(DisplayName = "floor-switch 0->1 detected")]
    public void FlipDetected()
    {
        var flipped = RunLogs.NewlySetFloorSwitches(new Snapshot { FloorSwitches = FloorSwitchArray() },
            new Snapshot { FloorSwitches = FloorSwitchArray((5, 2)) });
        Assert.Equal([(5, 2)], flipped);
    }

    [Fact(DisplayName = "floor-switch 1->0 ignored")]
    public void FlipOffIgnored() => Assert.Empty(RunLogs.NewlySetFloorSwitches(
        new Snapshot { FloorSwitches = FloorSwitchArray((5, 2)) }, new Snapshot { FloorSwitches = FloorSwitchArray() }));

    private static readonly Snapshot Lobby = new() { QuestPtr = 0 };

    private static (RunLogs Logs, Snapshot D) Played()
    {
        var clock = new ManualGameClock();
        var logs = new RunLogs(clock);
        var clear = FloorSwitchArray();
        var a = RoomSnap(1, 1, 2, [M(7, 100, "Booma", 44)], clear);
        var b = RoomSnap(1, 1, 2, [M(7, 0, "Booma", 44)], FloorSwitchArray((1, 5)));
        var c = RoomSnap(1, 1, 3, [M(8, 80, "Rag", 45)], FloorSwitchArray((1, 5)));
        var d = RoomSnap(1, 1, 3, [M(8, 0, "Rag", 45)], FloorSwitchArray((1, 5)));
        logs.UpdateRunLogs(Lobby, a);
        clock.AdvanceMs(33);
        logs.UpdateRunLogs(a, b);
        clock.AdvanceMs(33);
        logs.UpdateRunLogs(b, c);
        clock.AdvanceMs(33);
        logs.UpdateRunLogs(c, d);
        return (logs, d);
    }

    [Fact(DisplayName = "run-quest captured on load")]
    public void RunQuestCaptured()
    {
        var logs = new RunLogs(new ManualGameClock());
        logs.UpdateRunLogs(Lobby, RoomSnap(1, 1, 2, [], FloorSwitchArray()));
        Assert.Equal(1, logs.RunQuest?.Episode);
    }

    private static RunRoom Room(int room) => Played().Logs.RunRooms().Single(r => r.Room == room);

    [Fact(DisplayName = "run-rooms groups two rooms")]
    public void TwoRooms() => Assert.Equal(2, Played().Logs.RunRooms().Count);

    [Fact(DisplayName = "room 2 last kill is enemy 7")]
    public void Room2LastKill() => Assert.Equal(7, Room(2).LastKill.Id);

    [Fact(DisplayName = "room 2 kill tagged with its floor/room")]
    public void Room2Tagged()
    {
        Assert.Equal(1, Room(2).Kills[0].Floor);
        Assert.Equal(2, Room(2).Kills[0].Room);
    }

    [Fact(DisplayName = "room 2 correlated clear switch is floor 1 switch 5")]
    public void Room2Switch()
    {
        Assert.Equal(1, Room(2).Switch?.Floor);
        Assert.Equal(5, Room(2).Switch?.Switch);
    }

    [Fact(DisplayName = "room 3 last kill is enemy 8")]
    public void Room3LastKill() => Assert.Equal(8, Room(3).LastKill.Id);

    [Fact(DisplayName = "room 3 has no clear switch")]
    public void Room3NoSwitch() => Assert.Null(Room(3).Switch);

    [Fact(DisplayName = "RoomRows off the poll thread survives the logs being reset under it (PR #330 review)")]
    public void RoomRowsConcurrentWithReset()
    {
        var clock = new ManualGameClock();
        var logs = new RunLogs(clock);
        var stop = new ManualResetEventSlim();
        Exception? failure = null;
        var reader = new Thread(() =>
        {
            try
            {
                while (!stop.IsSet) logs.RoomRows();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        reader.Start();
        var deadline = DateTime.UtcNow.AddMilliseconds(300);
        long ptr = 1;
        while (DateTime.UtcNow < deadline && failure is null)
        {
            // Grow a switch log (one kill, many switches in the same room), then
            // reset it with a fresh quest load.
            var previous = RoomSnap(ptr, 1, 2, [M(7, 100, "Booma", 44)], FloorSwitchArray());
            logs.UpdateRunLogs(Lobby, previous);
            for (var s = 0; s < 16; s++)
            {
                var next = RoomSnap(ptr, 1, 2, [M(7, 0, "Booma", 44)], FloorSwitchArray([.. Enumerable.Range(0, s + 1).Select(i => (1, i))]));
                logs.UpdateRunLogs(previous, next);
                previous = next;
            }
            ptr++;
        }
        stop.Set();
        reader.Join();
        Assert.Null(failure);
    }

    [Fact(DisplayName = "client-map-name in range")]
    public void MapNameInRange() => Assert.Equal("Forest 1", RunLogs.ClientMapName(1));

    [Fact(DisplayName = "client-map-name out of range")]
    public void MapNameOutOfRange() => Assert.Equal("Map 99", RunLogs.ClientMapName(99));

    [Fact(DisplayName = "room carries its map")]
    public void RoomMap() => Assert.Equal(1, Played().Logs.RunRooms()[0].Map);

    [Fact(DisplayName = "room-area-label uses the map name")]
    public void AreaLabel() => Assert.Equal("Forest 1 · room 2", RunLogs.RoomAreaLabel(Room(2).Map, Room(2).Room));

    private static List<RoomRow> Rows() => Played().Logs.RoomRows();

    [Fact(DisplayName = "run-room-rows count")]
    public void RowCount() => Assert.Equal(4, Rows().Count);

    [Fact(DisplayName = "every room has a clear row")]
    public void ClearRows() => Assert.Equal(2, Rows().Count(r => r.Kind == RoomRowKind.Clear));

    [Fact(DisplayName = "room 2 clear is the floor-switch")]
    public void Room2Clear() => Assert.Contains(Rows(), r => r.Kind == RoomRowKind.Clear && r.Trigger == new FloorSwitchTrigger(1, 5));

    [Fact(DisplayName = "room 3 clear falls back to the last enemy")]
    public void Room3Clear() => Assert.Contains(Rows(), r => r.Kind == RoomRowKind.Clear && r.Trigger == new MonsterDeadTrigger(8));

    [Fact(DisplayName = "enemy row trigger is monster-dead")]
    public void EnemyRow() => Assert.IsType<MonsterDeadTrigger>(Rows().First(r => r.Kind == RoomRowKind.Enemy).Trigger);

    [Fact(DisplayName = "run logs survive into the lobby")]
    public void SurviveLobby()
    {
        var (logs, d) = Played();
        logs.UpdateRunLogs(d, Lobby);
        Assert.Equal(2, logs.RunRooms().Count);
    }

    [Fact(DisplayName = "a new quest load resets the run logs")]
    public void NewLoadResets()
    {
        var (logs, d) = Played();
        logs.UpdateRunLogs(d, Lobby);
        logs.UpdateRunLogs(Lobby, RoomSnap(2, 1, 1, [], FloorSwitchArray()));
        Assert.Empty(logs.RunRooms());
    }
}
