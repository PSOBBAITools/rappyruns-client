using System.Globalization;
using System.Text.Json;
using RappyRuns.Core.Game;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Tests.Game;

/// <summary>
/// Differential golden (desktop/tools/export-game-golden.lisp): the Lisp
/// client's detector-step, run-json, trigger log, last kill and room picker
/// over realistic snapshot sequences, replayed through the C# port. Runs must
/// match as the exact POST body (number tokens included) and as the exact
/// queue.sexp plist text.
/// </summary>
public class DetectorGoldenTests
{
    private static readonly JsonElement Root = Golden.Load("game/detector-scenarios.json");

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var s in Root.GetProperty("scenarios").EnumerateArray()) data.Add(s.GetProperty("name").GetString()!);
        return data;
    }

    [Theory(DisplayName = "golden scenario")]
    [MemberData(nameof(Scenarios))]
    public void Scenario(string name)
    {
        var scenario = Root.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);
        var clock = new ManualGameClock(Root.GetProperty("ticks_per_second").GetInt64())
        {
            UniversalTime = Root.GetProperty("universal_time").GetInt64(),
            Now = scenario.GetProperty("start_now").GetInt64(),
        };
        var catalog = GameTestKit.BuiltinCatalog();
        catalog.SetServerQuests(scenario.GetProperty("server_quests"));
        var detector = new Detector(catalog, clock);
        var logs = new RunLogs(clock);
        var stamp = Root.GetProperty("log_stamp").GetString()!;
        Snapshot? previous = null;
        var i = 0;
        foreach (var step in scenario.GetProperty("steps").EnumerateArray())
        {
            var where = $"{name} step {i}";
            clock.Now = step.GetProperty("now").GetInt64();
            var snapshot = ParseSnapshot(step.GetProperty("snapshot"));
            var runs = detector.Step(snapshot);
            var expected = step.GetProperty("runs").EnumerateArray().ToList();
            Assert.True(expected.Count == runs.Count, $"{where}: {runs.Count} runs, Lisp emitted {expected.Count}");
            for (var r = 0; r < runs.Count; r++)
            {
                var lispJson = expected[r].GetProperty("run_json").GetString()!;
                var csJson = RunPayload.RunJson(runs[r]);
                AssertJsonEqual(JsonDocument.Parse(lispJson).RootElement, JsonDocument.Parse(csJson).RootElement, $"{where} run {r}");
                Assert.Equal(lispJson, csJson);
                Assert.Equal(expected[r].GetProperty("sexp").GetString(), SexpWriter.Write(runs[r].ToSexp()));
            }
            Assert.True(step.GetProperty("state").GetString() == (detector.State == DetectorState.InQuest ? "in-quest" : "idle"), where);
            Assert.True(step.GetProperty("active").GetInt32() == detector.ActiveCount, where);
            var lines = TriggerLog.ChangeLines(previous, snapshot, stamp) ?? [];
            Assert.Equal(step.GetProperty("log_lines").EnumerateArray().Select(l => l.GetString()!), lines);
            logs.UpdateLastKill(previous, snapshot);
            logs.UpdateRunLogs(previous, snapshot);
            var lastKill = step.GetProperty("last_kill");
            if (lastKill.ValueKind == JsonValueKind.Null) Assert.Null(logs.LastKill);
            else
                Assert.Equal(new LastKill(lastKill.GetProperty("id").GetInt32(), Str(lastKill.GetProperty("name")), Long(lastKill.GetProperty("unitxt"))),
                    logs.LastKill);
            previous = snapshot;
            i++;
        }
        var runQuest = scenario.GetProperty("run_quest");
        if (runQuest.ValueKind == JsonValueKind.Null) Assert.Null(logs.RunQuest);
        else
            Assert.Equal(new RunQuest(Int(runQuest.GetProperty("number")), Str(runQuest.GetProperty("name")), Int(runQuest.GetProperty("episode"))),
                logs.RunQuest);
        var rows = logs.RoomRows();
        var expectedRows = scenario.GetProperty("room_rows").EnumerateArray().ToList();
        Assert.Equal(expectedRows.Count, rows.Count);
        for (var r = 0; r < rows.Count; r++)
        {
            Assert.Equal(expectedRows[r].GetProperty("area").GetString(), rows[r].Area);
            Assert.Equal(expectedRows[r].GetProperty("kind").GetString(), rows[r].Kind == RoomRowKind.Clear ? "clear" : "enemy");
            Assert.Equal(Str(expectedRows[r].GetProperty("name")), rows[r].Name);
            Assert.Equal(expectedRows[r].GetProperty("trigger").GetString(), SexpWriter.Write(rows[r].Trigger.ToSexp()));
        }
    }

    [Fact(DisplayName = "golden: quest-triggers.sexp reads as the Lisp reads it")]
    public void QuestDefs()
    {
        var expected = Golden.Load("game/quest-defs.json").EnumerateArray().ToList();
        var defs = GameTestKit.BuiltinCatalog().Defs;
        Assert.Equal(expected.Count, defs.Count);
        for (var i = 0; i < defs.Count; i++)
        {
            var e = expected[i];
            var d = defs[i];
            Assert.Equal(e.GetProperty("slug").GetString(), d.Slug);
            Assert.Equal(Int(e.GetProperty("episode")), d.Episode);
            Assert.Equal(e.GetProperty("names").EnumerateArray().Select(n => n.GetString()!), d.Names);
            Assert.Equal(Int(e.GetProperty("number")), d.Number);
            Assert.Equal(TriggerText(e.GetProperty("start")), d.Start?.ToJson());
            Assert.Equal(TriggerText(e.GetProperty("end")), d.End?.ToJson());
        }
    }

    private static string? TriggerText(JsonElement e) =>
        e.ValueKind == JsonValueKind.Null ? null : JsonSerializer.Serialize(e); // compact, key order kept

    // ------------------------------------------------------------ JSON → Snapshot

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? Int(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;

    private static long? Long(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt64() : null;

    private static JsonElement? P(JsonElement o, string key) => o.TryGetProperty(key, out var v) ? v : null;

    private static int? I(JsonElement o, string key) => P(o, key) is { } v ? Int(v) : null;

    private static long? L(JsonElement o, string key) => P(o, key) is { } v ? Long(v) : null;

    private static float? F(JsonElement o, string key) =>
        P(o, key) is { ValueKind: JsonValueKind.Number } v ? float.Parse(v.GetRawText(), CultureInfo.InvariantCulture) : null;

    private static string? S(JsonElement o, string key) => P(o, key) is { } v ? Str(v) : null;

    private static bool B(JsonElement o, string key) => P(o, key) is { ValueKind: JsonValueKind.True };

    internal static Snapshot? ParseSnapshot(JsonElement s)
    {
        if (s.ValueKind == JsonValueKind.Null) return null;
        byte[]? registers = null;
        if (P(s, "registers") is { ValueKind: JsonValueKind.Object } regs)
        {
            registers = new byte[1024];
            foreach (var r in regs.EnumerateObject()) MockMemory.PutU16(registers, 4 * int.Parse(r.Name, CultureInfo.InvariantCulture), r.Value.GetInt32());
        }
        byte[]? switches = null;
        if (P(s, "floor_switches") is { ValueKind: JsonValueKind.Array } sw)
            switches = MockMemory.FloorSwitchArray(sw.EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToArray());
        return new Snapshot
        {
            Episode = I(s, "episode"),
            MyIndex = I(s, "my_index"),
            Players = P(s, "players") is { ValueKind: JsonValueKind.Array } ps ? ps.EnumerateArray().Select(ParsePlayer).ToList() : [],
            Difficulty = I(s, "difficulty"),
            Map = I(s, "map"),
            MapVariation = I(s, "map_variation"),
            FastBurst = B(s, "fast_burst"),
            QuestPtr = L(s, "quest_ptr"),
            QuestName = S(s, "quest_name"),
            QuestNumber = I(s, "quest_number"),
            Anguish = I(s, "anguish"),
            Registers = registers,
            FloorSwitches = switches,
            Monsters = P(s, "monsters") is { ValueKind: JsonValueKind.Array } ms ? ms.EnumerateArray().Select(ParseMonster).ToList() : null,
            Inventory = P(s, "inventory") is { ValueKind: JsonValueKind.Object } inv ? ParseInventory(inv) : null,
        };
    }

    private static PlayerState ParsePlayer(JsonElement p) => new()
    {
        Index = I(p, "index"), Name = S(p, "name"), Class = S(p, "class"), SectionId = S(p, "section_id"),
        Level = I(p, "level"), GuildCard = S(p, "guild_card"), Npc = B(p, "npc"), NameColor = L(p, "name_color"),
        Floor = I(p, "floor"), Room = I(p, "room"), X = F(p, "x"), Y = F(p, "y"), Z = F(p, "z"), Facing = I(p, "facing"),
        Warping = B(p, "warping"), State = I(p, "state"), CurrentTech = I(p, "current_tech"), Hp = I(p, "hp"),
        MaxHp = I(p, "max_hp"), Tp = I(p, "tp"), MaxTp = I(p, "max_tp"), Meseta = L(p, "meseta"), Pb = F(p, "pb"),
        Shifta = I(p, "shifta"), Deband = I(p, "deband"), Invincible = B(p, "invincible"),
        DamageTraps = I(p, "damage_traps"), FreezeTraps = I(p, "freeze_traps"), ConfuseTraps = I(p, "confuse_traps"),
    };

    private static MonsterState ParseMonster(JsonElement m) => new()
    {
        Id = I(m, "id")!.Value, Unitxt = L(m, "unitxt"), Index = I(m, "index"), Name = S(m, "name"), Hp = I(m, "hp"),
        LastAttacker = I(m, "last_attacker"), X = F(m, "x"), Y = F(m, "y"), Z = F(m, "z"), Facing = I(m, "facing"),
        Frozen = B(m, "frozen"), Confused = B(m, "confused"), Paralyzed = B(m, "paralyzed"),
    };

    private static Equipment ParseEquipment(JsonElement e) =>
        new(e.GetProperty("id").GetString()!, e.GetProperty("type").GetString()!, e.GetProperty("display").GetString()!);

    private static Inventory ParseInventory(JsonElement inv) => new()
    {
        Equipment = inv.GetProperty("equipment").EnumerateArray().Select(ParseEquipment).ToList(),
        Weapon = inv.GetProperty("weapon") is { ValueKind: JsonValueKind.Object } w ? ParseEquipment(w) : null,
        Consumables = inv.GetProperty("consumables").EnumerateArray().Select(c => (c[0].GetString()!, c[1].GetInt32())).ToList(),
    };

    /// <summary>Structural JSON equality: key order free, number tokens compared as written.</summary>
    internal static void AssertJsonEqual(JsonElement expected, JsonElement actual, string path)
    {
        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: {actual.ValueKind} vs Lisp {expected.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var ek = expected.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
                var ak = actual.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
                Assert.True(ek.SequenceEqual(ak), $"{path}: keys [{string.Join(",", ak)}] vs Lisp [{string.Join(",", ek)}]");
                foreach (var k in ek) AssertJsonEqual(expected.GetProperty(k), actual.GetProperty(k), $"{path}.{k}");
                break;
            case JsonValueKind.Array:
                Assert.True(expected.GetArrayLength() == actual.GetArrayLength(), $"{path}: length {actual.GetArrayLength()} vs Lisp {expected.GetArrayLength()}");
                for (var i = 0; i < expected.GetArrayLength(); i++) AssertJsonEqual(expected[i], actual[i], $"{path}[{i}]");
                break;
            case JsonValueKind.Number:
                Assert.True(expected.GetRawText() == actual.GetRawText(), $"{path}: {actual.GetRawText()} vs Lisp {expected.GetRawText()}");
                break;
            case JsonValueKind.String:
                Assert.True(expected.GetString() == actual.GetString(), $"{path}: \"{actual.GetString()}\" vs Lisp \"{expected.GetString()}\"");
                break;
        }
    }
}
