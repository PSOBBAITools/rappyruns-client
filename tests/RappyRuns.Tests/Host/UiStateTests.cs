using System.Text.Json.Nodes;
using RappyRuns.Core.Api;
using RappyRuns.Core.I18n;
using RappyRuns.Host;

namespace RappyRuns.Tests.Host;

/// <summary>The host side of the ipc.md contract: Msg JSON, the state patch protocol, status wording.</summary>
public class UiStateTests
{
    private sealed class Sink : IUiSink
    {
        public List<(string Name, string Json)> Events { get; } = [];

        public void Emit(string name, object? data) => Events.Add((name, IpcJson.Serialize(data)));
    }

    private static SettingsDto Settings(string language = "en") => new()
    {
        Language = language,
        ServerUrl = "https://s.example",
        ApiToken = "",
        RecordDir = @"C:\Videos\RappyRuns\",
        OverlayCorner = "top-right",
        PinshareChannel = "",
    };

    [Fact(DisplayName = "Msg serializes as {key,args} with nested Msgs and nulls, raw text as {text}")]
    public void MsgJson()
    {
        Assert.Equal("""{"key":"version-status","args":["0.61.0",{"key":"update-up-to-date","args":[]}]}""",
            IpcJson.Serialize(Msg.Of("version-status", "0.61.0", Msg.Of("update-up-to-date"))));
        Assert.Equal("""{"key":"server-ok","args":[128,342,null]}""", IpcJson.Serialize(Msg.Of("server-ok", 128, 342L, null)));
        Assert.Equal("""{"text":"a \u0022b\u0022"}""", IpcJson.Serialize(Msg.Raw("a \"b\"")));
        Assert.Equal("""{"msg":{"key":"token-ok","args":["teapot"]},"tone":"ok"}""", IpcJson.Serialize(Line.Ok(Msg.Of("token-ok", "teapot"))));
        Assert.Equal("Server: OK (3 quests, 1 timed category)", Msg.Of("server-ok", 3, 1, null).Render(Language.En));
    }

    [Fact(DisplayName = "nothing is sent before hello; afterwards only changed top-level keys")]
    public void PatchProtocol()
    {
        var sink = new Sink();
        var state = new UiState(() => Settings(), moderator: false, pinshareAllowed: false);
        state.Attach(sink);
        state.SetGame(Line.Ok(Msg.Of("game-attached")));
        Assert.Empty(sink.Events);

        var snapshot = state.Snapshot();
        Assert.Equal(12, snapshot.Count);
        Assert.Equal("none", snapshot["quest"]!["kind"]!.GetValue<string>());
        Assert.Equal("ok", snapshot["game"]!["tone"]!.GetValue<string>());

        state.SetGame(Line.Ok(Msg.Of("game-attached")));
        state.Publish();
        Assert.Empty(sink.Events); // unchanged: no event (R1)

        state.SetToken(Line.Busy(Msg.Of("token-checking")));
        var (name, json) = Assert.Single(sink.Events);
        Assert.Equal("state", name);
        var patch = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(["token"], patch.Select(kv => kv.Key));

        sink.Events.Clear();
        state.Batch(s =>
        {
            s.SetPairing(true);
            s.SetQuest(UiState.QuestActive("ep1-towards-the-future", 1, "1:02", true, "12:31.402", "-3.2s"));
        });
        var both = JsonNode.Parse(Assert.Single(sink.Events).Json)!.AsObject();
        Assert.Equal(["pairing", "quest"], both.Select(kv => kv.Key).Order());
        Assert.Equal("-3.2s", both["quest"]!["ghost"]!["gap"]!.GetValue<string>());
    }

    [Fact(DisplayName = "a settings change is sent whole after InvalidateSettings")]
    public void SettingsInvalidation()
    {
        var sink = new Sink();
        var language = "en";
        var state = new UiState(() => Settings(language), false, false);
        state.Attach(sink);
        state.Snapshot();
        language = "ja";
        state.Publish();
        Assert.Empty(sink.Events); // cached until invalidated
        state.InvalidateSettings();
        var patch = JsonNode.Parse(Assert.Single(sink.Events).Json)!.AsObject();
        Assert.Equal("ja", patch["settings"]!["language"]!.GetValue<string>());
        Assert.Equal(21, patch["settings"]!.AsObject().Count);
    }

    [Fact(DisplayName = "server/token failure wording matches ErrorText key for key")]
    public void ErrorWording()
    {
        var connect = new ApiException("GET /api/quests failed", TransportFailure.ConnectFailed);
        Assert.Equal(ErrorText.ServerStatus(Language.En, connect), StatusMsgs.ServerError(connect).Render(Language.En));
        var status = new ApiException("GET /api/quests -> 500") { Status = 500 };
        Assert.Equal(ErrorText.ServerStatus(Language.Ja, status), StatusMsgs.ServerError(status).Render(Language.Ja));
        Assert.Equal(ErrorText.TokenStatus(Language.En, connect), StatusMsgs.TokenError(connect).Render(Language.En));
        var other = new InvalidOperationException("boom");
        Assert.Equal(ErrorText.ServerStatus(Language.En, other), StatusMsgs.ServerError(other).Render(Language.En));
    }
}
