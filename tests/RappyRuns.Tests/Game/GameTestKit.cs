using RappyRuns.Core.Game;
using RappyRuns.Core.Sexp;
using static RappyRuns.Tests.Game.MockMemory;

namespace RappyRuns.Tests.Game;

/// <summary>Shared fixtures of the detection suites (tests-detect.lisp readers, the builtin quest definitions).</summary>
internal static class GameTestKit
{
    /// <summary>The repository's client/data/quest-triggers.sexp (the Lisp tests' load-quest-defs).</summary>
    public static string QuestTriggersPath
    {
        get
        {
            // No data\ next to the test assembly: the source-tree fallback finds client/data.
            var path = QuestCatalog.ResolvePath();
            return File.Exists(path) ? path : throw new FileNotFoundException("client/data/quest-triggers.sexp not found above the test directory");
        }
    }

    public static QuestCatalog BuiltinCatalog()
    {
        var catalog = new QuestCatalog();
        catalog.LoadBuiltin(QuestTriggersPath);
        return catalog;
    }

    /// <summary>A catalog = the builtin definitions with extra ones consed in front (the tests' let of *quest-defs*).</summary>
    public static QuestCatalog CatalogWith(params QuestDef[] extra) =>
        new(extra.Concat(BuiltinCatalog().Defs));

    public static (Detector Detector, ManualGameClock Clock) NewDetector(QuestCatalog? catalog = null)
    {
        var clock = new ManualGameClock();
        return (new Detector(catalog ?? BuiltinCatalog(), clock), clock);
    }

    /// <summary>tests-detect.lisp:122 ttf-reader.</summary>
    public static MockReader TtfReader(int start = 0, int end = 0, int seg = 0, float pb = 0f, bool warping = false,
        int difficulty = 0, double? hpScale = null, long nameColor = 0xFFFFFFFF, long partnerColor = 0xFFFFFFFF) =>
        GameRegions(
            players:
            [
                PlayerBlock("Ryu", classId: 2, floor: 1, pb: pb, warping: warping, nameColor: nameColor),
                PlayerBlock("Elly", classId: 8, floor: 1, nameColor: partnerColor),
            ],
            questName: "Towards the Future", questNumber: 118, difficulty: difficulty, hpScale: hpScale,
            registerValues: [(12, start), (254, end), (100, seg)]);

    /// <summary>tests-detect.lisp:135 lobby-reader.</summary>
    public static MockReader LobbyReader() => GameRegions(players: [PlayerBlock("Ryu", classId: 2, floor: 0)]);

    /// <summary>tests-detect.lisp:139 step-with.</summary>
    public static List<Plist> StepWith(this Detector detector, IMemoryReader reader) =>
        detector.Step(PsobbReader.ReadSnapshot(reader));

    public static string? Str(this Plist run, string key) => run.Get(key)?.AsString;

    public static long? Num(this Plist run, string key) => run.Get(key)?.AsLong;

    public static bool Truthy(this Plist run, string key) => run.Get(key) is { IsTrue: true };

    public static List<Plist> Players(this Plist run) => run.Get("players")!.Elements.Select(p => Plist.From(p)!).ToList();

    public static QuestDef SegmentDef() => new("ep1-towards-the-future-2-rooms", 1, ["Towards the Future"], 118,
        new RegisterTrigger(12), new RegisterTrigger(100));
}
