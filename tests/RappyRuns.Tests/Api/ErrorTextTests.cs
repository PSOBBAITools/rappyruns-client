using System.Text.Json;
using System.Text.Json.Nodes;
using RappyRuns.Core.Api;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Tests.Api;

/// <summary>Error wording, tokens, /api/me flags and trigger JSON (tests-helpers, tests-pinshare, tests-quests).</summary>
public class ErrorTextTests
{
    private static string ServerText(string message) => ErrorText.ServerStatus(Language.En, new ApiException(message));

    [Fact(DisplayName = "windows error code is extracted")]
    public void WindowsCodeExtracted() =>
        Assert.Equal(12029, ErrorText.WindowsErrorCode("WinHttpConnect failed (Windows error 12029)"));

    [Fact(DisplayName = "windows error code absent -> nil")]
    public void WindowsCodeAbsent() => Assert.Null(ErrorText.WindowsErrorCode("plain message"));

    [Fact(DisplayName = "connection failure reads like a sentence, not a condition")]
    public void ConnectionFailureSentence() =>
        Assert.Contains("could not connect", ServerText("WinHttpConnect failed (Windows error 12029)"));

    [Fact(DisplayName = "bad URL points at the settings fix")]
    public void BadUrlFix() => Assert.Contains("Save & verify", ServerText("Bad URL: nonsense"));

    [Fact(DisplayName = "unexpected HTTP status mentions the response")]
    public void UnexpectedStatus() => Assert.Contains("unexpected response", ServerText("GET /api/quests -> 500"));

    [Fact(DisplayName = "non-api conditions still say what happened")]
    public void NonApiCondition() =>
        Assert.Contains("check failed", ErrorText.ServerStatus(Language.En, new InvalidOperationException("boom")));

    [Fact(DisplayName = "token transport failure reads like a sentence")]
    public void TokenTransportSentence() =>
        Assert.Contains("could not connect",
            ErrorText.TokenStatus(Language.En, new ApiException("WinHttpConnect failed (Windows error 12029)")));

    [Fact(DisplayName = "server/token error texts match the Lisp golden (en, ja)")]
    public void ErrorTextGolden()
    {
        foreach (var c in Golden.Load("api/api-golden.json").GetProperty("errors").EnumerateArray())
        {
            var message = c.GetProperty("message").GetString()!;
            var code = c.GetProperty("code");
            Assert.Equal(code.ValueKind == JsonValueKind.Null ? null : code.GetInt32(), ErrorText.WindowsErrorCode(message));
            var error = new ApiException(message);
            Assert.Equal(c.GetProperty("server_en").GetString(), ErrorText.ServerStatus(Language.En, error));
            Assert.Equal(c.GetProperty("server_ja").GetString(), ErrorText.ServerStatus(Language.Ja, error));
            Assert.Equal(c.GetProperty("token_en").GetString(), ErrorText.TokenStatus(Language.En, error));
            Assert.Equal(c.GetProperty("token_ja").GetString(), ErrorText.TokenStatus(Language.Ja, error));
        }
    }

    [Theory(DisplayName = ".NET transport failures get the same hints as their WinHTTP codes")]
    [InlineData(TransportFailure.AddressNotFound, "server address not found")]
    [InlineData(TransportFailure.ConnectFailed, "could not connect")]
    [InlineData(TransportFailure.Timeout, "connection timed out")]
    [InlineData(TransportFailure.Tls, "secure connection")]
    public void TransportFailureHints(TransportFailure failure, string expected)
    {
        var error = new ApiException("GET /api/quests failed: something", failure);
        Assert.Equal("Server: " + Strings.Default.Tr(Language.En, ErrorText.HintKey(failure)!), ErrorText.ServerStatus(Language.En, error));
        Assert.Contains(expected, ErrorText.TokenStatus(Language.En, error));
    }

    [Fact(DisplayName = "normalize-token trims spaces and CRLF")]
    public void NormalizeCrlf() => Assert.Equal("eta_abc123", Tokens.Normalize("  eta_abc123\r\n"));

    [Fact(DisplayName = "normalize-token trims tabs")]
    public void NormalizeTabs() => Assert.Equal("eta_abc123", Tokens.Normalize("\teta_abc123\t"));

    [Fact(DisplayName = "normalize-token maps nil to empty")]
    public void NormalizeNull() => Assert.Equal("", Tokens.Normalize(null));

    [Fact(DisplayName = "normalize-token keeps empty empty")]
    public void NormalizeBlank() => Assert.Equal("", Tokens.Normalize("   "));

    [Fact(DisplayName = "unlinked-p is true with the default (empty) api token")]
    public void UnlinkedDefault() => Assert.True(Tokens.IsUnlinked(""));

    [Fact(DisplayName = "unlinked-p is false once a linked token is configured")]
    public void LinkedToken() => Assert.False(Tokens.IsUnlinked("eta_x"));

