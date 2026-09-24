using System.Text.Json.Nodes;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Api;

/// <summary>
/// <c>trigger-&gt;json</c> (api-client.lisp): an internal trigger list to the wire
/// object the server expects in POST /api/quests, mirroring the server's TRIGGER-JSON:
/// <list type="bullet">
/// <item><c>(:warp-in)</c> → <c>{"type":"warp-in"}</c></item>
/// <item><c>(:register N)</c> → <c>{"type":"register","register":N}</c></item>
/// <item><c>(:floor-switch F S)</c> → <c>{"type":"floor-switch","floor":F,"switch":S}</c></item>
/// <item><c>(:monster-dead ID)</c> → <c>{"type":"monster","monster":ID}</c></item>
/// </list>
/// </summary>
public static class TriggerJson
{
    /// <summary>
    /// From the trigger's kind (the keyword name without colon, any case) and its
    /// integer arguments. Null kind → null (no trigger). An unknown kind or missing
    /// argument throws <see cref="ArgumentException"/> (Lisp <c>ecase</c>).
    /// </summary>
    public static JsonObject? FromParts(string? kind, params long[] args)
    {
        if (kind is null) return null;
        long Arg(int i) => i < args.Length ? args[i] : throw new ArgumentException($"trigger {kind} needs {i + 1} argument(s)");
        return kind.ToLowerInvariant() switch
        {
            "warp-in" => new JsonObject { ["type"] = "warp-in" },
            "register" => new JsonObject { ["type"] = "register", ["register"] = Arg(0) },
            "floor-switch" => new JsonObject { ["type"] = "floor-switch", ["floor"] = Arg(0), ["switch"] = Arg(1) },
            "monster-dead" => new JsonObject { ["type"] = "monster", ["monster"] = Arg(0) },
            _ => throw new ArgumentException($"unknown trigger {kind}"),
        };
    }

    /// <summary>From a trigger as read from sexp, e.g. <c>(:floor-switch 5 2)</c>; NIL → null.</summary>
    public static JsonObject? FromSexp(SexpNode? trigger)
    {
        if (trigger is null || trigger.IsNil) return null;
        var items = trigger.Elements;
        var kind = items[0].KeywordName ?? throw new ArgumentException($"not a trigger: {trigger}");
        var args = items.Skip(1).Select(n => n.AsLong ?? throw new ArgumentException($"not a trigger: {trigger}")).ToArray();
        return FromParts(kind, args);
    }
}
