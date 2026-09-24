using System.Collections.Concurrent;
using System.Globalization;
using static RappyRuns.Core.Game.PsobbLayout;

namespace RappyRuns.Core.Game;

/// <summary>
/// Snapshot, player, inventory and monster reads over an <see cref="IMemoryReader"/>
/// (psobb.lisp:313-823). The reads are batched exactly like the Lisp (spec core
/// §22 #35): one block per player, one pointer block for the 12 slots, one
/// block per monster plus one HP-table block, one block per item.
/// </summary>
public static class PsobbReader
{
    // unitxt data is static for a game session; like the Lisp caches these
    // live for the process (spec core §22 #49).
    private static readonly ConcurrentDictionary<long, string> ItemNameCache = new();
    private static readonly ConcurrentDictionary<long, string> MonsterNameCache = new();

    /// <summary>Forget cached unitxt names (tests: clrhash *monster-name-cache*).</summary>
    public static void ClearNameCaches()
    {
        ItemNameCache.Clear();
        MonsterNameCache.Clear();
    }

    /// <summary>psobb.lisp:313 read-player: one struct from a single block read; null when unreadable.</summary>
    public static PlayerState? ReadPlayer(IMemoryReader reader, long address)
    {
        var block = reader.ReadBlock(address + PlayerBlockStart, PlayerBlockEnd - PlayerBlockStart);
        if (block is null) return null;
        int U8(int offset) => block[offset - PlayerBlockStart];
        int U16(int offset) => MemoryDecode.U16(block, offset - PlayerBlockStart);
        uint U32(int offset) => MemoryDecode.U32(block, offset - PlayerBlockStart);
        float F32(int offset) => MemoryDecode.U32Float(U32(offset));

        var classBits = U16(PlayerClassOffset);
        var stateBits = U16(PlayerStateOffset);
        var guildCard = MemoryDecode.AsciiZ(block, PlayerGuildCardOffset - PlayerBlockStart, 8).Trim(' ');
        return new PlayerState
        {
            Name = PsobbTables.StripNamePrefix(MemoryDecode.Utf16Z(block, PlayerNameOffset - PlayerBlockStart, 24)),
            Class = PsobbTables.ClassNameForId((classBits & 0xF00) >> 8),
            SectionId = PsobbTables.SectionNameForId(classBits & 0xFF),
            Level = 1 + U16(PlayerLevelOffset),
            GuildCard = guildCard.Length > 0 ? guildCard : null,
            // The Lisp passes the trimmed card, "" included: "" is all digits, so a person.
            Npc = PsobbTables.NpcGuildCard(guildCard),
            NameColor = U32(PlayerNameColorOffset),
            Floor = U16(PlayerFloorOffset),
            Room = U16(PlayerRoomOffset),
            X = F32(PlayerXOffset),
            Y = F32(PlayerYOffset),
            Z = F32(PlayerZOffset),
            Facing = U16(PlayerFacingOffset),
            Warping = (stateBits & 0x04) != 0,
            State = U16(PlayerActionStateOffset),
            CurrentTech = U16(PlayerCurrentTechOffset),
            Hp = U16(PlayerHpOffset),
            MaxHp = U16(PlayerMaxHpOffset),
            Tp = U16(PlayerTpOffset),
            MaxTp = U16(PlayerMaxTpOffset),
            Meseta = U32(PlayerMesetaOffset),
            Pb = F32(PlayerPbOffset),
            Shifta = PsobbTables.ShiftaLevel(F32(PlayerShiftaOffset)),
            Deband = PsobbTables.ShiftaLevel(F32(PlayerDebandOffset)),
            Invincible = U32(PlayerInvincibilityOffset) > 0,
            DamageTraps = U8(PlayerDamageTrapsOffset),
            FreezeTraps = U8(PlayerFreezeTrapsOffset),
            ConfuseTraps = U8(PlayerConfuseTrapsOffset),
        };
    }

