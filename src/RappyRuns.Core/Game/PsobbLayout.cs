namespace RappyRuns.Core.Game;

/// <summary>
/// PSOBB memory layout for the 32-bit Ephinea client (psobb.lisp:8-126,
/// spec core §12), transcribed from psostats-client (MIT). Kept in one place
/// on purpose, like the Lisp file.
/// </summary>
public static class PsobbLayout
{
    // Globals (psobb.lisp:9-16)
    public const long BasePlayerArray = 0x00A94254;   // 12 pointers
    public const long MyPlayerIndex = 0x00A9C4F4;     // u8
    public const long EpisodeAddress = 0x00A9B1C8;    // u16, 0-based; 2 means ep4
    public const long QuestPointer = 0x00A95AA8;      // u32 -> quest struct
    public const long FloorSwitches = 0x00AC9FA0;     // 32 bytes per floor
    public const long DifficultyAddress = 0x00A9CD68; // u16
    public const long CurrentMapAddress = 0x00AAFC9C; // u16
    public const long MapVariationAddress = 0x00AAFC98; // u16

    // Camera (psobb.lisp:22-24)
    public const long CameraPositionAddress = 0x00A48780;
    public const long CameraDirectionAddress = 0x00A4878C;
    public const long CameraZoomAddress = 0x009ACEDC;

    // Fast burst (psobb.lisp:29-30)
    public const long FastBurstBase = 0x5B92DA;
    public const long FastBurstStep = 0x5B92DF;

    // Inventory (psobb.lisp:33-52)
    public const long ItemArrayPointer = 0x00A8D81C;
    public const long ItemArrayCount = 0x00A8D820;    // read as u16 (psobb.lisp:621)
    public const long PmtPointer = 0x00A8DC94;
    public const long UnitxtPointer = 0x00A9CD50;
    public const int ItemIdOffset = 0xD8;
    public const int ItemOwnerOffset = 0xE4;
    public const int ItemTypeOffset = 0xF2;
    public const int ItemGroupOffset = 0xF3;
    public const int ItemIndexOffset = 0xF4;
    public const int ItemToolCountOffset = 0x104;
    public const int ItemEquippedOffset = 0x190;
    public const int ItemMagStatsOffset = 0x1C0;
    public const int ItemWepStatsOffset = 0x1C8;
    public const int ItemArmSlotsOffset = 0x1B8;
    public const int ItemFrameDfpOffset = 0x1B9;
    public const int ItemFrameEvpOffset = 0x1BA;
    public const int ItemBarrierDfpOffset = 0x1E4;
    public const int ItemBarrierEvpOffset = 0x1E5;
    public const int ItemWepGrindOffset = 0x1F5;
    public const int ItemWepSpecialOffset = 0x1F6;

    // Monsters (psobb.lisp:55-84)
    public const long NpcArrayPointer = 0x007B4BA2;
    public const long NpcCountAddress = 0x00AAE164;
    public const long PlayerCountAddress = 0x00AAE168;
    public const long EphineaMonsterHpTable = 0x00B5F800;
    public const long EphineaHpScale = 0x00B5F804;    // f64
    public const int MonsterIdOffset = 0x1C;
    public const int MonsterUnitxtOffset = 0x378;
    public const int MonsterHpOffset = 0x334;
    public const int MonsterBlockStart = 0x1C;
    public const int MonsterBlockEnd = 0x37C;
    public const int MonsterXOffset = 0x38;
    public const int MonsterYOffset = 0x3C;
    public const int MonsterZOffset = 0x40;
    public const int MonsterFacingOffset = 0x60;
    public const int MonsterParalyzedOffset = 0x25C;
    public const int MonsterStatusOffset = 0x268;
    public const int MonsterAttackerOffset = 0x2D8;
    public const int MonsterZuYOffset = 0x418;
    public const int MonsterDeRolLeHp = 0x6B4;
    public const int MonsterDeRolLeShellHp = 0x39C;
    public const int MonsterBarbaRayHp = 0x704;
    public const int MonsterBarbaRayShellHp = 0x7AC;

    // Quest struct / data block (psobb.lisp:87-90)
    public const int QuestDataOffset = 0x19C;
    public const int QuestRegisterOffset = 0x2C;
    public const int QuestNumberOffset = 0x10;
    public const int QuestNameOffset = 0x18;

    // Player struct, read as one block [start, end) (psobb.lisp:95-123)
    public const int PlayerBlockStart = 0x028;
    public const int PlayerBlockEnd = 0xE50;
    public const int PlayerRoomOffset = 0x028;
    public const int PlayerXOffset = 0x038;
    public const int PlayerYOffset = 0x03C;
    public const int PlayerZOffset = 0x040;
    public const int PlayerFacingOffset = 0x060;
    public const int PlayerShiftaOffset = 0x278;
    public const int PlayerDebandOffset = 0x284;
    public const int PlayerMaxHpOffset = 0x2BC;
    public const int PlayerMaxTpOffset = 0x2BE;
    public const int PlayerHpOffset = 0x334;
    public const int PlayerTpOffset = 0x336;
    public const int PlayerStateOffset = 0x33E;
    public const int PlayerActionStateOffset = 0x348;
    public const int PlayerFloorOffset = 0x3F0;
    public const int PlayerNameOffset = 0x428;
    public const int PlayerCurrentTechOffset = 0x464;
    public const int PlayerPbOffset = 0x520;
    public const int PlayerInvincibilityOffset = 0x720;
    public const int PlayerDamageTrapsOffset = 0x89C;
    public const int PlayerFreezeTrapsOffset = 0x89D;
    public const int PlayerConfuseTrapsOffset = 0x89F;
    public const int PlayerGuildCardOffset = 0x930;
    public const int PlayerNameColorOffset = 0x948;
    public const int PlayerClassOffset = 0x960;
    public const int PlayerLevelOffset = 0xE44;
    public const int PlayerMesetaOffset = 0xE4C;

    public const int FloorCount = 18;
    public const int RegisterCount = 256;

    /// <summary>psobb.lisp:709 +hp-block-max-ids+: widest id span one HP-table read covers.</summary>
    public const int HpBlockMaxIds = 2048;
}
