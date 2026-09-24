using System.Text.Json;
using System.Text.Json.Nodes;

namespace RappyRuns.Core.Api;

/// <summary>
/// Small readers over parsed response bodies that reproduce how the Lisp client
/// looked at jzon output (hash tables, vectors, integers vs floats).
/// </summary>
public static class ApiJson
{
    /// <summary><c>(ignore-errors (jzon:parse body))</c>: the parsed body, or null when it is not JSON.</summary>
    public static JsonNode? TryParse(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The value at <paramref name="key"/> when <paramref name="node"/> is an object, else null.</summary>
    public static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary>The string at <paramref name="key"/> (Lisp <c>stringp</c>), else null.</summary>
    public static string? GetString(JsonNode? node, string key) => AsString(Get(node, key));

    /// <summary>The node as a string when it is a JSON string, else null.</summary>
    public static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>
    /// The integer at <paramref name="key"/> (Lisp <c>integerp</c>: "5" in the JSON text,
    /// not "5.0" or "5e0"), else null.
    /// </summary>
    public static long? GetInteger(JsonNode? node, string key) => AsInteger(Get(node, key));

    /// <summary>The node as an integer when its JSON text is an integer literal, else null.</summary>
    public static long? AsInteger(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        var text = v.ToJsonString();
        if (text.IndexOfAny(['.', 'e', 'E']) >= 0) return null;
        return long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>The number at <paramref name="key"/> (any JSON number), else null.</summary>
    public static double? GetNumber(JsonNode? node, string key) =>
        Get(node, key) is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    /// <summary>
    /// Lisp truthiness of a parsed JSON value: absent and <c>false</c> are false;
    /// everything else — including 0, "" and empty arrays — is true. JSON <c>null</c>
    /// counts as false here (jzon made it the truthy symbol NULL; no server field
    /// relies on that, and false is the safer reading).
    /// </summary>
    public static bool Truthy(JsonNode? node) =>
        node is not null && !(node is JsonValue v && v.GetValueKind() is JsonValueKind.False or JsonValueKind.Null);

    /// <summary>How FORMAT's <c>~a</c> would print a parsed JSON scalar: strings raw, others as JSON text.</summary>
    public static string Princ(JsonNode? node) => AsString(node) ?? node?.ToJsonString() ?? "NIL";
}
