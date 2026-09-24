using System.Text.Json;
using RappyRuns.Core.Game;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Tests.Game;

/// <summary>Helpers shared by the telemetry suites (tests-memory.lisp:195 tele-snapshot).</summary>
internal static class TeleSnapshots
{
    public static Snapshot Tele(int hp = 100, int tp = 50, int state = 1, float pb = 0f, long meseta = 1000, int floor = 1,
        int map = 1, int tech = 0, int shifta = 0, Inventory? inventory = null, IReadOnlyList<MonsterState>? monsters = null,
        bool fastBurst = false, IEnumerable<PlayerState>? extraPlayers = null) => new()
    {
        MyIndex = 0,
        Map = map,
        QuestPtr = 1,
        FastBurst = fastBurst,
        Players = new[]
        {
            new PlayerState
            {
                Index = 0, Name = "Ryu", Class = "HUcast", Hp = hp, MaxHp = 100, Tp = tp, MaxTp = 50, State = state, Pb = pb,
                Meseta = meseta, Floor = floor, Room = 2, X = 10.04f, Z = -3.06f, Shifta = shifta, Deband = 0,
                Invincible = false, CurrentTech = tech, DamageTraps = 0, FreezeTraps = 0, ConfuseTraps = 0,
            },
        }.Concat(extraPlayers ?? []).ToList(),
        Inventory = inventory,
        Monsters = monsters,
    };

    public static MonsterState M(int id, int hp, long? unitxt = null, int? index = null, string? name = null, int? lastAttacker = null) =>
        new() { Id = id, Hp = hp, Unitxt = unitxt, Index = index, Name = name, LastAttacker = lastAttacker };

    public static Plist Data(Telemetry t) => Plist.From(t.RunData())!;

    public static IReadOnlyList<SexpNode> List(Plist p, string key) => p.Get(key)!.Elements;

    public static long? Alist(Plist p, string key, SexpNode cellKey)
    {
        foreach (var cell in List(p, key))
        {
            var l = (SList)cell;
            if (l.Items[0] == cellKey) return l.Tail!.AsLong;
        }
        return null;
    }
}

/// <summary>tests-memory.lisp run-telemetry-tests.</summary>
public class TelemetryTests
{
    private static Plist Run()
    {
        const long tick = 1_000_000;
        const long start = 5_000_000;
        var tele = new Telemetry(start, tick);
        // Second 0: baseline frame, two monsters alive, 3 monomates.
        tele.Step(TeleSnapshots.Tele(inventory: new Inventory { Consumables = [("monomate", 3)] },
            monsters: [TeleSnapshots.M(1, 50), TeleSnapshots.M(2, 30)]), start);
        // Second 1: cast Resta, one monster killed, one monomate used, 100 meseta charged.
        tele.Step(TeleSnapshots.Tele(state: 8, tech: 0x0F, meseta: 900, inventory: new Inventory { Consumables = [("monomate", 2)] },
            monsters: [TeleSnapshots.M(1, 0), TeleSnapshots.M(2, 30)]), start + tick);
        // Second 2: died.
        tele.Step(TeleSnapshots.Tele(hp: 0, state: 15, meseta: 900), start + 2 * tick);
        return TeleSnapshots.Data(tele);
    }

    [Fact(DisplayName = "one frame per second")]
    public void OneFramePerSecond() => Assert.Equal(3, TeleSnapshots.List(Run(), "frames").Count);

    [Fact(DisplayName = "frame layout matches +frame-keys+")]
    public void FrameLayout() => Assert.Equal(Telemetry.FrameKeys.Count, TeleSnapshots.List(Run(), "frames")[0].Elements.Count);

    [Fact(DisplayName = "death counted")]
    public void Death() => Assert.Equal(1, Run().Get("death-count")!.AsLong);

    [Fact(DisplayName = "kill counted")]
    public void Kill() => Assert.Equal(1, Run().Get("kills")!.AsLong);

    [Fact(DisplayName = "meseta charged")]
    public void Meseta() => Assert.Equal(100, Run().Get("meseta-charged")!.AsLong);

    [Fact(DisplayName = "monomate use counted")]
    public void Monomate() => Assert.Equal("((:MONOMATE . 1))", SexpWriter.Write(Run().Get("items-used")!));

    [Fact(DisplayName = "resta cast counted")]
    public void Resta() => Assert.Equal("((\"Resta\" . 1))", SexpWriter.Write(Run().Get("techs-cast")!));

