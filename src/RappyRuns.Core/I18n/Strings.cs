using System.Text.Json;

namespace RappyRuns.Core.I18n;

/// <summary>
/// The UI string table (strings.json, generated from client/src/i18n.lisp by
/// desktop/tools/export-i18n.lisp) and the <c>tr</c> lookup. The same table is
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

    private static Strings LoadEmbedded()
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream("RappyRuns.Core.I18n.strings.json")
            ?? throw new InvalidOperationException("strings.json is not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
