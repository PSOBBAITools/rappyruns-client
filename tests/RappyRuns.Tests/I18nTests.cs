using RappyRuns.Core.I18n;

namespace RappyRuns.Tests;

// Port of run-i18n-tests (client/tests/tests-misc.lisp) plus the golden
// comparison against the Lisp FORMAT output.
public class I18nTests
{
    [Fact]
    public void EveryStringFormatsLikeLispFormat()
    {
        var golden = Golden.Load("i18n-format.json");
        Assert.True(golden.GetArrayLength() > 0);
        foreach (var g in golden.EnumerateArray())
        {
            var key = g.GetProperty("key").GetString()!;
            var language = Languages.FromCode(g.GetProperty("lang").GetString());
            var args = g.GetProperty("args").EnumerateArray().Select(Golden.Value).ToArray();
            var expected = g.GetProperty("expected").GetString();
            Assert.True(expected == Strings.Default.Tr(language, key, args), $"{key} ({language}) [{string.Join(", ", args)}]");
        }
    }

    [Fact]
    public void TableHasAllKeysInBothLanguages()
    {
        Assert.Equal(247, Strings.Default.Entries.Count);
        foreach (var (key, entry) in Strings.Default.Entries)
        {
            Assert.False(string.IsNullOrEmpty(entry.En), key);
            Assert.False(string.IsNullOrEmpty(entry.Ja), key);
        }
    }

    [Fact]
    public void MissingKeyIsAnError() =>
        Assert.Throws<KeyNotFoundException>(() => Strings.Default.Tr(Language.En, "no-such-key"));

    [Theory]
    [InlineData("{0?x{0}y}", null, "")]
    [InlineData("{0?x{0}y}", 5, "x5y")]
    [InlineData("item{0#|s}", 1, "item")]
    [InlineData("item{0#|s}", 2, "items")]
    [InlineData("categor{0#y|ies}", 0, "categories")]
    public void TemplateSyntax(string template, object? arg, string expected)
        => Assert.Equal(expected, Template.Format(template, [arg]));

    [Theory]
    [InlineData("{0")]
    [InlineData("{x}")]
    [InlineData("{0?never closed")]
    [InlineData("{1}")]
    public void MalformedTemplatesThrow(string template) =>
        Assert.Throws<FormatException>(() => Template.Format(template, ["a"]));

    [Fact]
    public void UnknownLanguageCodeIsEnglish()
    {
        Assert.Equal(Language.En, Languages.FromCode("fr"));
        Assert.Equal(Language.En, Languages.FromCode(null));
        Assert.Equal(Language.Ja, Languages.FromCode("ja"));
    }
}
