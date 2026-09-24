using System.Text.Json;
using RappyRuns.Core.Game;
using RappyRuns.Core.I18n;

namespace RappyRuns.Tests.Game;

/// <summary>The game parts of tests-helpers.lisp run-pure-helper-tests.</summary>
public class PureHelperTests
{
    [Fact(DisplayName = "u64-double decodes 1.0")]
    public void U64One() => Assert.Equal(1.0, MemoryDecode.U64Double(0x3FF0000000000000));

    [Fact(DisplayName = "u64-double decodes -2.5")]
    public void U64MinusTwoPointFive() => Assert.Equal(-2.5, MemoryDecode.U64Double(0xC004000000000000));

    [Fact(DisplayName = "u64-double decodes the Anguish 1.30 scale")]
    public void U64Anguish() => Assert.True(Math.Abs(1.30 - MemoryDecode.U64Double(0x3FF4CCCCCCCCCCCD)) < 1e-12);

    [Fact(DisplayName = "u64-double clamps infinity/NaN")]
    public void U64Clamp() => Assert.Equal(1.7e308, MemoryDecode.U64Double(0x7FF0000000000000));

    [Fact(DisplayName = "u64-double decodes subnormals")]
    public void U64Subnormal()
    {
        var v = MemoryDecode.U64Double(1);
        Assert.True(v > 0 && v < 1e-300);
    }

    private static Snapshot Players(params PlayerState[] players) => new() { Players = players };

    [Fact(DisplayName = "trigger-met-p warp-in needs a landed player")]
    public void WarpInLanded()
    {
        Assert.True(Detector.TriggerMet(new WarpInTrigger(), Players(new PlayerState { Floor = 1 }), null));
        Assert.False(Detector.TriggerMet(new WarpInTrigger(), Players(new PlayerState { Floor = 1, Warping = true }), null));
        Assert.False(Detector.TriggerMet(new WarpInTrigger(), Players(new PlayerState { Floor = 0 }), null));
    }

    [Fact(DisplayName = "trigger-met-p warp-in ignores a landed quest NPC")]
    public void WarpInIgnoresNpc() => Assert.False(Detector.TriggerMet(new WarpInTrigger(),
        Players(new PlayerState { Floor = 0 }, new PlayerState { Floor = 1, Npc = true }), null));

    [Fact(DisplayName = "npc-guild-card-p: name bytes are an NPC, digits and none are not")]
    public void NpcGuildCard()
    {
        Assert.True(PsobbTables.NpcGuildCard("Mr.X"));
        Assert.True(PsobbTables.NpcGuildCard("Ch@osM@g"));
        Assert.False(PsobbTables.NpcGuildCard("42115973"));
        Assert.False(PsobbTables.NpcGuildCard(null));
    }

    [Fact(DisplayName = "party-of drops quest NPCs")]
    public void PartyDropsNpcs() => Assert.Equal(["teapot"], Detector.PartyOf(Players(
        new PlayerState { Name = "teapot", Class = "RAmar", GuildCard = "42115973" },
        new PlayerState { Name = "Mr.X", Class = "HUcast", GuildCard = "Mr.X", Npc = true })).Select(p => p.Name));

    [Fact(DisplayName = "party-of keeps the local player whatever their card reads as")]
    public void PartyKeepsMe() => Assert.Equal(["Ryu"], Detector.PartyOf(new Snapshot
    {
        MyIndex = 0,
        Players =
        [
            new PlayerState { Index = 0, Name = "Ryu", Class = "HUcast", GuildCard = "x1", Npc = true },
            new PlayerState { Index = 1, Name = "Mr.X", Class = "HUcast", GuildCard = "Mr.X", Npc = true },
        ],
    }).Select(p => p.Name));

    [Fact(DisplayName = "party-of :include-npcs keeps NPCs (Shifta ceiling)")]
    public void PartyIncludeNpcs() => Assert.Equal(2, Detector.PartyOf(Players(
        new PlayerState { Name = "a", Class = "RAmar" },
        new PlayerState { Name = "Rico", Class = "FOnewearl", Npc = true }), includeNpcs: true).Count);

    [Fact(DisplayName = "npc-guild-card-p: only ASCII digits make a person")]
    public void FullWidthDigits() => Assert.True(PsobbTables.NpcGuildCard("４２"));

    [Fact(DisplayName = "trigger-met-p monster-dead consults killed ids")]
    public void MonsterDeadKilledIds()
    {
        Assert.True(Detector.TriggerMet(new MonsterDeadTrigger(42), new Snapshot(), [41, 42]));
        Assert.False(Detector.TriggerMet(new MonsterDeadTrigger(42), new Snapshot(), [41]));
    }

    private static readonly QuestDef[] SlugDefs =
    [
        new("ep1-known", null, [], null, null, null),
        new("ep2-typo", null, [], null, null, null),
    ];

    [Fact(DisplayName = "unknown-slugs reports only the unmatched")]
    public void UnknownSlugs() => Assert.Equal(["ep2-typo"], QuestCatalog.UnknownSlugs(["ep1-known"], SlugDefs));

