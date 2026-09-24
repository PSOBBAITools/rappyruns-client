namespace RappyRuns.Core.I18n;

public enum Language
{
    En,
    Ja,
}

public static class Languages
{
    public static IReadOnlyList<Language> All { get; } = [Language.En, Language.Ja];

    /// <summary>The config/IPC code: "en" or "ja".</summary>
    public static string Code(this Language language) => language == Language.Ja ? "ja" : "en";

    /// <summary>Anything but a known code is English (Lisp valid-language).</summary>
    public static Language FromCode(string? code) =>
        string.Equals(code, "ja", StringComparison.OrdinalIgnoreCase) ? Language.Ja : Language.En;

    /// <summary>Label for the language toggle, always in its own language.</summary>
    public static string Label(this Language language) => language == Language.Ja ? "日本語" : "English";
}
