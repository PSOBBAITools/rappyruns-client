using System.Globalization;
using RappyRuns.Core.Game;

namespace RappyRuns.Tests.Game;

/// <summary>The jzon-compatible JSON writer (single floats, strings) behind the run payload.</summary>
public class LispJsonTests
{
    public static TheoryData<long, string> Floats()
    {
        var data = new TheoryData<long, string>();
        foreach (var e in Golden.Load("game/json-floats.json").EnumerateArray())
            data.Add(e.GetProperty("bits").GetInt64(), e.GetProperty("text").GetString()!);
        return data;
    }

    [Theory(DisplayName = "golden: jzon single-float text")]
    [MemberData(nameof(Floats))]
    public void FloatText(long bits, string text) =>
        Assert.Equal(text, LispJson.FormatSingle(BitConverter.Int32BitsToSingle(unchecked((int)bits))));

    [Fact(DisplayName = "every one-decimal telemetry value prints as its decimal and round-trips")]
    public void Round1Values()
    {
        for (var k = -200_000; k <= 200_000; k++)
        {
            var v = k / 10f;
            var text = LispJson.FormatSingle(v);
            Assert.Equal(v, float.Parse(text, CultureInfo.InvariantCulture));
        }
    }

    [Fact(DisplayName = "jzon float text always parses back to the same float")]
    public void RoundTripsSweep()
    {
        var random = new Random(322);
        for (var i = 0; i < 200_000; i++)
        {
            var v = BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue));
            if (!float.IsFinite(v)) continue;
            Assert.Equal(v, float.Parse(LispJson.FormatSingle(v), CultureInfo.InvariantCulture));
        }
    }

    [Fact(DisplayName = "strings escape like jzon: quote, backslash, named controls, \\uXXXX, raw UTF-8")]
    public void StringEscapes() =>
        Assert.Equal("\"a\\\"b\\\\c\\n\\t\\u0001ダーク\"", LispJson.Str("a\"b\\c\n\t\u0001ダーク").Stringify());
}
