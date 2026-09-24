using RappyRuns.Core.Game;
using static RappyRuns.Tests.Game.MockMemory;

namespace RappyRuns.Tests.Game;

/// <summary>client/tests/tests-memory.lisp run-memory-tests (memory/snapshot).</summary>
public class MemoryTests
{
    private static readonly MockReader Small = new((100, new byte[] { 1, 2, 3, 4, 0x78, 0x56, 0x34, 0x12 }));

    [Fact(DisplayName = "read-u8")]
    public void ReadU8() => Assert.Equal(1, Small.ReadU8(100));

    [Fact(DisplayName = "read-u16 little-endian")]
    public void ReadU16() => Assert.Equal(0x0201, Small.ReadU16(100));

    [Fact(DisplayName = "read-u32 little-endian")]
    public void ReadU32() => Assert.Equal(0x12345678u, Small.ReadU32(104));

    [Fact(DisplayName = "read out of range -> NIL")]
    public void ReadOutOfRange() => Assert.Null(Small.ReadU16(200));

    [Fact(DisplayName = "f32 decode 12.5")]
    public void F32Decode() => Assert.True(Math.Abs(12.5 - MemoryDecode.U32Float(0x41480000)) < 0.001);

    private static Snapshot TtfSnapshot() => PsobbReader.ReadSnapshot(GameRegions(
        episodeRaw: 0,
        players: [PlayerBlock("Ryu", classId: 2, floor: 5), PlayerBlock("Elly", classId: 8, floor: 5)],
        questName: "Towards the Future",
        questNumber: 118,
        registerValues: [(12, 1)]))!;

    [Fact(DisplayName = "snapshot episode")]
    public void SnapshotEpisode() => Assert.Equal(1, TtfSnapshot().Episode);

    [Fact(DisplayName = "snapshot quest name")]
    public void SnapshotQuestName() => Assert.Equal("Towards the Future", TtfSnapshot().QuestName);

    [Fact(DisplayName = "snapshot quest number")]
    public void SnapshotQuestNumber() => Assert.Equal(118, TtfSnapshot().QuestNumber);

    [Fact(DisplayName = "snapshot two players")]
    public void SnapshotTwoPlayers() => Assert.Equal(2, TtfSnapshot().Players.Count);

    [Fact(DisplayName = "player name decoded (\\tE stripped)")]
    public void PlayerNameDecoded() => Assert.Equal("Ryu", TtfSnapshot().Players[0].Name);

    [Fact(DisplayName = "player classes decoded")]
    public void PlayerClassesDecoded() =>
        Assert.Equal(["HUcast", "FOnewearl"], TtfSnapshot().Players.Select(p => p.Class));

    [Fact(DisplayName = "register 12 set")]
    public void Register12Set() => Assert.True(TtfSnapshot().RegisterSet(12));

    [Fact(DisplayName = "register 254 clear")]
    public void Register254Clear() => Assert.False(TtfSnapshot().RegisterSet(254));

    [Fact(DisplayName = "floor switch clear")]
    public void FloorSwitchClear() => Assert.False(TtfSnapshot().FloorSwitchSet(4, 99));
}

/// <summary>tests-memory.lisp run-inventory-tests.</summary>
public class InventoryTests
{
    /// <summary>tests-memory.lisp:43 make-item-block.</summary>
    internal static (long, byte[]) ItemBlock(long addr, int owner = 0, int type = 0, int group = 0, int index = 0,
        bool equipped = false, int idHigh = 0, int idLow = 0, int? toolCount = null)
    {
        var bytes = Zeros(0x200);
        PutU16(bytes, 0xD8, idLow);
        PutU16(bytes, 0xDA, idHigh);
        bytes[0xE4] = (byte)owner;
        bytes[0xF2] = (byte)type;
        bytes[0xF3] = (byte)group;
        bytes[0xF4] = (byte)index;
        if (equipped) bytes[0x190] = 1;
        if (toolCount is { } c) bytes[0x104] = (byte)(c ^ ((addr + 0x104) & 0xFF));
        return (addr, bytes);
    }

