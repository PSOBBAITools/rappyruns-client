namespace RappyRuns.Core.Game;

/// <summary>
/// One frame of game state (psobb.lisp:414 read-snapshot plus the poll loop's
/// augment-snapshot, main.lisp:69). The Lisp snapshot is a plist and the
/// detector / telemetry read it with getf defaults, so every field here is
/// nullable: null is "key absent or NIL", and the consumers apply the same
/// defaults the Lisp getf calls do. Tests build snapshots directly, the way
/// tests-memory's tele-snapshot and tests-detect's mon-snapshot build plists.
/// </summary>
public sealed record Snapshot
{
    public int? Episode { get; init; }

    public int? MyIndex { get; init; }

    public IReadOnlyList<PlayerState> Players { get; init; } = [];

    /// <summary>Raw difficulty u16 (0=Normal..3=Ultimate).</summary>
    public int? Difficulty { get; init; }

    /// <summary>Real loaded map number (area names key off it).</summary>
    public int? Map { get; init; }

    public int? MapVariation { get; init; }

    public bool FastBurst { get; init; }

    /// <summary>Quest struct pointer; 0 = no quest loaded. read-snapshot writes 0 when unreadable; hand-built snapshots may omit it (null).</summary>
    public long? QuestPtr { get; init; }

    public string? QuestName { get; init; }

    public int? QuestNumber { get; init; }

    public int? Anguish { get; init; }

    /// <summary>1024 bytes: 256 registers, stride 4, u16 each.</summary>
    public byte[]? Registers { get; init; }

    /// <summary>32 bytes per floor, 18 floors.</summary>
    public byte[]? FloorSwitches { get; init; }

    /// <summary>Monsters, read every frame while a quest is loaded (augment-snapshot).</summary>
    public IReadOnlyList<MonsterState>? Monsters { get; init; }

    /// <summary>Game camera, only when the ghost marker needs it.</summary>
    public Camera? Camera { get; init; }

    /// <summary>Inventory, about once per second.</summary>
    public Inventory? Inventory { get; init; }

    /// <summary>Loaded quest per the Lisp test (and (getf :quest-ptr) (plusp ...)).</summary>
    public bool QuestLoaded => QuestPtr is > 0;

    /// <summary>psobb.lisp:469 snapshot-my-player: the player whose :index is :my-index.</summary>
    public PlayerState? MyPlayer => MyIndex is { } my ? Players.FirstOrDefault(p => p.Index == my) : Players.FirstOrDefault(p => p.Index is null);

    /// <summary>psobb.lisp:453 snapshot-register-set-p (psostats IsRegisterSet).</summary>
    public bool RegisterSet(int registerId) =>
        Registers is { } r && 4 * registerId + 1 < r.Length && registerId >= 0 && MemoryDecode.U16(r, 4 * registerId) > 0;

    /// <summary>psobb.lisp:460 snapshot-floor-switch-set-p; the MSB of each byte is switch 0.</summary>
    public bool FloorSwitchSet(int floor, int switchId)
    {
        if (FloorSwitches is not { } s) return false;
        var offset = 32 * floor + LispMath.FloorDiv(switchId, 8);
        if (offset >= s.Length || offset < 0) return false;
        var bit = (int)LispMath.Mod(switchId, 8);
        return (s[offset] & (0x80 >> bit)) != 0;
    }
}

/// <summary>One player slot (psobb.lisp:313 read-player). Nullable like the plist it mirrors.</summary>
public sealed record PlayerState
{
    /// <summary>Party slot 0..11.</summary>
    public int? Index { get; init; }

    public string? Name { get; init; }

    public string? Class { get; init; }

    public string? SectionId { get; init; }

    public int? Level { get; init; }

    public string? GuildCard { get; init; }

    /// <summary>Quest NPC (guild card present and non-numeric, psobb.lisp:306).</summary>
    public bool Npc { get; init; }

    /// <summary>ARGB name colour (sandbox detection, spec core §13).</summary>
    public long? NameColor { get; init; }

    /// <summary>Quest floor slot.</summary>
    public int? Floor { get; init; }

    public int? Room { get; init; }

    public float? X { get; init; }

    public float? Y { get; init; }

    public float? Z { get; init; }

    public int? Facing { get; init; }

    public bool Warping { get; init; }

    /// <summary>Action state (5,6,7 attack, 8 casting).</summary>
    public int? State { get; init; }

    public int? CurrentTech { get; init; }

    public int? Hp { get; init; }

    public int? MaxHp { get; init; }

    public int? Tp { get; init; }

    public int? MaxTp { get; init; }

    public long? Meseta { get; init; }

    /// <summary>Photon blast gauge.</summary>
    public float? Pb { get; init; }

    /// <summary>Shifta level (already converted from the multiplier).</summary>
    public int? Shifta { get; init; }

    public int? Deband { get; init; }

    public bool Invincible { get; init; }

    public int? DamageTraps { get; init; }

    public int? FreezeTraps { get; init; }

    public int? ConfuseTraps { get; init; }
}

/// <summary>One monster entity (psobb.lisp:729 read-monster).</summary>
public sealed record MonsterState
{
    public int Id { get; init; }

    public long? Unitxt { get; init; }

    /// <summary>Absolute entity slot, players included (psostats Monster.Index).</summary>
    public int? Index { get; init; }

    public string? Name { get; init; }

    public int? Hp { get; init; }

    public int? LastAttacker { get; init; }

    public float? X { get; init; }

    public float? Y { get; init; }

    public float? Z { get; init; }

    public int? Facing { get; init; }

    public bool Frozen { get; init; }

    public bool Confused { get; init; }

    public bool Paralyzed { get; init; }
}

/// <summary>Game camera (psobb.lisp:386 read-camera) for the overlay's in-world ghost marker.</summary>
public sealed record Camera(float X, float Y, float Z, float DirX, float DirY, float DirZ, long Zoom);

/// <summary>An equipped item (psobb.lisp:582 read-equipped-item). Type is weapon/frame/barrier/unit/mag.</summary>
public sealed record Equipment(string Id, string Type, string Display);

/// <summary>
/// The local player's inventory (psobb.lisp:617 read-inventory). Consumables
/// keep the Lisp plist order: (setf getf) pushes a new key to the front.
/// </summary>
public sealed record Inventory
{
    public IReadOnlyList<Equipment> Equipment { get; init; } = [];

    public Equipment? Weapon { get; init; }

    /// <summary>(key, count) with keys like "monomate", "moon-atomizer" (Lisp keyword names, lower case).</summary>
    public IReadOnlyList<(string Key, int Count)> Consumables { get; init; } = [];
}
