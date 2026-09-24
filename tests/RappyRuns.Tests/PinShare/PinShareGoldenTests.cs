using System.Text.Json;
using RappyRuns.Core.PinShare;

namespace RappyRuns.Tests.PinShare;

/// <summary>
/// Outputs of the Lisp relay (desktop/tools/export-pinshare-golden.lisp,
/// run on SBCL) reproduced by the port: number parsing and printing,
/// command translation, and whole in.txt texts including edge cases (JSON
/// nulls/booleans, float ids, bad items, pin-set fallbacks).
/// </summary>
public class PinShareGoldenTests
{
    private static readonly JsonElement Data = Golden.Load("pinshare/pinshare.json");

    public static TheoryData<int> NumberCases() => Indexes("numbers");

    public static TheoryData<int> CommandCases() => Indexes("commands");

    public static TheoryData<int> RenderCases() => Indexes("renders");

    private static TheoryData<int> Indexes(string key)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.GetProperty(key).GetArrayLength(); i++) data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(NumberCases))]
    public void NumberParsesAndPrintsLikeLisp(int index)
    {
        var c = Data.GetProperty("numbers")[index];
        var input = c.GetProperty("in").GetString()!;
        var parsed = PinShareText.ParseNumber(input);
        var text = c.GetProperty("text");
        if (text.ValueKind == JsonValueKind.Null) Assert.Null(parsed);
        else Assert.Equal(text.GetString(), PinShareText.FormatNumber(parsed!.Value));

        var integer = c.GetProperty("int");
        var ported = PinShareText.ParseInteger(input);
        if (integer.ValueKind == JsonValueKind.Null) Assert.Null(ported);
        else if (long.TryParse(integer.GetString(), out var expected)) Assert.Equal(expected, ported);
        else Assert.Null(ported); // documented deviation: no bignums
    }

    [Theory]
    [MemberData(nameof(CommandCases))]
    public void CommandTranslatesLikeLisp(int index)
    {
        var c = Data.GetProperty("commands")[index];
        var fields = c.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToList();
        var json = c.GetProperty("json");
        var message = Commands.Message(fields);
        if (json.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(message);
            return;
        }
        Assert.NotNull(message);
        AssertSameJson(PinShareJson.TryParse(json.GetString()!)!.Value, PinShareJson.TryParse(message)!.Value);
    }

    [Theory]
    [MemberData(nameof(RenderCases))]
    public void InboxRendersLikeLisp(int index)
    {
        var c = Data.GetProperty("renders")[index];
        var relay = new PinShareRelay
        {
            Channel = c.GetProperty("channel").GetString()!,
            Session = c.GetProperty("session").GetString()!,
            LastSeq = c.GetProperty("lastSeq").GetInt64(),
        };
        relay.SetStatus(c.GetProperty("status").GetString()!, c.GetProperty("message").GetString()!);
        relay.NoteMessage(c.GetProperty("state").GetString()!);
        var set = c.GetProperty("set").ValueKind == JsonValueKind.String
            ? new PinSet(PinShareJson.TryParse(c.GetProperty("set").GetString()!)!.Value)
            : null;
        Assert.Equal(c.GetProperty("text").GetString(), Inbox.Render(relay, c.GetProperty("time").GetInt64(), set));
    }

    // Same keys and values; numbers compared by value (jzon prints 0.0 where
    // System.Text.Json prints 0 - the server reads both as the same number).
    private static void AssertSameJson(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var e = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var a = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                Assert.Equal(e.Keys.Order(), a.Keys.Order());
                foreach (var key in e.Keys) AssertSameJson(e[key], a[key]);
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetDouble(), actual.GetDouble());
                break;
            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }
}