    [Fact(DisplayName = "bare-handed accrues seconds")]
    public void BareHanded()
    {
        var weapon = TeleSnapshots.List(Run(), "weapons").Select(w => Plist.From(w)!).Single(w => w.Get("id")!.AsString == "Bare Handed");
        Assert.Equal(2, weapon.Get("seconds")!.AsLong);
    }

    [Fact(DisplayName = "time-by-state covers the dead second")]
    public void TimeByState() => Assert.True(TeleSnapshots.Alist(Run(), "time-by-state", SexpNode.Int(15)) > 0);

    [Fact(DisplayName = "run data is printable")]
    public void Printable()
    {
        var text = SexpWriter.Write(Run().ToSexp());
        Assert.Equal(SexpWriter.Write(SexpReader.ReadOne(text)), text);
    }
}

/// <summary>tests-memory.lisp run-psostats-telemetry-tests.</summary>
public class PsostatsTelemetryTests
{
    private static Plist Run()
    {
        const long tick = 1_000_000;
        const long ms = tick / 1000;
        const long start = 7_000_000;
        var tele = new Telemetry(start, tick, maxPartyPbShifta: 21);
        MonsterState M(int id, int hp, long unitxt, int index, int attacker, string? name = null) =>
            TeleSnapshots.M(id, hp, unitxt, index, name, attacker);
        // t=0: a Booma and the Sil Dragon boss are up.
        tele.Step(TeleSnapshots.Tele(monsters: [M(1, 100, 5, 1, 0, "Booma"), M(2, 500, 44, 2, 0, "Sil Dragon")]), start);
        // t=30ms: player 1 damages the Booma; a second Booma spawns.
        tele.Step(TeleSnapshots.Tele(monsters: [M(1, 40, 5, 1, 1), M(2, 500, 44, 2, 0), M(3, 30, 5, 3, 0)]), start + 30 * ms);
        // t=50ms: the fresh Booma dies 20ms after spawning (frame-1 kill).
        tele.Step(TeleSnapshots.Tele(monsters: [M(1, 40, 5, 1, 1), M(2, 500, 44, 2, 0), M(3, 0, 5, 3, 0)]), start + 50 * ms);
        // t=1s: player 1 finishes the first Booma (last hit, 40 hp credit).
        tele.Step(TeleSnapshots.Tele(monsters: [M(1, 0, 5, 1, 1), M(2, 500, 44, 2, 0)]), start + tick);
        // t=2s: own shifta above the party ceiling; a party member warps while fast burst is on.
        tele.Step(TeleSnapshots.Tele(shifta: 25, fastBurst: true, extraPlayers:
        [
            new PlayerState { Index = 1, Name = "Elly", Class = "FOnewearl", GuildCard = "42009999", Floor = 1, Room = 3, X = 1.0f, Y = 0f, Z = 2.0f, Facing = 7, Warping = true },
        ]), start + 2 * tick);
        return TeleSnapshots.Data(tele);
    }

    private static Plist Monster(Plist data, int id) =>
        TeleSnapshots.List(data, "monsters").Select(m => Plist.From(m)!).Single(m => m.Get("id")!.AsLong == id);

    [Fact(DisplayName = "monsters dead counted")]
    public void Kills() => Assert.Equal(2, Run().Get("kills")!.AsLong);

    [Fact(DisplayName = "damage attributed per player")]
    public void Damage()
    {
        var d = Run();
        Assert.Equal(30, TeleSnapshots.Alist(d, "player-damage", SexpNode.Int(0)));
        Assert.Equal(100, TeleSnapshots.Alist(d, "player-damage", SexpNode.Int(1)));
    }

    [Fact(DisplayName = "last hits attributed per player")]
    public void LastHits()
    {
        var d = Run();
        Assert.Equal(1, TeleSnapshots.Alist(d, "last-hits", SexpNode.Int(0)));
        Assert.Equal(1, TeleSnapshots.Alist(d, "last-hits", SexpNode.Int(1)));
    }

    [Fact(DisplayName = "all monsters recorded")]
    public void AllMonsters() => Assert.Equal(3, TeleSnapshots.List(Run(), "monsters").Count);

    [Fact(DisplayName = "frame-1 kill flagged")]
    public void Frame1() => Assert.True(Monster(Run(), 3).Get("frame1")!.IsTrue);