    private static Inventory? MultiplayerInventory()
    {
        const long arrayBase = 0x00610000;
        const int itemCount = 70;
        var globals = Zeros(8);
        var pointers = Zeros(4 * itemCount);
        const long myWeapon = 0x00600000, myMate = 0x00600400, theirWeapon = 0x00600800, theirMate = 0x00600C00;
        PutU32(globals, 0, arrayBase);
        PutU16(globals, 4, itemCount);
        PutU32(pointers, 4 * 0, myWeapon);
        PutU32(pointers, 4 * 63, theirWeapon);
        PutU32(pointers, 4 * 65, myMate);
        PutU32(pointers, 4 * 69, theirMate);
        var reader = new MockReader(
            (0x00A8D81C, globals),
            (arrayBase, pointers),
            ItemBlock(myWeapon, owner: 0, type: 0, group: 2, index: 5, equipped: true, idHigh: 0x0001, idLow: 0x0203),
            ItemBlock(myMate, owner: 0, type: 3, group: 0, index: 0, toolCount: 5),
            ItemBlock(theirWeapon, owner: 1, type: 0, group: 2, index: 5, equipped: true),
            ItemBlock(theirMate, owner: 1, type: 3, group: 0, index: 0, toolCount: 99));
        return PsobbReader.ReadInventory(reader, 0);
    }

    [Fact(DisplayName = "inventory read past 60 items")]
    public void ReadPast60() => Assert.NotNull(MultiplayerInventory());

    [Fact(DisplayName = "only my equipment listed")]
    public void OnlyMyEquipment() => Assert.Single(MultiplayerInventory()!.Equipment);

    [Fact(DisplayName = "equipped weapon resolved")]
    public void WeaponResolved() => Assert.Equal("00010203", MultiplayerInventory()!.Weapon!.Id);

    [Fact(DisplayName = "equipped weapon typed")]
    public void WeaponTyped() => Assert.Equal("weapon", MultiplayerInventory()!.Weapon!.Type);

    [Fact(DisplayName = "my consumables counted")]
    public void ConsumablesCounted() =>
        Assert.Equal(5, MultiplayerInventory()!.Consumables.Single(c => c.Key == "monomate").Count);

    [Fact(DisplayName = "garbage item count -> NIL")]
    public void GarbageCount()
    {
        var globals = Zeros(8);
        PutU32(globals, 0, 0x00610000);
        PutU16(globals, 4, 0xFFFF);
        Assert.Null(PsobbReader.ReadInventory(new MockReader((0x00A8D81C, globals)), 0));
    }
}

/// <summary>tests-memory.lisp run-extended-player-tests.</summary>
public class ExtendedPlayerTests
{
    private static Snapshot Snap() => PsobbReader.ReadSnapshot(GameRegions(
        difficulty: 3, map: 5,
        players:
        [
            PlayerBlock("Ryu", classId: 2, floor: 5, sectionId: 2, levelRaw: 41, room: 7, state: 4, hp: 945, maxHp: 1200,
                tp: 300, maxTp: 400, meseta: 123456, guildCard: "42001234"),
        ]))!;

    private static PlayerState Me() => Snap().MyPlayer!;

    [Fact(DisplayName = "difficulty in snapshot")]
    public void Difficulty() => Assert.Equal(3, Snap().Difficulty);

    [Fact(DisplayName = "difficulty name")]
    public void DifficultyName() => Assert.Equal("Ultimate", PsobbTables.DifficultyName(3));

    [Fact(DisplayName = "map in snapshot")]
    public void Map() => Assert.Equal(5, Snap().Map);

    [Fact(DisplayName = "section id decoded")]
    public void Section() => Assert.Equal("Skyly", Me().SectionId);

    [Fact(DisplayName = "level decoded (+1)")]
    public void Level() => Assert.Equal(42, Me().Level);

    [Fact(DisplayName = "guild card decoded")]
    public void GuildCard() => Assert.Equal("42001234", Me().GuildCard);

    [Fact(DisplayName = "a numeric guild card is a person")]
    public void NumericCardPerson() => Assert.False(Me().Npc);