    /// <summary>
    /// psobb.lisp:361 read-players: slot order preserved, zero pointers and
    /// unreadable structs dropped. The 12 pointers come in one read, with a
    /// per-slot fallback for a reader that cannot serve the whole block.
    /// </summary>
    public static List<PlayerState> ReadPlayers(IMemoryReader reader)
    {
        var pointers = reader.ReadBlock(BasePlayerArray, 4 * 12);
        var players = new List<PlayerState>();
        for (var i = 0; i < 12; i++)
        {
            var pointer = pointers is not null ? MemoryDecode.U32(pointers, 4 * i) : reader.ReadU32(BasePlayerArray + 4 * i);
            if (pointer is not > 0) continue;
            if (ReadPlayer(reader, pointer.Value) is { } player) players.Add(player with { Index = i });
        }
        return players;
    }

    /// <summary>psobb.lisp:378 fast-burst-enabled-p (psostats ephineaFastBurstEnabled).</summary>
    public static bool FastBurstEnabled(IMemoryReader reader)
    {
        var baseAddress = reader.ReadU32(FastBurstBase);
        if (baseAddress is not > 0) return false;
        var slowBurstPtr = reader.ReadU32(baseAddress.Value + FastBurstStep);
        if (slowBurstPtr is not > 0) return false;
        return reader.ReadU16(slowBurstPtr.Value) == 0;
    }

    /// <summary>psobb.lisp:386 read-camera: position + direction in one read; null when unreadable.</summary>
    public static Camera? ReadCamera(IMemoryReader reader)
    {
        var block = reader.ReadBlock(CameraPositionAddress, 24);
        if (block is null) return null;
        float F(int o) => MemoryDecode.U32Float(MemoryDecode.U32(block, o));
        return new Camera(F(0), F(4), F(8), F(12), F(16), F(20), reader.ReadU32(CameraZoomAddress) ?? 0);
    }

