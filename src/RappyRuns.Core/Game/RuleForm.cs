using System.Globalization;
using System.Text.Json;

namespace RappyRuns.Core.Game;

/// <summary>A manual end-trigger marker in the rule form (rule-form.lisp:59).</summary>
public enum RuleManualMarker
{
    Monster,
    FloorSwitch,
    Register,
}

/// <summary>
/// The quest-rule form's pure logic (rule-form.lisp): which fetched quests can
/// parent a rule, matching the just-played quest, manual trigger parsing and
/// the API error text. The UI owns the controls; this owns the decisions.
/// </summary>
public static class RuleForm
{
    private static JsonElement? Prop(JsonElement quest, string key) =>
        quest.ValueKind == JsonValueKind.Object && quest.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static bool Truthy(JsonElement? v) => v is { } e && e.ValueKind is not (JsonValueKind.Null or JsonValueKind.False);

    /// <summary>rule-form.lisp:9 timeable-quests: /api/quests entries that carry start+end triggers.</summary>
    public static List<JsonElement> TimeableQuests(JsonElement quests) =>
        quests.ValueKind != JsonValueKind.Array
            ? []
            : quests.EnumerateArray().Where(q => Truthy(Prop(q, "start")) && Truthy(Prop(q, "end"))).ToList();

    /// <summary>rule-form.lisp:16 quest-parent-label: "name  (slug)".</summary>
    public static string QuestParentLabel(JsonElement quest) =>
        $"{Text(Prop(quest, "name"))}  ({Text(Prop(quest, "slug"))})";

    private static string Text(JsonElement? v) => v switch
    {
        null => "NIL",
        { ValueKind: JsonValueKind.String } s => s.GetString()!,
        { } other => other.GetRawText(),
    };

    /// <summary>
    /// rule-form.lisp:19 detected-parent: the timeable quest matching the
    /// just-played <paramref name="runQuest"/> by in-game number, else by
    /// episode + name; null when none matches.
    /// </summary>
    public static JsonElement? DetectedParent(IReadOnlyList<JsonElement> parents, RunQuest? runQuest)
    {
        if (runQuest is null) return null;
        if (runQuest.Number is > 0)
        {
            foreach (var q in parents)
            {
                if (Prop(q, "game_number") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) && v == runQuest.Number)
                    return q;
            }
        }
        if (runQuest.Name is { } name)
        {
            foreach (var q in parents)
            {
                var episodeMatches = Prop(q, "episode") is { ValueKind: JsonValueKind.Number } e && e.TryGetInt64(out var ep)
                    ? runQuest.Episode == ep
                    : runQuest.Episode is null;
                if (!episodeMatches) continue;
                if (Prop(q, "game_names") is { ValueKind: JsonValueKind.Array } names &&
                    names.EnumerateArray().Any(n => n.ValueKind == JsonValueKind.String && n.GetString() == name))
                    return q;
            }
        }
        return null;
    }

    /// <summary>rule-form.lisp:48 rule-error-message: the payload's "message", else its "errors" joined by "; ", else "?".</summary>
    public static string RuleErrorMessage(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p) return "?";
        if (Prop(p, "message") is { ValueKind: JsonValueKind.String } m && m.GetString() is { Length: > 0 } message) return message;
        if (Prop(p, "errors") is { } errors)
        {
            if (errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                return string.Join("; ", errors.EnumerateArray().Select(e => Text(e)));
            if (errors.ValueKind == JsonValueKind.String && errors.GetString() is { Length: > 0 } s) return string.Join("; ", s.Select(c => c.ToString()));
        }
        return "?";
    }

    private static readonly char[] LispWhitespace = [' ', '\t', '\n', '\r', '\f'];

    /// <summary>rule-form.lisp:64 parse-int-in-range: trim spaces, whole-string integer in [min, max], else null.</summary>
    public static int? ParseIntInRange(string? text, int min, int max)
    {
        // string-trim " ", then parse-integer's own whitespace skipping.
        var s = (text ?? "").Trim(' ').Trim(LispWhitespace);
        // parse-integer: optional sign, decimal digits, nothing else.
        if (s.Length == 0) return null;
        var start = s[0] is '+' or '-' ? 1 : 0;
        if (start == s.Length || !s[start..].All(char.IsAsciiDigit)) return null;
        if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return null;
        return n >= min && n <= max ? (int)n : null;
    }

    /// <summary>rule-form.lisp:69 resolve-manual-trigger: a trigger from the value fields, or null when out of range.</summary>
    public static Trigger? ResolveManualTrigger(RuleManualMarker marker, string? value1, string? value2) => marker switch
    {
        RuleManualMarker.Monster => ParseIntInRange(value1, 0, 65535) is { } id ? new MonsterDeadTrigger(id) : null,
        RuleManualMarker.FloorSwitch => ParseIntInRange(value1, 0, 17) is { } f && ParseIntInRange(value2, 0, 255) is { } s
            ? new FloorSwitchTrigger(f, s)
            : null,
        RuleManualMarker.Register => ParseIntInRange(value1, 0, 255) is { } n ? new RegisterTrigger(n) : null,
        _ => null,
    };

    /// <summary>rule-form.lisp:81 moderator-role-p: "moderator" or "admin" (server MODELS:MODERATOR-P).</summary>
    public static bool ModeratorRole(string? role) => role is "moderator" or "admin";
}
