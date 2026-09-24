using System.Text.Json;

namespace RappyRuns.Tests;

/// <summary>Reads expected outputs exported from the Lisp client (tests/golden/).</summary>
internal static class Golden
{
    public static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", name);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    /// <summary>A JSON scalar as the CLR value the formatter would be given.</summary>
    public static object? Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetInt32(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException($"unexpected golden value {e}"),
    };
}
