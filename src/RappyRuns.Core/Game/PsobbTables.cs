namespace RappyRuns.Core.Game;

/// <summary>
/// Pure PSOBB tables and rules (psobb.lisp): class/section/difficulty/tech
/// names, Shifta levels and ceilings, Anguish, account mode by name colour,
/// NPC detection, boss names and the Authenticode signer policy.
/// </summary>
public static class PsobbTables
{
    private static readonly Dictionary<int, string> ClassIds = new()
    {
        [0x00] = "HUmar", [0x01] = "HUnewearl", [0x02] = "HUcast", [0x09] = "HUcaseal",
        [0x03] = "RAmar", [0x0B] = "RAmarl", [0x04] = "RAcast", [0x05] = "RAcaseal",
        [0x0A] = "FOmar", [0x06] = "FOmarl", [0x07] = "FOnewm", [0x08] = "FOnewearl",
    };

    private static readonly Dictionary<string, int> ClassMaxShifta = new(StringComparer.Ordinal)
    {
        ["HUmar"] = 3, ["HUnewearl"] = 20, ["HUcast"] = 3, ["HUcaseal"] = 3,
        ["RAmar"] = 15, ["RAmarl"] = 20, ["RAcast"] = 0, ["RAcaseal"] = 0,
        ["FOmar"] = 30, ["FOmarl"] = 30, ["FOnewm"] = 30, ["FOnewearl"] = 30,
    };

    private static readonly string[] SectionIds =
        ["Viridia", "Greenill", "Skyly", "Bluefull", "Purplenum", "Pinkal", "Redria", "Oran", "Yellowboze", "Whitill"];

    private static readonly string[] DifficultyNames = ["Normal", "Hard", "Very Hard", "Ultimate"];

    private static readonly Dictionary<int, string> TechNames = new()
    {
        [0x00] = "Foie", [0x01] = "Gifoie", [0x02] = "Rafoie", [0x03] = "Barta", [0x04] = "Gibarta",
        [0x05] = "Rabarta", [0x06] = "Zonde", [0x07] = "Gizonde", [0x08] = "Razonde", [0x09] = "Grants",
        [0x12] = "Megid", [0x0A] = "Deband", [0x0B] = "Jellen", [0x0C] = "Zalure", [0x0D] = "Shifta",
        [0x0E] = "Ryuker", [0x0F] = "Resta", [0x10] = "Anti", [0x11] = "Reverser",
    };

    /// <summary>psobb.lisp:245 +sandbox-name-color+ (measured 2026-09-22).</summary>
    public const long SandboxNameColor = 0xFFAB9423;

    /// <summary>psobb.lisp:249 +normal-name-color+: plain white.</summary>
    public const long NormalNameColor = 0xFFFFFFFF;

    /// <summary>psobb.lisp:831 +trusted-psobb-signers+: accepted signing-certificate subject CNs.</summary>
    public static IReadOnlyList<string> TrustedPsobbSigners { get; } = ["Terry Chatman"];

    public static string? ClassNameForId(int id) => ClassIds.GetValueOrDefault(id);

    public static int MaxSupplyableShifta(string? className) =>
        className is not null && ClassMaxShifta.TryGetValue(className, out var v) ? v : 0;

    /// <summary>
    /// psobb.lisp:145 max-party-pb-shifta: the party PB ceiling (21 + 20 per
    /// extra member, psostats StartNewQuest) or the best caster's own, if higher.
    /// </summary>
    public static int MaxPartyPbShifta(IEnumerable<string?> partyClasses)
    {
        var classes = partyClasses.ToList();
        var size = Math.Max(1, classes.Count);
        var best = 21 + 20 * (size - 1);
        foreach (var c in classes) best = Math.Max(best, MaxSupplyableShifta(c));
        return best;
    }

    public static string? SectionNameForId(int? id) =>
        id is { } i && i >= 0 && i < SectionIds.Length ? SectionIds[i] : null;

    public static string? DifficultyName(int? id) =>
        id is { } i && i >= 0 && i < DifficultyNames.Length ? DifficultyNames[i] : null;

    /// <summary>
    /// psobb.lisp:172 anguish-level: the level (1-3) whose HP boost is nearest
    /// the Ephinea HP scale, or null for an absent/unscaled (&lt;= 1.15) scale.
    /// </summary>
    public static int? AnguishLevel(double? hpScale)
    {
        // 1.15 is a single-float literal in the Lisp; CL compares it exactly.
        if (hpScale is not { } s || !(s > (double)1.15f)) return null;
        // reduce keeps A unless B is strictly nearer.
        (int Level, double Scale)[] levels = [(1, 1.30), (2, 1.82), (3, 2.50)];
        var best = levels[0];
        for (var i = 1; i < levels.Length; i++)
        {
            if (!(Math.Abs(best.Scale - s) < Math.Abs(levels[i].Scale - s))) best = levels[i];
        }
        return best.Level;
    }