    [Fact(DisplayName = "normal kill not frame-1")]
    public void NotFrame1()
    {
        var slow = Monster(Run(), 1);
        Assert.True(slow.Get("frame1")!.IsNil);
        Assert.InRange(slow.Get("killed-ms")!.AsLong!.Value, 990, 1010);
    }

    [Fact(DisplayName = "boss recorded")]
    public void Boss() => Assert.Equal("Sil Dragon", Plist.From(TeleSnapshots.List(Run(), "bosses")[0])!.Get("name")!.AsString);

    [Fact(DisplayName = "boss hp history follows the frames")]
    public void BossHp() => Assert.Equal("(500 500 500)", SexpWriter.Write(Plist.From(TeleSnapshots.List(Run(), "bosses")[0])!.Get("hp")!));

    [Fact(DisplayName = "monster hp pool follows the frames")]
    public void HpPool() => Assert.Equal("(600 500 500)", SexpWriter.Write(Run().Get("monster-hp-pool")!));

    [Fact(DisplayName = "illegal shifta flagged")]
    public void IllegalShifta() => Assert.True(Run().Get("illegal-shifta")!.IsTrue);

    [Fact(DisplayName = "fast warp flagged")]
    public void FastWarp() => Assert.True(Run().Get("fast-warps")!.IsTrue);

    [Fact(DisplayName = "pb ceiling kept")]
    public void PbCeiling() => Assert.Equal(21, Run().Get("max-party-pb-shifta")!.AsLong);

    [Fact(DisplayName = "frames carry the damage column")]
    public void DamageColumn()
    {
        var frame = TeleSnapshots.List(Run(), "frames")[1].Elements;
        Assert.Equal(30, frame[Telemetry.FrameKeys.ToList().IndexOf("damage")].AsLong);
    }

    [Fact(DisplayName = "parity run data is printable")]
    public void Printable()
    {
        var text = SexpWriter.Write(Run().ToSexp());
        Assert.Equal(SexpWriter.Write(SexpReader.ReadOne(text)), text);
    }

    private static JsonElement Json() => JsonDocument.Parse(RunPayload.TelemetryJson(Run().ToSexp())).RootElement;

    [Fact(DisplayName = "json monsters array")]
    public void JsonMonsters() => Assert.Equal(3, Json().GetProperty("monsters").GetArrayLength());

    [Fact(DisplayName = "json frame1 only on the fast kill")]
    public void JsonFrame1() => Assert.Equal([3],
        Json().GetProperty("monsters").EnumerateArray()
            .Where(m => m.TryGetProperty("frame1", out var f) && f.ValueKind == JsonValueKind.True)
            .Select(m => m.GetProperty("id").GetInt32()));

    [Fact(DisplayName = "json bosses keyed by monster id")]
    public void JsonBosses() => Assert.Equal("Sil Dragon", Json().GetProperty("bosses").GetProperty("2").GetProperty("name").GetString());

    [Fact(DisplayName = "json player damage keyed by player index")]
    public void JsonPlayerDamage() => Assert.Equal(100, Json().GetProperty("player_damage").GetProperty("1").GetInt32());

    [Fact(DisplayName = "json monster hp pool")]
    public void JsonHpPool() => Assert.Equal(3, Json().GetProperty("monster_hp_pool").GetArrayLength());

    [Fact(DisplayName = "json cheat flags")]
    public void JsonCheatFlags()
    {
        Assert.True(Json().GetProperty("illegal_shifta").GetBoolean());
        Assert.True(Json().GetProperty("fast_warps").GetBoolean());
    }

    private static JsonElement Locs()
    {
        var frame = Json().GetProperty("frames")[2];
        return frame[Telemetry.FrameKeys.ToList().IndexOf("player_locs")];
    }

    [Fact(DisplayName = "json frame player locations keyed by guild card")]
    public void JsonLocsKeys()
    {
        var locs = Locs();
        Assert.Equal(JsonValueKind.Object, locs.ValueKind);
        Assert.True(locs.TryGetProperty("Ryu", out _));
        Assert.Equal(7, locs.GetProperty("42009999").GetArrayLength());
    }

    [Fact(DisplayName = "json frame warp flag rides in the location row")]
    public void JsonWarpFlag() => Assert.Equal(1, Locs().GetProperty("42009999")[6].GetInt32());
}
