using System.Text.Json;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Game;

/// <summary>
/// A start/end condition (spec core §5.1). The Lisp form is a list:
/// (:register N) | (:floor-switch F S) | (:warp-in) | (:monster-dead ID).
/// Records compare by value, like Lisp EQUAL on the lists.
/// </summary>
public abstract record Trigger
{
    /// <summary>The Lisp list form, e.g. (:FLOOR-SWITCH 5 2) - what quest-triggers.sexp and the run logs hold.</summary>
    public abstract SexpNode ToSexp();

    /// <summary>Parse the internal list form; null for anything else (quests.lisp reads it with getf, unknown shapes never match).</summary>
    public static Trigger? FromSexp(SexpNode? node)
    {
        if (node is not SList { Tail: null } list || list.Items.Count == 0 || list.Items[0] is not SKeyword head) return null;
        long? Arg(int i) => i < list.Items.Count ? list.Items[i].AsLong : null;
        return head.Name switch
        {
            "REGISTER" when Arg(1) is { } n => new RegisterTrigger((int)n),
            "FLOOR-SWITCH" when Arg(1) is { } f && Arg(2) is { } s => new FloorSwitchTrigger((int)f, (int)s),
            "WARP-IN" => new WarpInTrigger(),
            "MONSTER-DEAD" when Arg(1) is { } id => new MonsterDeadTrigger((int)id),
            _ => null,
        };
    }

    /// <summary>
    /// quests.lisp:73 json-trigger: one trigger object from GET /api/quests;
    /// null for unknown types or non-objects. A missing number field reads as
    /// null in Lisp (a trigger that can never fire); here it rejects the trigger.
    /// </summary>
    public static Trigger? FromJson(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e) return null;
        var type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        int? Num(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        return type switch
        {
            "warp-in" => new WarpInTrigger(),
            "register" when Num("register") is { } n => new RegisterTrigger(n),
            "floor-switch" when Num("floor") is { } f && Num("switch") is { } s => new FloorSwitchTrigger(f, s),
            "monster" when Num("monster") is { } id => new MonsterDeadTrigger(id),
            _ => null,
        };
    }

    /// <summary>api-client.lisp:616 trigger->json: the wire object the server expects (compact, jzon key order).</summary>
    public abstract string ToJson();

    /// <summary>rule-form.lisp:37 rule-trigger-label: "register:7", "floor-switch:3:42", "monster:9", "warp-in".</summary>
    public abstract string Label { get; }

    /// <summary>Label for a possibly-absent trigger ("" for NIL).</summary>
    public static string LabelOf(Trigger? trigger) => trigger?.Label ?? "";
}

public sealed record RegisterTrigger(int Register) : Trigger
{
    public override SexpNode ToSexp() => SexpNode.List(SexpNode.Kw("register"), SexpNode.Int(Register));

    public override string ToJson() => FormattableString.Invariant($"{{\"type\":\"register\",\"register\":{Register}}}");

    public override string Label => FormattableString.Invariant($"register:{Register}");
}

public sealed record FloorSwitchTrigger(int Floor, int Switch) : Trigger
{
    public override SexpNode ToSexp() => SexpNode.List(SexpNode.Kw("floor-switch"), SexpNode.Int(Floor), SexpNode.Int(Switch));

    public override string ToJson() =>
        FormattableString.Invariant($"{{\"type\":\"floor-switch\",\"floor\":{Floor},\"switch\":{Switch}}}");

    public override string Label => FormattableString.Invariant($"floor-switch:{Floor}:{Switch}");
}

public sealed record WarpInTrigger : Trigger
{
    public override SexpNode ToSexp() => SexpNode.List(SexpNode.Kw("warp-in"));

    public override string ToJson() => "{\"type\":\"warp-in\"}";

    public override string Label => "warp-in";
}