    /// <summary>psobb.lisp:191 difficulty-label: "Anguish N" replaces "Ultimate" in Anguish games.</summary>
    public static string? DifficultyLabel(int? id, int? anguish)
    {
        var name = DifficultyName(id);
        return anguish is { } a && name == "Ultimate" ? $"Anguish {a}" : name;
    }

    public static string? TechName(int? id) => id is { } i ? TechNames.GetValueOrDefault(i) : null;

    /// <summary>
    /// psobb.lisp:211 shifta-level (psostats getSDLvlFromMultiplier), computed in
    /// single precision exactly as the Lisp does: 1 + floor((|m|*100 - 10)/1.3 + 0.5),
    /// negated for a negative multiplier (Jellen/Zalure). A multiplier big enough to
    /// overflow is a floating-point error in Lisp - read-player then fails - so it throws.
    /// </summary>
    public static int ShiftaLevel(float? multiplier)
    {
        if (multiplier is not { } m || m == 0f) return 0;
        var a = MathF.Abs(m) * 100f;
        if (!float.IsFinite(a)) throw new OverflowException("shifta multiplier overflow");
        var b = a - 10f;
        var c = b / 1.3f;
        var d = c + 0.5f;
        var level = 1 + checked((long)MathF.Floor(d));
        return checked((int)(m < 0 ? -level : level));
    }

    /// <summary>psobb.lisp:252: NIL and 0 mean the game has not filled the colour in yet.</summary>
    public static bool NameColorKnown(long? color) => color is > 0;

    /// <summary>
    /// psobb.lisp:264 account-mode-of-color: "sandbox"/"normal" for the two
    /// measured colours (RGB only, alpha ignored), null for anything else -
    /// null is "no verdict", never normal.
    /// </summary>
    public static string? AccountModeOfColor(long? color)
    {
        if (!NameColorKnown(color)) return null;
        var rgb = color!.Value & 0xFFFFFF;
        if (rgb == (SandboxNameColor & 0xFFFFFF)) return "sandbox";
        if (rgb == (NormalNameColor & 0xFFFFFF)) return "normal";
        return null;
    }

    /// <summary>psobb.lisp:282: drop the "\tE" language marker from a character name.</summary>
    public static string StripNamePrefix(string name) =>
        name.Length >= 2 && name[0] == '\t' ? name[2..] : name;

    /// <summary>
    /// psobb.lisp:306 npc-guild-card-p: only a present card with a non-ASCII-digit
    /// character marks a quest NPC; a missing card stays a person.
    /// </summary>
    public static bool NpcGuildCard(string? guildCard) =>
        guildCard is not null && !guildCard.All(c => c is >= '0' and <= '9');

    /// <summary>psobb.lisp:849 boss-name (psostats isBoss); INDEX is the absolute entity slot.</summary>
    public static string? BossName(long? unitxt, int? index)
    {
        switch (unitxt)
        {
            case 44: return "Sil Dragon";
            case 45: return index == 0 ? "Dal Ra Lie" : null;
            case 46: return index switch { 31 => "Vol Opt ver. 2 (1)", 32 => "Vol Opt ver. 2 (2)", _ => null };
            case 47: return "Dark Falz";
            case 73: return index == 0 ? "Barba Ray" : null;
            case 76: return "Gol Dragon";
            case 77: return "Gal Gryphon";
            case 78: return "Olga Flow";
            case 106 or 107 or 108:
                var name = unitxt switch { 106 => "Saint-Million", 107 => "Shambertin", _ => "Kondrieu" };
                if (index is not { } i) return null;
                if (i < 5) return $"{name} Tail ({i})";
                if (i < 9) return $"{name} Head ({i - 4})";
                return null;
            default: return null;
        }
    }

    /// <summary>
    /// psobb.lisp:837 psobb-signature-trusted-p: an official Ephinea client has a
    /// VALID Authenticode signature whose leaf CN is on the trusted list.
    /// </summary>
    public static bool SignatureTrusted(SignatureStatus status, string? signer) =>
        status == SignatureStatus.Valid && signer is not null && TrustedPsobbSigners.Contains(signer, StringComparer.Ordinal);
}

/// <summary>Authenticode verdict (win32.lisp:520 authenticode-verify).</summary>
public enum SignatureStatus
{
    Valid,
    Unsigned,
    Invalid,
}