    [Fact(DisplayName = "room decoded")]
    public void Room() => Assert.Equal(7, Me().Room);

    [Fact(DisplayName = "action state decoded")]
    public void State() => Assert.Equal(4, Me().State);

    [Fact(DisplayName = "hp decoded")]
    public void Hp() => Assert.Equal(945, Me().Hp);

    [Fact(DisplayName = "max hp decoded")]
    public void MaxHp() => Assert.Equal(1200, Me().MaxHp);

    [Fact(DisplayName = "tp decoded")]
    public void Tp() => Assert.Equal(300, Me().Tp);

    [Fact(DisplayName = "meseta decoded")]
    public void Meseta() => Assert.Equal(123456, Me().Meseta);

    [Fact(DisplayName = "no shifta -> level 0")]
    public void NoShifta() => Assert.Equal(0, Me().Shifta);

    [Fact(DisplayName = "name colour decoded (a normal player's is white)")]
    public void NameColorWhite() => Assert.Equal(0xFFFFFFFF, Me().NameColor);

    /// <summary>The 64 bytes at player+#x930 of a live Sandbox character (2026-09-22), guild card digits made up.</summary>
    private static PlayerState LiveSandbox()
    {
        byte[] live =
        [
            0x34, 0x32, 0x30, 0x30, 0x30, 0x30, 0x30, 0x31, 0x00, 0x31, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x23, 0x94, 0xAB, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x07, 0x03, 0x00, 0x03, 0x45, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00,
        ];
        var block = PlayerBlock("a", floor: 1);
        Array.Copy(live, 0, block, 0x930, live.Length);
        return PsobbReader.ReadSnapshot(GameRegions(players: [block]))!.MyPlayer!;
    }

    [Fact(DisplayName = "live sandbox bytes: guild card")]
    public void LiveGuildCard() => Assert.Equal("42000001", LiveSandbox().GuildCard);

    [Fact(DisplayName = "live sandbox bytes: class and section")]
    public void LiveClassSection()
    {
        var me = LiveSandbox();
        Assert.Equal("RAmar", me.Class);
        Assert.Equal("Oran", me.SectionId);
    }

    [Fact(DisplayName = "live sandbox bytes: the name colour is the sandbox one")]
    public void LiveColor() => Assert.Equal(PsobbTables.SandboxNameColor, LiveSandbox().NameColor);

    [Fact(DisplayName = "live sandbox bytes: the verdict is sandbox")]
    public void LiveVerdict() => Assert.Equal("sandbox", PsobbTables.AccountModeOfColor(LiveSandbox().NameColor));

    [Fact(DisplayName = "the verdict ignores the alpha byte")]
    public void VerdictIgnoresAlpha()
    {
        Assert.Equal("sandbox", PsobbTables.AccountModeOfColor(0x00AB9423));
        Assert.Equal("sandbox", PsobbTables.AccountModeOfColor(0x80AB9423));
        Assert.Equal("normal", PsobbTables.AccountModeOfColor(0x80FFFFFF));
    }

    [Fact(DisplayName = "no colour, and a colour not filled in, are no verdict")]
    public void NoVerdict()
    {
        Assert.Null(PsobbTables.AccountModeOfColor(null));
        Assert.Null(PsobbTables.AccountModeOfColor(0));
    }

    [Fact(DisplayName = "shifta multiplier -> level")]
    public void ShiftaLevel() => Assert.Equal(20, PsobbTables.ShiftaLevel(0.347f));

    [Fact(DisplayName = "tech name lookup")]
    public void TechName() => Assert.Equal("Resta", PsobbTables.TechName(0x0F));
}

/// <summary>tests-memory.lisp run-monster-read-tests.</summary>
public class MonsterReadTests
{
    private const long NpcArrayBase = 0x00630000;
    private const long HpTableBase = 0x00640000;
    private const long MonsterABase = 0x00650000;
    private const long MonsterBBase = 0x00660000;