    [Fact(DisplayName = "unlinked-p ignores the guest token - a guest is not linked")]
    public void GuestIsNotLinked()
    {
        var auth = new AuthService(Api.Make(500).Client, new FakeSettings { AnonToken = "eta_g" });
        Assert.True(auth.IsUnlinked);
    }

    [Fact(DisplayName = "submission-token prefers the linked token over the guest")]
    public void SubmissionPrefersLinked() => Assert.Equal("eta_x", Tokens.Submission("eta_x", "eta_g"));

    [Fact(DisplayName = "submission-token falls back to the guest token")]
    public void SubmissionFallsBack() => Assert.Equal("eta_g", Tokens.Submission("", "eta_g"));

    [Fact(DisplayName = "submission-token normalizes and is empty without tokens")]
    public void SubmissionEmpty()
    {
        Assert.Equal("", Tokens.Submission(null, null));
        Assert.Equal("eta_g", Tokens.Submission(" \r\n", " eta_g\n"));
    }

    [Fact(DisplayName = "moderator-role-p")]
    public void ModeratorRole()
    {
        Assert.True(MeUser.IsModeratorRole("moderator"));
        Assert.True(MeUser.IsModeratorRole("admin"));
        Assert.False(MeUser.IsModeratorRole("user"));
        Assert.False(MeUser.IsModeratorRole(null));
    }

    [Fact(DisplayName = "an older server's /api/me (no features) and no account mean no")]
    public void PinShareFeature()
    {
        Assert.False(MeUser.HasPinShareFeature(JsonNode.Parse("""{"id":1,"role":"user"}""")));
        Assert.False(MeUser.HasPinShareFeature(JsonNode.Parse("""{"features":"pinshare"}""")));
        Assert.False(MeUser.HasPinShareFeature(null));
        Assert.True(MeUser.HasPinShareFeature(JsonNode.Parse("""{"features":["pinshare"]}""")));
        Assert.True(MeUser.FromJson(JsonNode.Parse("""{"features":["x","pinshare"]}""")!.AsObject()).PinShareAllowed);
        Assert.False(MeUser.FromJson(JsonNode.Parse("""{"features":"pinshare"}""")!.AsObject()).PinShareAllowed);
    }

    [Theory(DisplayName = "auto_publish is on only for the integer 1 (eql 1)")]
    [InlineData("""{"auto_publish":1}""", true)]
    [InlineData("""{"auto_publish":0}""", false)]
    [InlineData("""{"auto_publish":true}""", false)]
    [InlineData("""{"auto_publish":1.0}""", false)]
    [InlineData("""{}""", false)]
    public void AutoPublishFlag(string json, bool expected) =>
        Assert.Equal(expected, MeUser.FromJson(JsonNode.Parse(json)!.AsObject()).AutoPublish);

    [Fact(DisplayName = "monster trigger type / id")]
    public void MonsterTrigger()
    {
        var m = TriggerJson.FromParts("monster-dead", 1234)!;
        Assert.Equal("monster", (string?)m["type"]);
        Assert.Equal(1234, (long)m["monster"]!);
    }

    [Fact(DisplayName = "floor-switch type / floor / switch")]
    public void FloorSwitchTrigger()
    {
        var f = TriggerJson.FromParts("floor-switch", 5, 2)!;
        Assert.Equal("floor-switch", (string?)f["type"]);
        Assert.Equal(5, (long)f["floor"]!);
        Assert.Equal(2, (long)f["switch"]!);
    }

    [Fact(DisplayName = "register type / value")]
    public void RegisterTrigger()
    {
        var r = TriggerJson.FromParts("register", 254)!;
        Assert.Equal("register", (string?)r["type"]);
        Assert.Equal(254, (long)r["register"]!);
    }

    [Fact(DisplayName = "warp-in trigger type")]
    public void WarpInTrigger() => Assert.Equal("warp-in", (string?)TriggerJson.FromParts("warp-in")!["type"]);

    [Fact(DisplayName = "nil trigger -> nil")]
    public void NilTrigger()
    {
        Assert.Null(TriggerJson.FromParts(null));
        Assert.Null(TriggerJson.FromSexp(SexpNode.Nil));
        Assert.Null(TriggerJson.FromSexp(null));
    }

    [Fact(DisplayName = "trigger survives json round-trip")]
    public void TriggerRoundTrip()
    {
        var obj = new JsonObject { ["end"] = TriggerJson.FromParts("monster-dead", 1234) };
        var parsed = JsonNode.Parse(obj.ToJsonString())!;
        Assert.Equal(1234, (long)parsed["end"]!["monster"]!);
    }

    [Fact(DisplayName = "trigger->json matches the Lisp golden (from sexp)")]
    public void TriggerGolden()
    {
        foreach (var c in Golden.Load("api/api-golden.json").GetProperty("triggers").EnumerateArray())
        {
            var sexp = SexpReader.ReadOne(c.GetProperty("sexp").GetString()!);
            Assert.Equal(c.GetProperty("json").GetString(), TriggerJson.FromSexp(sexp)!.ToJsonString());
        }
    }
}