    [Fact(DisplayName = "unknown-slugs empty when everything matches")]
    public void UnknownSlugsEmpty() => Assert.Empty(QuestCatalog.UnknownSlugs(["ep1-known", "ep2-typo"], SlugDefs));
}

/// <summary>tests-helpers.lisp run-rule-form-tests.</summary>
public class RuleFormTests
{
    private const string Quests = """
        [{"name":"GDV reset","slug":"ep2-gdv-reset","episode":2,"start":{"type":"warp-in"},"end":{"type":"register"},
          "game_number":118,"game_names":["Gal Da Val"]},
         {"name":"Plain","slug":"ep1-plain","episode":1}]
        """;

    private static List<JsonElement> Parents() => RuleForm.TimeableQuests(JsonDocument.Parse(Quests).RootElement);

    private static string? Slug(JsonElement? q) => q?.GetProperty("slug").GetString();

    [Fact(DisplayName = "timeable-quests keeps only trigger-carrying quests")]
    public void Timeable() => Assert.Equal(["ep2-gdv-reset"], Parents().Select(q => Slug(q)));

    [Fact(DisplayName = "detected-parent matches by game number")]
    public void ByNumber() => Assert.Equal("ep2-gdv-reset", Slug(RuleForm.DetectedParent(Parents(), new RunQuest(118, null, 2))));

    [Fact(DisplayName = "detected-parent falls back to episode + name")]
    public void ByName() => Assert.Equal("ep2-gdv-reset", Slug(RuleForm.DetectedParent(Parents(), new RunQuest(0, "Gal Da Val", 2))));

    [Fact(DisplayName = "detected-parent needs the episode to agree")]
    public void EpisodeMustAgree() => Assert.Null(RuleForm.DetectedParent(Parents(), new RunQuest(0, "Gal Da Val", 1)));

    [Fact(DisplayName = "detected-parent nil run-quest")]
    public void NilRunQuest() => Assert.Null(RuleForm.DetectedParent(Parents(), null));

    [Fact(DisplayName = "rule-error-message prefers the message")]
    public void ErrorMessage() => Assert.Equal("boom", RuleForm.RuleErrorMessage(JsonDocument.Parse("{\"message\":\"boom\"}").RootElement));

    [Fact(DisplayName = "rule-error-message joins the errors")]
    public void ErrorJoin() => Assert.Equal("a; b", RuleForm.RuleErrorMessage(JsonDocument.Parse("{\"errors\":[\"a\",\"b\"]}").RootElement));

    [Fact(DisplayName = "rule-error-message shrugs at garbage")]
    public void ErrorGarbage() => Assert.Equal("?", RuleForm.RuleErrorMessage(null));

    [Fact(DisplayName = "rule-trigger-label nil")]
    public void LabelNil() => Assert.Equal("", Trigger.LabelOf(null));

    [Fact(DisplayName = "rule-trigger-label warp-in")]
    public void LabelWarpIn() => Assert.Equal("warp-in", new WarpInTrigger().Label);

    [Fact(DisplayName = "rule-trigger-label register")]
    public void LabelRegister() => Assert.Equal("register:7", new RegisterTrigger(7).Label);

    [Fact(DisplayName = "rule-trigger-label floor-switch")]
    public void LabelFloorSwitch() => Assert.Equal("floor-switch:3:42", new FloorSwitchTrigger(3, 42).Label);

    [Fact(DisplayName = "rule-trigger-label monster")]
    public void LabelMonster() => Assert.Equal("monster:9", new MonsterDeadTrigger(9).Label);

    // rule-manual-marker-p distinguishes markers from trigger lists; in C# the two are
    // different types (RuleManualMarker vs Trigger), so the checks are type facts.
    [Fact(DisplayName = "rule-manual-marker-p marker")]
    public void MarkerIsMarker() => Assert.True(Enum.IsDefined(RuleManualMarker.Monster));

    [Fact(DisplayName = "rule-manual-marker-p trigger list")]
    public void TriggerIsNotMarker() => Assert.IsNotType<RuleManualMarker>(new WarpInTrigger());

    [Fact(DisplayName = "parse-int-in-range trims and parses")]
    public void ParseTrims() => Assert.Equal(42, RuleForm.ParseIntInRange(" 42 ", 0, 255));

    [Fact(DisplayName = "parse-int-in-range rejects out of range")]
    public void ParseOutOfRange() => Assert.Null(RuleForm.ParseIntInRange("256", 0, 255));

    [Fact(DisplayName = "parse-int-in-range rejects junk")]
    public void ParseJunk() => Assert.Null(RuleForm.ParseIntInRange("4x", 0, 255));

    [Fact(DisplayName = "resolve monster")]
    public void ResolveMonster() => Assert.Equal(new MonsterDeadTrigger(300), RuleForm.ResolveManualTrigger(RuleManualMarker.Monster, "300", null));

