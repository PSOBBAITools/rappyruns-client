using RappyRuns.Core.Game;

namespace RappyRuns.Tests.Game;

/// <summary>
/// The Lisp test harness's mock memory builders (client/tests/client-tests.lisp:27-141),
/// ported one to one so the suites translate mechanically.
/// </summary>
internal static class MockMemory
{
    public const long Player0Base = 0x00500000;
    public const long QuestBase = 0x00700000;
    public const long QuestDataBase = 0x00710000;
    public const long RegisterBase = 0x00720000;

    public static void PutU16(byte[] bytes, int offset, long value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void PutU32(byte[] bytes, int offset, long value)
    {
        PutU16(bytes, offset, value & 0xFFFF);
        PutU16(bytes, offset + 2, (value >> 16) & 0xFFFF);
    }

    public static void PutUtf16(byte[] bytes, int offset, string s)
    {
        for (var i = 0; i < s.Length; i++) PutU16(bytes, offset + 2 * i, s[i]);
    }

    /// <summary>put-f32; exact IEEE bits (the Lisp encoder is exact for every value the suites use).</summary>
    public static void PutF32(byte[] bytes, int offset, float value) =>
        PutU32(bytes, offset, (uint)BitConverter.SingleToInt32Bits(value));

    public static void PutF64(byte[] bytes, int offset, double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        for (var i = 0; i < 8; i++) bytes[offset + i] = (byte)((bits >> (8 * i)) & 0xFF);
    }

    public static byte[] Zeros(int n) => new byte[n];

    /// <summary>client-tests.lisp:64 make-player-block.</summary>
    public static byte[] PlayerBlock(string name, int classId = 0, int floor = 0, bool warping = false, float pb = 0f,
        int sectionId = 0, int levelRaw = 0, int room = 0, int state = 1, int hp = 0, int maxHp = 0, int tp = 0,
        int maxTp = 0, long meseta = 0, string? guildCard = null, long nameColor = 0xFFFFFFFF)
    {
        var bytes = new byte[0xE60];
        PutUtf16(bytes, 0x428, "\tE" + name);
        PutU16(bytes, 0x960, (classId << 8) | sectionId);
        PutU16(bytes, 0x3F0, floor);
        PutU16(bytes, 0x33E, warping ? 0x04 : 0);
        PutF32(bytes, 0x520, pb);
        PutU16(bytes, 0x028, room);
        PutU16(bytes, 0x348, state);
        PutU16(bytes, 0x2BC, maxHp);
        PutU16(bytes, 0x2BE, maxTp);
        PutU16(bytes, 0x334, hp);
        PutU16(bytes, 0x336, tp);
        PutU16(bytes, 0xE44, levelRaw);
        PutU32(bytes, 0xE4C, meseta);
        PutU32(bytes, 0x948, nameColor);
        if (guildCard is not null)
        {
            for (var i = 0; i < guildCard.Length; i++) bytes[0x930 + i] = (byte)guildCard[i];
        }
        return bytes;
    }

    /// <summary>client-tests.lisp:90 make-game-regions: a full mock memory image.</summary>
    public static MockReader GameRegions(int episodeRaw = 0, IReadOnlyList<byte[]>? players = null, string? questName = null,
        int? questNumber = null, IEnumerable<(int Id, int Value)>? registerValues = null, int difficulty = 0, int map = 0,
        double? hpScale = null)
    {
        var globals = Zeros(4);
        var episode = Zeros(2);
        var playerArray = Zeros(48);
        var questPtr = Zeros(4);
        var regions = new List<(long, byte[])>();
        PutU16(episode, 0, episodeRaw);
        var i = 0;
        foreach (var player in players ?? [])
        {
            var baseAddress = 0x00500000 + i * 0x10000;
            PutU32(playerArray, 4 * i, baseAddress);
            regions.Insert(0, (baseAddress, player));
            i++;
        }
        if (questName is not null)
        {
            var quest = Zeros(0x200);
            var data = Zeros(128);
            var registers = Zeros(1024);
            PutU32(questPtr, 0, QuestBase);
            PutU32(quest, 0x19C, QuestDataBase);
            PutU32(quest, 0x2C, RegisterBase);
            PutU16(data, 0x10, questNumber ?? 0);
            PutUtf16(data, 0x18, questName);
            foreach (var (id, value) in registerValues ?? []) PutU16(registers, 4 * id, value);
            regions.Insert(0, (QuestBase, quest));
            regions.Insert(0, (QuestDataBase, data));
            regions.Insert(0, (RegisterBase, registers));
        }
        var difficultyBytes = Zeros(2);
        var mapBytes = Zeros(2);
        PutU16(difficultyBytes, 0, difficulty);
        PutU16(mapBytes, 0, map);
        regions.Insert(0, (0x00A9CD68, difficultyBytes));
        regions.Insert(0, (0x00AAFC9C, mapBytes));
        if (hpScale is { } scale)
        {
            var ephinea = Zeros(12);
            PutU32(ephinea, 0, 0x00CAFE00);
            PutF64(ephinea, 4, scale);
            regions.Insert(0, (0x00B5F800, ephinea));
        }
        regions.Insert(0, (0x00A9C4F4, globals));
        regions.Insert(0, (0x00A9B1C8, episode));
        regions.Insert(0, (0x00A94254, playerArray));
        regions.Insert(0, (0x00A95AA8, questPtr));
        regions.Insert(0, (0x00AC9FA0, Zeros(32 * 18)));
        return new MockReader(regions);
    }

    /// <summary>A 32*18 floor-switch block with the given (floor, switch) bits on (tests-quests fsw-array).</summary>
    public static byte[] FloorSwitchArray(params (int Floor, int Switch)[] on)
    {
        var a = Zeros(32 * 18);
        foreach (var (f, s) in on) a[32 * f + s / 8] |= (byte)(0x80 >> (s % 8));
        return a;
    }
}