public sealed record MonsterDeadTrigger(int MonsterId) : Trigger
{
    public override SexpNode ToSexp() => SexpNode.List(SexpNode.Kw("monster-dead"), SexpNode.Int(MonsterId));

    public override string ToJson() => FormattableString.Invariant($"{{\"type\":\"monster\",\"monster\":{MonsterId}}}");

    public override string Label => FormattableString.Invariant($"monster:{MonsterId}");
}

/// <summary>
/// One detection definition (quests.lisp:9 quest-def). A class, not a record:
/// the detector keys trackers by definition identity (detect.lisp:292 uses
/// EQL), so two equal-looking definitions are still two definitions.
/// </summary>
public sealed class QuestDef(string slug, int? episode, IReadOnlyList<string> names, int? number, Trigger? start, Trigger? end)
{
    /// <summary>Server quest slug (must match the server's slugify).</summary>
    public string Slug { get; } = slug;

    /// <summary>Site episode 1, 2 or 4.</summary>
    public int? Episode { get; } = episode;

    /// <summary>In-game quest names that map here.</summary>
    public IReadOnlyList<string> Names { get; } = names;

    /// <summary>In-game quest number, null when the quest has none.</summary>
    public int? Number { get; } = number;

    public Trigger? Start { get; } = start;

    public Trigger? End { get; } = end;

    /// <summary>
    /// quests.lisp:111 quest-def-matches-p (psostats): by in-game number when it
    /// is positive, or by name when the episode (if known) agrees. The number
    /// match is episode-blind on purpose.
    /// </summary>
    public bool Matches(int? number, int? episode, string? name) =>
        (number is > 0 && number == Number) ||
        (name is not null && (episode is null || episode == Episode) && Names.Contains(name, StringComparer.Ordinal));

    public override string ToString() => Slug;
}

/// <summary>
/// The active quest definitions (quests.lisp:17-54): the builtin
/// data/quest-triggers.sexp merged with the trigger-carrying categories from
/// GET /api/quests, server winning on slug collisions. Thread-safe: the poll
/// thread reads <see cref="Defs"/> while a worker swaps in server definitions.
/// </summary>
public sealed class QuestCatalog
{
    private volatile IReadOnlyList<QuestDef> _builtin = [];
    private volatile IReadOnlyList<QuestDef> _server = [];
    private volatile IReadOnlyList<QuestDef> _defs = [];
    private readonly Lock _gate = new();

    public QuestCatalog()
    {
    }

    /// <summary>A catalog with fixed definitions (tests binding *quest-defs*).</summary>
    public QuestCatalog(IEnumerable<QuestDef> builtin)
    {
        _builtin = builtin.ToList();
        Recompute();
    }

    /// <summary>The merged definitions, server ones first (the Lisp *quest-defs*).</summary>
    public IReadOnlyList<QuestDef> Defs => _defs;

    public IReadOnlyList<QuestDef> Builtin => _builtin;

    public IReadOnlyList<QuestDef> Server => _server;

    /// <summary>quests.lisp:40 quest-triggers-path: data\quest-triggers.sexp next to the exe.</summary>
    public static string DefaultPath(string? exeDir = null) =>
        Path.Combine(exeDir ?? AppContext.BaseDirectory, "data", "quest-triggers.sexp");

