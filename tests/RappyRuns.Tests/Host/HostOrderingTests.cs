using System.Text.Json;
using RappyRuns.Core.Api;
using RappyRuns.Core.PinShare;
using RappyRuns.Core.Sexp;
using RappyRuns.Host;

namespace RappyRuns.Tests.Host;

/// <summary>
/// The ordering ClientHost promises while its state is written from several
/// threads (S32): each test picks the completion order with held HTTP
/// answers or concurrent writers, and pins what the UI must end up seeing.
/// </summary>
public sealed class HostOrderingTests
{
    private const string MeA = """{"username":"alice","role":"user","auto_publish":0,"features":["pinshare"]}""";
    private const string MeB = """{"username":"bob","role":"user","auto_publish":0,"features":["pinshare"]}""";

    private static bool Me(string url) => url.EndsWith("/api/me", StringComparison.Ordinal);

    // /api/me waits for the test; everything else is a quick 404.
    private static GatedHandler HoldMe() => new((url, _) => Me(url) ? null : (404, ""));

    [Fact(DisplayName = "ordering: a token check that finishes after a newer one changes nothing (S32)")]
    public async Task StaleTokenCheckLoses()
    {
        using var h = new HostHarness(HoldMe(), c => c.ApiToken = "token-a");
        h.CallOrdered("app.hello");
        var first = h.Host.CheckTokenAsync();
        var firstAnswer = h.Http.Take((url, auth) => Me(url) && auth == "Bearer token-a");
        h.Config.ApiToken = "token-b";
        var second = h.Host.CheckTokenAsync();
        var secondAnswer = h.Http.Take((url, auth) => Me(url) && auth == "Bearer token-b");

        // The newer check lands first, then the old token's 401.
        secondAnswer.SetResult((200, MeB));
        Assert.Equal(TokenCheckKind.Ok, (await second)!.Kind);
        firstAnswer.SetResult((401, ""));
        Assert.Null(await first);

        Assert.Equal(Json(Line.Ok(Msg.Of("token-ok", "bob"))), Json(h.Host.Ui.Token));
        Assert.True(h.Host.Permission.Allowed); // the stale 401 did not switch Pin Share off
        Assert.Contains(h.Services.Lines, l => l == "token check: a superseded Unauthorized result was ignored");
        // The UI saw the same: its last token patch is bob's.
        var tokens = h.Sink.Events.Where(e => e.Name == "state" && e.Json.Contains("\"token\"", StringComparison.Ordinal)).ToList();
        Assert.Contains("bob", tokens[^1].Json, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ordering: a token check whose token changed meanwhile applies nothing, even when it finishes last (S32)")]
    public async Task TokenChangedDuringCheck()
    {
        using var h = new HostHarness(HoldMe(), c =>
        {
            c.ApiToken = "token-a";
            c.PinshareAllowed = false;
        });
        h.CallOrdered("app.hello");
        var check = h.Host.CheckTokenAsync();
        var answer = h.Http.Take((url, _) => Me(url));
        h.Config.ApiToken = "token-b"; // changed without a new check (yet)
        answer.SetResult((200, MeA));
        Assert.Null(await check);
        Assert.False(h.Host.Permission.Allowed); // alice's verdict never applied to token-b
        Assert.DoesNotContain("alice", Json(h.Host.Ui.Token), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ordering: no runs list reaches the UI before the hello reply, and none is lost after it (S32)")]
    public void RunsNeverOvertakeHello()
    {
        using var h = new HostHarness();
        const int total = 200;
        using var go = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            go.Wait();
            for (var i = 0; i < total; i++) h.Host.Queue.Enqueue(new Plist().With("QUEST-SLUG", $"q{i}"));
        });
        writer.Start();
        go.Set();
        Thread.Sleep(1); // let the writer get going
        h.CallOrdered("app.hello");
        writer.Join();

        var events = h.Sink.Events;
        var hello = events.FindIndex(e => e.Name == "reply:app.hello");
        Assert.True(hello >= 0);
        Assert.DoesNotContain(events.Take(hello), e => e.Name == "runs");
        // Every list after the reply is newer than the reply's, and the last one is complete.
        var counts = new List<int> { RunCount(JsonDocument.Parse(events[hello].Json).RootElement.GetProperty("runs")) };
        counts.AddRange(events.Skip(hello + 1).Where(e => e.Name == "runs").Select(e => RunCount(JsonDocument.Parse(e.Json).RootElement)));
        Assert.Equal(counts.Order(), counts);
        Assert.Equal(total, counts[^1]);
    }

    [Fact(DisplayName = "ordering: the runs list and a state patch made before hello are inside the reply, not after it (S32)")]
    public void ChangesBeforeHelloAreInTheReply()
    {
        using var h = new HostHarness();
        h.Host.Queue.Enqueue(new Plist().With("QUEST-SLUG", "early"));
        h.Host.Ui.SetServer(Line.Ok(Msg.Of("server-ok", 1, 0, null)));
        Assert.Empty(h.Sink.Events); // nothing goes out before the handshake
        h.CallOrdered("app.hello");
        var (name, json) = Assert.Single(h.Sink.Events);
        Assert.Equal("reply:app.hello", name);
        Assert.Contains("early", json, StringComparison.Ordinal);
        Assert.Contains("server-ok", json, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ordering: the Pin Share line settles on the relay's status for the settings last saved (S32)")]
    public async Task PinShareLineFollowsSettings()
    {
        using var h = new HostHarness(HoldMe(), c =>
        {
            c.PinshareAllowed = true;
            c.PinshareChannel = "secret";
        });
        h.CallOrdered("app.hello");
        h.Host.PinShare.Start();
        for (var i = 0; i < 6; i++)
        {
            h.Config.PinshareEnabled = i % 2 == 0;
            await Task.Delay(30);
        }
        h.Config.PinshareEnabled = true; // on, but no game: waiting for the game
        var want = StatusMsgs.PinShare(new PinShareStatus(PinShareStatusKind.WaitingGame));
        await WaitFor(() => Json(h.Host.Ui.Pinshare) == Json(want));
        // The UI's copy agrees with the host's once the patches are in.
        var last = h.Sink.Events.Last(e => e.Name == "state" && e.Json.Contains("\"pinshare\"", StringComparison.Ordinal));
        Assert.Contains("pinshare-status-waiting-game", last.Json, StringComparison.Ordinal);
    }

    private static int RunCount(JsonElement runs) => runs.GetArrayLength();

    private static string Json(object value) => IpcJson.Serialize(value);

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }
}