    private static byte[] MonsterBlock(int id = 0, long unitxt = 0, float x = 0, float y = 0, float z = 0, int facing = 0,
        int status = 0, int paralyzed = 0, int attacker = 0, float zuY = 0)
    {
        var bytes = Zeros(0x420);
        PutU16(bytes, 0x1C, id);
        PutU32(bytes, 0x378, unitxt);
        PutF32(bytes, 0x38, x);
        PutF32(bytes, 0x3C, y);
        PutF32(bytes, 0x40, z);
        PutU16(bytes, 0x60, facing);
        PutU16(bytes, 0x268, status);
        PutU16(bytes, 0x25C, paralyzed);
        PutU16(bytes, 0x2D8, attacker);
        PutF32(bytes, 0x418, zuY);
        return bytes;
    }

    /// <summary>tests-memory.lisp:285 make-monster-reader: one player slot, a Booma (7), an empty slot and a Zu (8) whose hp underflowed.</summary>
    internal static MockReader MonsterReader(int hp7 = 50, int hp8 = 0xFFFF)
    {
        var npcPtr = Zeros(4);
        var counts = Zeros(8);
        var hpPtr = Zeros(4);
        var hpTable = Zeros(512);
        var pointers = Zeros(16);
        var unitxtPtr = Zeros(4);
        var unitxt = Zeros(20);
        var names = Zeros(512);
        var booma = Zeros(32);
        var zu = Zeros(32);
        PutU32(npcPtr, 0, NpcArrayBase);
        PutU32(counts, 0, 3);
        PutU32(counts, 4, 1);
        PutU32(hpPtr, 0, HpTableBase);
        PutU16(hpTable, 4 + 32 * 7, hp7);
        PutU16(hpTable, 4 + 32 * 8, hp8);
        PutU32(pointers, 4 * 1, MonsterABase);
        PutU32(pointers, 4 * 2, 0);
        PutU32(pointers, 4 * 3, MonsterBBase);
        PutU32(unitxtPtr, 0, 0x00670000);
        PutU32(unitxt, 16, 0x00680000);
        PutU32(names, 4 * 5, 0x00690000);
        PutU32(names, 4 * 94, 0x00690040);
        PutUtf16(booma, 0, "Booma");
        PutUtf16(zu, 0, "Zu");
        return new MockReader(
            (0x007B4BA2, npcPtr),
            (0x00AAE164, counts),
            (0x00B5F800, hpPtr),
            (HpTableBase, hpTable),
            (NpcArrayBase, pointers),
            (MonsterABase, MonsterBlock(id: 7, unitxt: 5, x: 10.5f, y: 2.0f, z: 3.25f, facing: 100, status: 0x02, attacker: 1)),
            (MonsterBBase, MonsterBlock(id: 8, unitxt: 94, y: 2.0f, zuY: 3.0f, paralyzed: 0x10)),
            (0x00A9CD50, unitxtPtr),
            (0x00670000, unitxt),
            (0x00680000, names),
            (0x00690000, booma),
            (0x00690040, zu));
    }

    private static List<MonsterState> Monsters()
    {
        PsobbReader.ClearNameCaches();
        return PsobbReader.ReadMonsters(MonsterReader())!;
    }

    private static MonsterState Booma() => Monsters().Single(m => m.Id == 7);

    private static MonsterState Zu() => Monsters().Single(m => m.Id == 8);

    [Fact(DisplayName = "two monsters read (empty slot skipped)")]
    public void TwoMonsters() => Assert.Equal(2, Monsters().Count);

    [Fact(DisplayName = "monster id and unitxt")]
    public void IdUnitxt() => Assert.Equal(5, Booma().Unitxt);

    [Fact(DisplayName = "monster name resolved")]
    public void Name() => Assert.Equal("Booma", Booma().Name);

    [Fact(DisplayName = "monster hp from the Ephinea table")]
    public void HpFromTable() => Assert.Equal(50, Booma().Hp);

    [Fact(DisplayName = "monster position")]
    public void Position()
    {
        Assert.True(Math.Abs(10.5 - Booma().X!.Value) < 0.01);
        Assert.True(Math.Abs(3.25 - Booma().Z!.Value) < 0.01);
    }