    /// <summary>
    /// quests.lisp:40 with the development fallback: the exe-adjacent file when it
    /// exists, else client/data/quest-triggers.sexp in a source tree above the exe
    /// (the Lisp falls back to the ASDF source directory the same way).
    /// </summary>
    public static string ResolvePath(string? exeDir = null)
    {
        var adjacent = DefaultPath(exeDir);
        if (File.Exists(adjacent)) return adjacent;
        for (var dir = new DirectoryInfo(exeDir ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var source = Path.Combine(dir.FullName, "client", "data", "quest-triggers.sexp");
            if (File.Exists(source)) return source;
        }
        return adjacent;
    }

    /// <summary>
    /// quests.lisp:56 load-quest-defs: read the builtin definitions (one list of
    /// plists, ;; comments allowed) and re-merge. Returns the builtin count.
    /// Throws on a missing or malformed file, like the Lisp open/read.
    /// </summary>
    public int LoadBuiltin(string path)
    {
        var form = SexpReader.ReadOne(File.ReadAllText(path));
        var defs = new List<QuestDef>();
        foreach (var entry in form.Elements)
        {
            var p = Plist.From(entry) ?? new Plist();
            defs.Add(new QuestDef(
                p.Get("slug")?.AsString ?? "",
                (int?)p.Get("episode")?.AsLong,
                p.Get("names") is { IsNil: false } names ? names.Elements.Select(n => n.AsString ?? "").ToList() : [],
                (int?)p.Get("number")?.AsLong,
                Trigger.FromSexp(p.Get("start")),
                Trigger.FromSexp(p.Get("end"))));
        }
        _builtin = defs;
        Recompute();
        return defs.Count;
    }

    /// <summary>
    /// quests.lisp:85 server-quest->def: a GET /api/quests entry, or null when it
    /// lacks a start or end trigger (display-only catalog quest).
    /// </summary>
    public static QuestDef? ServerQuestToDef(JsonElement quest)
    {
        if (quest.ValueKind != JsonValueKind.Object) return null;
        JsonElement? Prop(string key) => quest.TryGetProperty(key, out var v) ? v : null;
        var start = Trigger.FromJson(Prop("start"));
        var end = Trigger.FromJson(Prop("end"));
        if (start is null || end is null) return null;
        var names = Prop("game_names") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).ToList()
            : [];
        int? Int(string key) => Prop(key) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i) ? i : null;
        return new QuestDef(
            Prop("slug") is { ValueKind: JsonValueKind.String } s ? s.GetString()! : "",
            Int("episode"), names, Int("game_number"), start, end);
    }

    /// <summary>
    /// quests.lisp:100 set-server-quest-defs: replace the server definitions from
    /// the parsed GET /api/quests array and re-merge. Returns the number of
    /// timeable server categories. An empty array drops them all (offline = builtin only).
    /// </summary>
    public int SetServerQuests(JsonElement quests)
    {
        var defs = quests.ValueKind == JsonValueKind.Array
            ? quests.EnumerateArray().Select(ServerQuestToDef).OfType<QuestDef>().ToList()
            : [];
        _server = defs;
        Recompute();
        return defs.Count;
    }

    /// <summary>quests.lisp:46 recompute-quest-defs: server defs, then builtin ones whose slug the server does not define.</summary>
    private void Recompute()
    {
        lock (_gate)
        {
            var server = _server;
            var slugs = server.Select(d => d.Slug).ToHashSet(StringComparer.Ordinal);
            _defs = server.Concat(_builtin.Where(d => !slugs.Contains(d.Slug))).ToList();
        }
    }

    /// <summary>quests.lisp:120 find-quest-defs: every matching definition, in definition order.</summary>
    public static List<QuestDef> FindAll(IEnumerable<QuestDef> defs, int? number, int? episode, string? name) =>
        defs.Where(d => d.Matches(number, episode, name)).ToList();

    public List<QuestDef> FindAll(int? number, int? episode, string? name) => FindAll(Defs, number, episode, name);

    /// <summary>quests.lisp:133 unknown-slugs: definition slugs the server does not know (misconfiguration).</summary>
    public static List<string> UnknownSlugs(IEnumerable<string> serverSlugs, IEnumerable<QuestDef> defs)
    {
        var known = serverSlugs.ToHashSet(StringComparer.Ordinal);
        return defs.Where(d => !known.Contains(d.Slug)).Select(d => d.Slug).ToList();
    }

    public List<string> UnknownSlugs(IEnumerable<string> serverSlugs) => UnknownSlugs(serverSlugs, Defs);

}
