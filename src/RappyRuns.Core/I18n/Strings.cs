using System.Text.Json;

namespace RappyRuns.Core.I18n;

/// <summary>
/// The UI string table (strings.json, generated from client/src/i18n.lisp by
/// desktop/tools/export-i18n.lisp, plus strings.extra.json) and the <c>tr</c> lookup. The same table is
/// sent to the web UI at startup.
/// </summary>
public sealed class Strings
{
    public sealed record Entry(string En, string Ja)
    {
        public string For(Language language) => language == Language.Ja ? Ja : En;
    }

    private readonly Dictionary<string, Entry> _entries;

    private Strings(Dictionary<string, Entry> entries) => _entries = entries;

    public static Strings Default { get; } = LoadEmbedded();

    public IReadOnlyDictionary<string, Entry> Entries => _entries;

    /// <summary>The string for <paramref name="key"/>, formatted with <paramref name="args"/>. A missing key is a bug and throws.</summary>
    public string Tr(Language language, string key, params object?[] args)
    {
        if (!_entries.TryGetValue(key, out var entry))
            throw new KeyNotFoundException($"No UI string for {key}");
        return Template.Format(entry.For(language), args);
    }

    /// <summary>Raw templates for one language (what the UI formats itself).</summary>
    public IReadOnlyDictionary<string, string> TemplatesFor(Language language) =>
        _entries.ToDictionary(kv => kv.Key, kv => kv.Value.For(language));

    public static Strings Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            entries[prop.Name] = new Entry(
                prop.Value.GetProperty("en").GetString() ?? throw new InvalidDataException($"{prop.Name}: no en"),
                prop.Value.GetProperty("ja").GetString() ?? throw new InvalidDataException($"{prop.Name}: no ja"));
        }
        return new Strings(entries);
    }

    /// <summary>
    /// strings.json (generated from i18n.lisp) merged with strings.extra.json
    /// (keys only the C# client and its web UI use, docs/ipc.md). The generated
    /// table wins on a clash, so a regenerated strings.json never loses a key.
    /// </summary>
    private static Strings LoadEmbedded()
    {
        var table = Parse(ReadResource("RappyRuns.Core.I18n.strings.json"));
        foreach (var (key, entry) in Parse(ReadResource("RappyRuns.Core.I18n.strings.extra.json"))._entries)
            table._entries.TryAdd(key, entry);
        return table;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