    [Fact(DisplayName = "monster facing")]
    public void Facing() => Assert.Equal(100, Booma().Facing);

    [Fact(DisplayName = "monster frozen")]
    public void Frozen()
    {
        var b = Booma();
        Assert.True(b.Frozen && !b.Paralyzed && !b.Confused);
    }

    [Fact(DisplayName = "monster last attacker")]
    public void LastAttacker() => Assert.Equal(1, Booma().LastAttacker);

    [Fact(DisplayName = "monster index counts player slots")]
    public void IndexCountsPlayers() => Assert.Equal(1, Booma().Index);

    [Fact(DisplayName = "hp underflow clamps to zero")]
    public void Underflow() => Assert.Equal(0, Zu().Hp);

    [Fact(DisplayName = "monster paralyzed")]
    public void Paralyzed() => Assert.True(Zu().Paralyzed && !Zu().Frozen);

    [Fact(DisplayName = "Zu height adds the extra field")]
    public void ZuHeight() => Assert.True(Math.Abs(5.0 - Zu().Y!.Value) < 0.01);

    [Fact(DisplayName = "hp block covers the live id span in one read")]
    public void HpBlockSpan()
    {
        var block = PsobbReader.MonsterHpBlock(MonsterReader(), HpTableBase, [7, 8]);
        Assert.NotNull(block);
        Assert.Equal(7, block.Value.LowId);
        Assert.Equal(64, block.Value.Bytes.Length);
    }

    [Fact(DisplayName = "hp block refuses a garbage id span")]
    public void HpBlockGarbage() => Assert.Null(PsobbReader.MonsterHpBlock(MonsterReader(), HpTableBase, [0, 60000]));

    [Fact(DisplayName = "hp block absent without a table")]
    public void HpBlockNoTable() => Assert.Null(PsobbReader.MonsterHpBlock(MonsterReader(), 0, [7, 8]));

    private static MockReader FastBurstReader(int flag)
    {
        var b = Zeros(4);
        var link = Zeros(4);
        var value = Zeros(2);
        PutU32(b, 0, 0x100);
        PutU32(link, 0, 0x00620000);
        PutU16(value, 0, flag);
        return new MockReader((0x5B92DA, b), (0x100 + 0x5B92DF, link), (0x00620000, value));
    }

    [Fact(DisplayName = "fast burst detected")]
    public void FastBurst() => Assert.True(PsobbReader.FastBurstEnabled(FastBurstReader(0)));

    [Fact(DisplayName = "slow burst detected")]
    public void SlowBurst() => Assert.False(PsobbReader.FastBurstEnabled(FastBurstReader(1)));

    [Fact(DisplayName = "boss: Sil Dragon")]
    public void BossSil() => Assert.Equal("Sil Dragon", PsobbTables.BossName(44, 5));

    [Fact(DisplayName = "boss: Dal Ra Lie only at index 0")]
    public void BossDalRaLie()
    {
        Assert.Equal("Dal Ra Lie", PsobbTables.BossName(45, 0));
        Assert.Null(PsobbTables.BossName(45, 3));
    }

    [Fact(DisplayName = "boss: Saint-Million head numbering")]
    public void BossSaintMillion() => Assert.Equal("Saint-Million Head (2)", PsobbTables.BossName(106, 6));

    [Fact(DisplayName = "non-boss unitxt -> NIL")]
    public void NonBoss() => Assert.Null(PsobbTables.BossName(5, 0));

    [Fact(DisplayName = "solo HUcast pb ceiling")]
    public void SoloHucast() => Assert.Equal(21, PsobbTables.MaxPartyPbShifta(["HUcast"]));

    [Fact(DisplayName = "solo FOnewearl can out-shifta the pb ceiling")]
    public void SoloFonewearl() => Assert.Equal(30, PsobbTables.MaxPartyPbShifta(["FOnewearl"]));

    [Fact(DisplayName = "two-player pb ceiling")]
    public void TwoPlayer() => Assert.Equal(41, PsobbTables.MaxPartyPbShifta(["HUcast", "RAmar"]));
}