    [Fact(DisplayName = "resolve floor-switch")]
    public void ResolveFloorSwitch() =>
        Assert.Equal(new FloorSwitchTrigger(5, 128), RuleForm.ResolveManualTrigger(RuleManualMarker.FloorSwitch, "5", "128"));

    [Fact(DisplayName = "resolve floor-switch rejects a bad switch")]
    public void ResolveBadSwitch() => Assert.Null(RuleForm.ResolveManualTrigger(RuleManualMarker.FloorSwitch, "5", "300"));

    [Fact(DisplayName = "resolve register")]
    public void ResolveRegister() => Assert.Equal(new RegisterTrigger(8), RuleForm.ResolveManualTrigger(RuleManualMarker.Register, "8", null));

    [Fact(DisplayName = "moderator-role-p")]
    public void ModeratorRole()
    {
        Assert.True(RuleForm.ModeratorRole("moderator"));
        Assert.True(RuleForm.ModeratorRole("admin"));
        Assert.False(RuleForm.ModeratorRole("user"));
        Assert.False(RuleForm.ModeratorRole(null));
    }
}

/// <summary>tests-misc.lisp run-signature-policy-tests.</summary>
public class SignaturePolicyTests
{
    [Fact(DisplayName = "the official signer is accepted")]
    public void Official() => Assert.True(PsobbTables.SignatureTrusted(SignatureStatus.Valid, "Terry Chatman"));

    [Fact(DisplayName = "a valid signature from an unknown signer is refused")]
    public void UnknownSigner() => Assert.False(PsobbTables.SignatureTrusted(SignatureStatus.Valid, "Mallory"));

    [Fact(DisplayName = "an unsigned exe is refused")]
    public void Unsigned() => Assert.False(PsobbTables.SignatureTrusted(SignatureStatus.Unsigned, null));

    [Fact(DisplayName = "a broken signature is refused even with the right name")]
    public void Broken() => Assert.False(PsobbTables.SignatureTrusted(SignatureStatus.Invalid, "Terry Chatman"));

    [Fact(DisplayName = "a missing signer name is refused")]
    public void MissingSigner() => Assert.False(PsobbTables.SignatureTrusted(SignatureStatus.Valid, null));

    [Fact(DisplayName = "the rejection label names an untrusted signer")]
    public void LabelNamesSigner() =>
        Assert.Contains("Mallory", new PsobbRejection(1, null, SignatureStatus.Valid, "Mallory").Label(Language.En));

    [Fact(DisplayName = "the rejection label explains an unsigned exe")]
    public void LabelUnsigned() =>
        Assert.Equal("no signature", new PsobbRejection(1, null, SignatureStatus.Unsigned, null).Label(Language.En));

    [Fact(DisplayName = "any other status reads as unverifiable")]
    public void LabelInvalid() =>
        Assert.Equal("署名を検証できません", new PsobbRejection(1, null, SignatureStatus.Invalid, null).Label(Language.Ja));
}

/// <summary>Candidate choice and trust caching (win32.lisp:589, main.lisp:52) - no Lisp suite; pinned here.</summary>
public class AttachPolicyTests
{
    private sealed class R(string name, bool trusted, bool quest)
    {
        public string Name { get; } = name;
        public bool Trusted { get; } = trusted;
        public bool Quest { get; } = quest;
        public bool Closed { get; set; }
    }

    private static R? Choose(params R[] rs) => PsobbAttach.Choose(rs, r => r.Trusted, r => r.Quest, r => r.Closed = true);

    [Fact(DisplayName = "a single candidate is returned untouched")]
    public void Single() => Assert.Equal("a", Choose(new R("a", false, false))?.Name);

    [Fact(DisplayName = "no trusted candidate: the first untrusted, the rest closed")]
    public void NoneTrusted()
    {
        R a = new("a", false, true), b = new("b", false, true);
        Assert.Same(a, Choose(a, b));
        Assert.True(b.Closed && !a.Closed);
    }

    [Fact(DisplayName = "a trusted candidate beats an untrusted title-squatter")]
    public void TrustedWins() => Assert.Equal("b", Choose(new R("a", false, true), new R("b", true, false))?.Name);

    [Fact(DisplayName = "among trusted candidates the one in a quest wins, else the first")]
    public void QuestWins()
    {
        Assert.Equal("c", Choose(new R("a", false, true), new R("b", true, false), new R("c", true, true))?.Name);
        Assert.Equal("b", Choose(new R("b", true, false), new R("c", true, false))?.Name);
    }

    [Fact(DisplayName = "trust rejection: trusted -> none, missing path fails closed, cached per pid")]
    public void TrustRejection()
    {
        Assert.Null(PsobbAttach.TrustRejection(1, null, () => "x.exe", _ => (SignatureStatus.Valid, "Terry Chatman")));
        var noPath = PsobbAttach.TrustRejection(1, null, () => null, _ => throw new InvalidOperationException());
        Assert.Equal(SignatureStatus.Invalid, noPath?.Status);
        Assert.Same(noPath, PsobbAttach.TrustRejection(1, noPath, () => throw new InvalidOperationException(), _ => default));
    }
}