    /// <summary>psobb.lisp:400 read-episode: raw 0→1, 1→2, 2→4, 3→4, else null.</summary>
    public static int? ReadEpisode(IMemoryReader reader) =>
        reader.ReadU16(EpisodeAddress) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 4,
            _ => null,
        };

    /// <summary>psobb.lisp:407 reader-quest-loaded-p: one read of the quest pointer.</summary>
    public static bool QuestLoaded(IMemoryReader reader) => reader.ReadU32(QuestPointer) is > 0;

    /// <summary>psobb.lisp:184 read-anguish-level: only while the Ephinea HP table pointer is live.</summary>
    public static int? ReadAnguishLevel(IMemoryReader reader) =>
        reader.ReadU32(EphineaMonsterHpTable) is > 0 ? PsobbTables.AnguishLevel(reader.ReadF64(EphineaHpScale)) : null;

    /// <summary>
    /// psobb.lisp:414 read-snapshot: one frame of game state; null when the
    /// process is unreadable (my player index is the gate). Quest fields are
    /// read only while a quest is loaded.
    /// </summary>
    public static Snapshot? ReadSnapshot(IMemoryReader reader)
    {
        var myIndex = reader.ReadU8(MyPlayerIndex);
        if (myIndex is null) return null;
        var episode = ReadEpisode(reader);
        var players = ReadPlayers(reader);
        var questPtr = reader.ReadU32(QuestPointer);
        var snapshot = new Snapshot
        {
            Episode = episode,
            MyIndex = myIndex,
            Players = players,
            Difficulty = reader.ReadU16(DifficultyAddress),
            Map = reader.ReadU16(CurrentMapAddress),
            MapVariation = reader.ReadU16(MapVariationAddress),
            FastBurst = FastBurstEnabled(reader),
            QuestPtr = questPtr ?? 0,
        };
        if (questPtr is > 0)
        {
            var ptr = questPtr.Value;
            var dataPtr = reader.ReadU32(ptr + QuestDataOffset);
            var registerPtr = reader.ReadU32(ptr + QuestRegisterOffset);
            var questName = dataPtr is > 0 ? reader.ReadUtf16String(dataPtr.Value + QuestNameOffset, 64) : null;
            var questNumber = dataPtr is > 0 ? reader.ReadU16(dataPtr.Value + QuestNumberOffset) : null;
            snapshot = snapshot with
            {
                QuestName = questName?.Trim(' '),
                QuestNumber = questNumber,
                Anguish = ReadAnguishLevel(reader),
                Registers = registerPtr is > 0 ? reader.ReadBlock(registerPtr.Value, 4 * RegisterCount) : null,
                FloorSwitches = reader.ReadBlock(FloorSwitches, 32 * FloorCount),
            };
        }
        return snapshot;
    }

    // ---------------------------------------------------------------- inventory

    private static string? ReadItemName(IMemoryReader reader, long unitxtIndex)
    {
        if (ItemNameCache.TryGetValue(unitxtIndex, out var cached)) return cached;
        var unitxt = reader.ReadU32(UnitxtPointer);
        var names = unitxt is > 0 ? reader.ReadU32(unitxt.Value + 4) : null;
        var nameAddress = names is > 0 ? reader.ReadU32(names.Value + 4 * unitxtIndex) : null;
        var name = nameAddress is > 0 ? reader.ReadUtf16String(nameAddress.Value, 48) : null;
        if (string.IsNullOrEmpty(name)) return null;
        ItemNameCache[unitxtIndex] = name;
        return name;
    }

    /// <summary>psobb.lisp:493 item-pmt-index: the unitxt name index of an item via ItemPMT.</summary>
    private static long? ItemPmtIndex(IMemoryReader reader, long group, long index, int typeOffset, int stride)
    {
        var pmt = reader.ReadU32(PmtPointer);
        var table = pmt is > 0 ? reader.ReadU32(pmt.Value + typeOffset) : null;
        if (table is not > 0) return null;
        var entry = reader.ReadU32(table.Value + 8 * group + 4);
        if (entry is not > 0) return null;
        return reader.ReadU32(entry.Value + stride * index);
    }

    private static readonly string[] WeaponSpecials =
    [
        "", "Draw", "Drain", "Fill", "Gush", "Heart", "Mind", "Soul", "Geist",
        "Master's", "Lord's", "King's", "Charge", "Spirit", "Berserk",
        "Ice", "Frost", "Freeze", "Blizzard", "Bind", "Hold", "Seize", "Arrest",
        "Heat", "Fire", "Flame", "Burning", "Shock", "Thunder", "Storm", "Tempest",
        "Dim", "Shadow", "Dark", "Hell", "Panic", "Riot", "Havoc", "Chaos",
        "Devil's", "Demon's",
    ];

    private static readonly string[] SrankSpecials =
    [
        "", "Jellen", "Zalure", "HP Regeneration", "TP Regeneration", "Burning",
        "Tempest", "Blizzard", "Arrest", "Chaos", "Hell", "Spirit", "Berserk",
        "Demon's", "Gush", "Geist", "King's",
    ];

    private static string SpecialName(string[] table, int? id) =>
        id is { } i && i >= 0 && i < table.Length ? table[i] : "?";

    private static string Name(IMemoryReader reader, long? pmtIndex) =>
        (pmtIndex is { } p ? ReadItemName(reader, p) : null) ?? "?";

    /// <summary>psobb.lisp:523 weapon-display: "name[ +grind][ [special]] [n/a/m/d|h]".</summary>
    private static string WeaponDisplay(IMemoryReader reader, long itemAddr, int group, int index)
    {
        var name = Name(reader, ItemPmtIndex(reader, group, index, 0x00, 44));
        var grind = reader.ReadU8(itemAddr + ItemWepGrindOffset);
        var srank = group is >= 0x70 and <= 0x88 || group is >= 0xA5 and <= 0xA9;
        int native = 0, abeast = 0, machine = 0, dark = 0, hit = 0;
        for (var slot = 0; slot < 3; slot++)
        {
            var area = reader.ReadU8(itemAddr + ItemWepStatsOffset + 2 * slot);
            var percent = reader.ReadU8(itemAddr + ItemWepStatsOffset + 2 * slot + 1);
            if (area is null || percent is null) continue;
            var value = percent > 127 ? percent.Value - 256 : percent.Value;
            switch (area)
            {
                case 1: native = value; break;
                case 2: abeast = value; break;
                case 3: machine = value; break;
                case 4: dark = value; break;
                case 5: hit = value; break;
            }
        }
        var special = srank
            ? SpecialName(SrankSpecials, index)
            : SpecialName(WeaponSpecials, reader.ReadU8(itemAddr + ItemWepSpecialOffset));
        var text = name;
        if (grind is > 0) text += " +" + grind.Value.ToString(CultureInfo.InvariantCulture);
        if (special != "") text += " [" + special + "]";
        return text + FormattableString.Invariant($" [{native}/{abeast}/{machine}/{dark}|{hit}]");
    }

    /// <summary>psobb.lisp:554 armor-display for frame / barrier / unit.</summary>
    private static string ArmorDisplay(IMemoryReader reader, long itemAddr, int group, int index, string kind)
    {
        var pmtIndex = kind == "unit"
            ? ItemPmtIndex(reader, 0, index, 0x08, 20)
            : ItemPmtIndex(reader, group - 1, index, 0x04, 32);
        var name = Name(reader, pmtIndex);
        int B(int offset) => reader.ReadU8(itemAddr + offset) ?? 0;
        return kind switch
        {
            "frame" => FormattableString.Invariant($"{name} [{B(ItemFrameDfpOffset)}|{B(ItemFrameEvpOffset)}] [{B(ItemArmSlotsOffset)}s]"),
            "barrier" => FormattableString.Invariant($"{name} [{B(ItemBarrierDfpOffset)}|{B(ItemBarrierEvpOffset)}]"),
            _ => name,
        };
    }

    /// <summary>psobb.lisp:571 mag-display: "name [def/pow/dex/mind]" with stats floored /100.</summary>
    private static string MagDisplay(IMemoryReader reader, long itemAddr, int group)
    {
        var name = Name(reader, ItemPmtIndex(reader, 0, group, 0x10, 28));
        long Stat(int slot) => LispMath.FloorDiv(reader.ReadU16(itemAddr + ItemMagStatsOffset + 2 * slot) ?? 0, 100);
        return FormattableString.Invariant($"{name} [{Stat(0)}/{Stat(1)}/{Stat(2)}/{Stat(3)}]");
    }

    /// <summary>psobb.lisp:582 read-equipped-item; id = "%04X%04X" of (word1, word0).</summary>
    public static Equipment? ReadEquippedItem(IMemoryReader reader, long itemAddr)
    {
        var word0 = reader.ReadU16(itemAddr + ItemIdOffset);
        var word1 = reader.ReadU16(itemAddr + ItemIdOffset + 2);
        var type = reader.ReadU8(itemAddr + ItemTypeOffset);
        var group = reader.ReadU8(itemAddr + ItemGroupOffset);
        var index = reader.ReadU8(itemAddr + ItemIndexOffset);
        if (word0 is null || word1 is null || type is null || group is null || index is null) return null;
        var id = word1.Value.ToString("X4", CultureInfo.InvariantCulture) + word0.Value.ToString("X4", CultureInfo.InvariantCulture);
        return type switch
        {
            0 => new Equipment(id, "weapon", WeaponDisplay(reader, itemAddr, group.Value, index.Value)),
            1 => group switch
            {
                1 => new Equipment(id, "frame", ArmorDisplay(reader, itemAddr, group.Value, index.Value, "frame")),
                2 => new Equipment(id, "barrier", ArmorDisplay(reader, itemAddr, group.Value, index.Value, "barrier")),
                3 => new Equipment(id, "unit", ArmorDisplay(reader, itemAddr, group.Value, index.Value, "unit")),
                _ => null,
            },
            2 => new Equipment(id, "mag", MagDisplay(reader, itemAddr, group.Value)),
            _ => null,
        };
    }

    /// <summary>psobb.lisp:604 +consumable-keys+ (psostats addConsumableToInventory).</summary>
    public static string? ConsumableKey(int group, int index) => (group, index) switch
    {
        (0, 0) => "monomate",
        (0, 1) => "dimate",
        (0, 2) => "trimate",
        (1, 0) => "monofluid",
        (1, 1) => "difluid",
        (1, 2) => "trifluid",
        (3, _) => "sol-atomizer",
        (4, _) => "moon-atomizer",
        (5, _) => "star-atomizer",
        (7, _) => "telepipe",
        _ => null,
    };

    /// <summary>
    /// psobb.lisp:617 read-inventory: equipped gear and consumable counts of
    /// the local player. The item array covers the whole game world, so the
    /// count bound (u16, 0..4096) only rejects garbage; owner filters to us.
    /// </summary>
    public static Inventory? ReadInventory(IMemoryReader reader, int myIndex)
    {
        var count = reader.ReadU16(ItemArrayCount);
        var array = reader.ReadU32(ItemArrayPointer);
        if (count is null || array is not > 0 || count > 4096) return null;
        var pointers = reader.ReadBlock(array.Value, 4 * count.Value);
        var equipment = new List<Equipment>();
        Equipment? weapon = null;
        var consumables = new List<(string Key, int Count)>();
        if (pointers is not null)
        {
            for (var i = 0; i < count; i++)
            {
                long itemAddr = MemoryDecode.U32(pointers, 4 * i);
                if (itemAddr <= 0) continue;
                var fields = reader.ReadBlock(itemAddr + ItemOwnerOffset, ItemEquippedOffset + 1 - ItemOwnerOffset);
                if (fields is null) continue;
                int U8(int offset) => fields[offset - ItemOwnerOffset];
                var type = U8(ItemTypeOffset);
                var group = U8(ItemGroupOffset);
                var index = U8(ItemIndexOffset);
                var owner = U8(ItemOwnerOffset);
                var equipped = U8(ItemEquippedOffset);
                if (owner == myIndex && (equipped & 1) == 1)
                {
                    if (ReadEquippedItem(reader, itemAddr) is { } item)
                    {
                        equipment.Add(item);
                        if (item.Type == "weapon") weapon = item;
                    }
                }
                else if (type == 3 && owner == myIndex)
                {
                    if (ConsumableKey(group, index) is { } key)
                    {
                        // XOR-obfuscated with the low byte of its own address.
                        var value = U8(ItemToolCountOffset) ^ (int)((itemAddr + ItemToolCountOffset) & 0xFF);
                        // (setf getf): an existing key keeps its place, a new one goes first.
                        var at = consumables.FindIndex(c => c.Key == key);
                        if (at >= 0) consumables[at] = (key, value);
                        else consumables.Insert(0, (key, value));
                    }
                }
            }
        }
        return new Inventory { Equipment = equipment, Weapon = weapon, Consumables = consumables };
    }

    // ---------------------------------------------------------------- monsters

    private static string? ReadMonsterName(IMemoryReader reader, long unitxtId)
    {
        if (MonsterNameCache.TryGetValue(unitxtId, out var cached)) return cached;
        var unitxt = reader.ReadU32(UnitxtPointer);
        // The monster name table is the third table (offset 16).
        var names = unitxt is > 0 ? reader.ReadU32(unitxt.Value + 16) : null;
        var nameAddress = names is > 0 ? reader.ReadU32(names.Value + 4 * unitxtId) : null;
        var name = nameAddress is > 0 ? reader.ReadUtf16String(nameAddress.Value, 32) : null;
        if (string.IsNullOrEmpty(name)) return null;
        MonsterNameCache[unitxtId] = name;
        return name;
    }

    /// <summary>psobb.lisp:700 boss-hp-offset: De Rol Le / Barba Ray keep HP elsewhere.</summary>
    private static int? BossHpOffset(long unitxt, int index) => unitxt switch
    {
        45 => index == 0 ? MonsterDeRolLeHp : MonsterDeRolLeShellHp,
        73 => index == 0 ? MonsterBarbaRayHp : MonsterBarbaRayShellHp,
        _ => null,
    };

    /// <summary>
    /// psobb.lisp:714 monster-hp-block: one read of the HP-table entries for all
    /// <paramref name="ids"/>, or null when the table is absent, the id span
    /// exceeds <see cref="HpBlockMaxIds"/> or the memory is unreadable.
    /// </summary>
    public static (byte[] Bytes, int LowId)? MonsterHpBlock(IMemoryReader reader, long? hpTable, IReadOnlyCollection<int> ids)
    {
        if (hpTable is not > 0 || ids.Count == 0) return null;
        var low = ids.Min();
        var span = ids.Max() - low + 1;
        if (span > HpBlockMaxIds) return null;
        var bytes = reader.ReadBlock(hpTable.Value + 32L * low, 32 * span);
        return bytes is null ? null : (bytes, low);
    }

    /// <summary>psobb.lisp:729 read-monster: decode one pre-read entity block; null when not a live monster.</summary>
    private static MonsterState? ReadMonster(IMemoryReader reader, long monsterAddr, int index, long? hpTable, byte[] block,
        (byte[] Bytes, int LowId)? hpBlock)
    {
        int U16(int offset) => MemoryDecode.U16(block, offset - MonsterBlockStart);
        uint U32(int offset) => MemoryDecode.U32(block, offset - MonsterBlockStart);
        float F32(int offset) => MemoryDecode.U32Float(U32(offset));
        long unitxt = U32(MonsterUnitxtOffset);
        var id = U16(MonsterIdOffset);
        if (unitxt <= 0) return null;
        int? hp;
        long? hpOffset = hpBlock is { } hb ? 32L * (id - hb.LowId) + 4 : null;
        if (BossHpOffset(unitxt, index) is { } bossOffset)
            hp = reader.ReadU16(monsterAddr + bossOffset);
        else if (hpBlock is { } b && hpOffset is { } o && o >= 0 && o + 2 <= b.Bytes.Length)
            hp = MemoryDecode.U16(b.Bytes, (int)o);
        else if (hpTable is > 0)
            hp = reader.ReadU16(hpTable.Value + 4 + 32L * id);
        else
            hp = U16(MonsterHpOffset);
        var status = U16(MonsterStatusOffset);
        var y = F32(MonsterYOffset);
        // HP underflows below zero on kill.
        if (hp > 0x8000) hp = 0;
        // Zu and Pazuzu keep their height in a separate field.
        if (unitxt is 94 or 95 && reader.ReadF32(monsterAddr + MonsterZuYOffset) is { } extra) y += extra;
        return new MonsterState
        {
            Id = id,
            Unitxt = unitxt,
            Index = index,
            Name = ReadMonsterName(reader, unitxt),
            Hp = hp ?? 0,
            LastAttacker = U16(MonsterAttackerOffset),
            X = F32(MonsterXOffset),
            Y = y,
            Z = F32(MonsterZOffset),
            Facing = U16(MonsterFacingOffset),
            Frozen = status == 0x02,
            Confused = status == 0x12,
            Paralyzed = U16(MonsterParalyzedOffset) == 0x10,
        };
    }

    /// <summary>
    /// psobb.lisp:780 read-monsters: entity blocks first, then all live HP in
    /// one batched table read; null when the entity array is unreadable.
    /// </summary>
    public static List<MonsterState>? ReadMonsters(IMemoryReader reader)
    {
        var array = reader.ReadU32(NpcArrayPointer);
        var npcCount = reader.ReadU32(NpcCountAddress);
        var playerCount = reader.ReadU32(PlayerCountAddress);
        var hpTable = reader.ReadU32(EphineaMonsterHpTable);
        if (array is not > 0 || npcCount is not { } npc || playerCount is not { } pc || npc > 512 || pc > 12) return null;
        var pointers = reader.ReadBlock(array.Value + 4L * pc, 4 * (int)npc);
        if (pointers is null) return null;
        var entities = new List<(long Addr, int Index, byte[] Block)>();
        for (var i = 0; i < npc; i++)
        {
            long monsterAddr = MemoryDecode.U32(pointers, 4 * i);
            if (monsterAddr <= 0) continue;
            var block = reader.ReadBlock(monsterAddr + MonsterBlockStart, MonsterBlockEnd - MonsterBlockStart);
            if (block is not null) entities.Add((monsterAddr, (int)pc + i, block));
        }
        var liveIds = entities
            .Where(e => MemoryDecode.U32(e.Block, MonsterUnitxtOffset - MonsterBlockStart) > 0)
            .Select(e => MemoryDecode.U16(e.Block, MonsterIdOffset - MonsterBlockStart))
            .ToList();
        var hpBlock = MonsterHpBlock(reader, hpTable, liveIds);
        var monsters = new List<MonsterState>();
        foreach (var (addr, index, block) in entities)
        {
            if (ReadMonster(reader, addr, index, hpTable, block, hpBlock) is { } monster) monsters.Add(monster);
        }
        return monsters;
    }
}
