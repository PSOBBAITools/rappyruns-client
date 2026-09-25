using System.Text.Json;
using RappyRuns.Core.Api;
using RappyRuns.Core.PinShare;
using RappyRuns.Core.Sexp;
using RappyRuns.Host;

namespace RappyRuns.Tests.Host;

/// <summary>
/// The ordering ClientHost promises while its state is written from several
/// threads (S32): each test picks the completion order with held HTTP
/// answers or a hook inside a concurrent writer, and pins what the UI must
/// end up seeing.
/// </summary>
public sealed class HostOrderingTests
{
    // Alice is a moderator with auto-publish on: a stale check that leaked
    // would change the role and the mirror, not only the token line.
    private const string MeA = """{"username":"alice","role":"moderator","auto_publish":1,"features":["pinshare"]}""";
    private const string MeB = """{"username":"bob","role":"user","auto_publish":0,"features":["pinshare"]}""";

    private static bool Me(string url) => url.EndsWith("/api/me", StringComparison.Ordinal);

    // /api/me waits for the test; everything else is a quick 404.
    private static GatedHandler HoldMe() => new((url, _) => Me(url) ? null : (404, ""));

    [Theory(DisplayName = "ordering: a token check that finishes after a newer one changes nothing, whatever it got (S32)")]
    [InlineData(401, "")]
    [InlineData(200, MeA)]
    public async Task StaleTokenCheckLoses(int staleStatus, string staleBody)
    {
        using var h = new HostHarness(HoldMe(), c => c.ApiToken = "token-a");
        h.CallOrdered("app.hello");
        var first = h.Host.CheckTokenAsync();
        var firstAnswer = h.Http.Take((url, auth) => Me(url) && auth == "Bearer token-a");
        h.Config.ApiToken = "token-b";
        var second = h.Host.CheckTokenAsync();
        var secondAnswer = h.Http.Take((url, auth) => Me(url) && auth == "Bearer token-b");

        // The newer check lands first, then the old token's answer.
        secondAnswer.SetResult((200, MeB));
        Assert.Equal(TokenCheckKind.Ok, (await second)!.Kind);
        firstAnswer.SetResult((staleStatus, staleBody));
        Assert.Null(await first);

        AssertBobApplied(h);
        // The UI saw the same: its last token patch is bob's.
        var tokens = h.Events.Where(e => e.Name == "state" && e.Json.Contains("\"token\"", StringComparison.Ordinal)).ToList();
        Assert.Contains("bob", tokens[^1].Json, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ordering: a token check whose token changed before its answer applies nothing (S32)")]
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
        // Alice's verdict, role and auto-publish never applied to token-b.
        Assert.False(h.Host.Permission.Allowed);
        Assert.False(h.Config.Moderator);
        Assert.False(h.Host.Ui.Moderator);
        Assert.False(h.Config.AutoPublish);
        Assert.DoesNotContain("alice", Json(h.Host.Ui.Token), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ordering: a login.txt login that fails after a newer token check leaves that check's line (S49)")]
    public async Task FileLoginLosesToNewerCheck()
    {
        using var h = new HostHarness(new GatedHandler((url, _) => Me(url) || url.EndsWith("/api/login", StringComparison.Ordinal) ? null : (404, "")),
            c => c.ApiToken = "token-a");
        File.WriteAllText(Path.Combine(h.Dir, "login.txt"), "username=alice\npassword=wrong\n");
        h.CallOrdered("app.hello");
        var login = h.Host.StartFileLogin();
        var loginAnswer = h.Http.Take((url, _) => url.EndsWith("/api/login", StringComparison.Ordinal));
        // Meanwhile the player pastes bob's token and saves: a newer check.
        h.Config.ApiToken = "token-b";
        var check = h.Host.CheckTokenAsync();
        h.Http.Take((url, auth) => Me(url) && auth == "Bearer token-b").SetResult((200, MeB));
        Assert.Equal(TokenCheckKind.Ok, (await check)!.Kind);
        loginAnswer.SetResult((401, ""));
        await login;
        Assert.Equal(Json(Line.Ok(Msg.Of("token-ok", "bob"))), Json(h.Host.Ui.Token));
    }

    [Fact(DisplayName = "ordering: a login.txt login that succeeds after the player set another token keeps that token (S49)")]
    public async Task FileLoginSuccessKeepsNewerToken()
    {
        using var h = new HostHarness(new GatedHandler((url, _) => Me(url) || url.EndsWith("/api/login", StringComparison.Ordinal) ? null : (404, "")),
            c => c.ApiToken = "token-a");
        File.WriteAllText(Path.Combine(h.Dir, "login.txt"), "username=alice\npassword=right\n");
        h.CallOrdered("app.hello");
        var login = h.Host.StartFileLogin();
        var loginAnswer = h.Http.Take((url, _) => url.EndsWith("/api/login", StringComparison.Ordinal));
        h.Config.ApiToken = "token-b";
        var check = h.Host.CheckTokenAsync();
        h.Http.Take((url, auth) => Me(url) && auth == "Bearer token-b").SetResult((200, MeB));
        Assert.Equal(TokenCheckKind.Ok, (await check)!.Kind);
        loginAnswer.SetResult((201, """{"token":"token-alice"}"""));
        await login;
        Assert.Equal("token-b", h.Config.ApiToken);
        Assert.Equal(Json(Line.Ok(Msg.Of("token-ok", "bob"))), Json(h.Host.Ui.Token));
    }

    [Fact(DisplayName = "ordering: a login.txt login with nothing newer still shows its failure (S49)")]
    public async Task FileLoginShowsOwnFailure()
    {
        using var h = new HostHarness(new GatedHandler((url, _) => url.EndsWith("/api/login", StringComparison.Ordinal) ? (401, "") : (404, "")),
            c => c.ApiToken = "token-a");
        File.WriteAllText(Path.Combine(h.Dir, "login.txt"), "username=alice\npassword=wrong\n");
        h.CallOrdered("app.hello");
        await h.Host.StartFileLogin();
        Assert.Equal(Json(Line.Error(Msg.Of("file-login-invalid"))), Json(h.Host.Ui.Token));
    }

    [Fact(DisplayName = "ordering: a pairing that fails after a newer token check leaves that check's line (S49)")]
    public async Task PairingLosesToNewerCheck()
    {
        using var h = new HostHarness(new GatedHandler((url, _) => Me(url) || url.EndsWith("/api/pair", StringComparison.Ordinal) ? null : (404, "")));
        h.CallOrdered("app.hello");
        var pairing = h.Host.StartPairing();
        var pairAnswer = h.Http.Take((url, _) => url.EndsWith("/api/pair", StringComparison.Ordinal));
        h.Config.ApiToken = "token-b"; // pasted in Settings instead of waiting for the browser
        var check = h.Host.CheckTokenAsync();
        h.Http.Take((url, auth) => Me(url) && auth == "Bearer token-b").SetResult((200, MeB));
        Assert.Equal(TokenCheckKind.Ok, (await check)!.Kind);
        pairAnswer.SetResult((500, ""));
        await pairing;
        Assert.Equal(Json(Line.Ok(Msg.Of("token-ok", "bob"))), Json(h.Host.Ui.Token));
    }

    [Fact(DisplayName = "ordering: no runs list reaches the UI before the hello reply, and none is lost after it (S32)")]
    public void RunsNeverOvertakeHello()
    {
        using var h = new HostHarness();
        const int total = 200;
        // Hello arrives from inside the writer's stream of changes, halfway.
        var enqueued = 0;
        h.Host.Queue.Changed += (_, _) =>
        {
            if (Interlocked.Increment(ref enqueued) == total / 2) h.CallOrdered("app.hello");
        };
        var writer = new Thread(() =>
        {
            for (var i = 0; i < total; i++) h.Host.Queue.Enqueue(new Plist().With("QUEST-SLUG", $"q{i}"));
        });
        writer.Start();
        writer.Join();

        var events = h.Events;
        var hello = events.FindIndex(e => e.Name == "reply:app.hello");
        Assert.True(hello >= 0);
        Assert.DoesNotContain(events.Take(hello), e => e.Name == "runs");
        // Every list after the reply is newer than the reply's, and the last one is complete.
        var counts = new List<int> { JsonDocument.Parse(events[hello].Json).RootElement.GetProperty("runs").GetArrayLength() };
        counts.AddRange(events.Skip(hello + 1).Where(e => e.Name == "runs").Select(e => JsonDocument.Parse(e.Json).RootElement.GetArrayLength()));
        Assert.True(counts[0] >= total / 2);
        Assert.Equal(counts.Order(), counts);
        Assert.Equal(total, counts[^1]);
    }

    [Fact(DisplayName = "ordering: the runs list and a state patch made before hello are inside the reply, not after it (S32)")]
    public void ChangesBeforeHelloAreInTheReply()
    {
        using var h = new HostHarness();
        h.Host.Queue.Enqueue(new Plist().With("QUEST-SLUG", "early"));
        h.Host.Ui.SetServer(Line.Ok(Msg.Of("server-ok", 1, 0, null)));
        Assert.Empty(h.Events); // nothing goes out before the handshake
        h.CallOrdered("app.hello");
        var (name, json) = Assert.Single(h.Events);
        Assert.Equal("reply:app.hello", name);
        Assert.Contains("early", json, StringComparison.Ordinal);
        Assert.Contains("server-ok", json, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ordering: the Pin Share line follows the relay's status for each saved setting, in order (S32)")]
    public async Task PinShareLineFollowsSettings()
    {
        using var h = new HostHarness(configure: c =>
        {
            c.PinshareAllowed = true;
            c.PinshareChannel = "secret";
        });
        h.CallOrdered("app.hello");
        h.Host.PinShare.Start();
        var off = Json(StatusMsgs.PinShare(new PinShareStatus(PinShareStatusKind.Off)));
        var waiting = Json(StatusMsgs.PinShare(new PinShareStatus(PinShareStatusKind.WaitingGame)));
        for (var i = 0; i < 3; i++)
        {
            // Each change is seen by the relay (it re-reads the settings every
            // second) and reaches the host's line and the UI before the next.
            h.Config.PinshareEnabled = true;
            await HostHarness.WaitFor(() => Json(h.Host.Ui.Pinshare) == waiting);
            h.Config.PinshareEnabled = false;
            await HostHarness.WaitFor(() => Json(h.Host.Ui.Pinshare) == off);
        }
        var lines = h.Events.Where(e => e.Name == "state" && e.Json.Contains("\"pinshare\"", StringComparison.Ordinal))
            .Select(e => e.Json.Contains("pinshare-status-waiting-game", StringComparison.Ordinal) ? "waiting" : "off")
            .ToList();
        Assert.Equal(["waiting", "off", "waiting", "off", "waiting", "off"], lines);
    }

    private static void AssertBobApplied(HostHarness h)
    {
        Assert.Equal(Json(Line.Ok(Msg.Of("token-ok", "bob"))), Json(h.Host.Ui.Token));
        Assert.True(h.Host.Permission.Allowed); // a stale 401 did not switch Pin Share off
        Assert.False(h.Config.Moderator);       // nor did a stale 200 make bob a moderator
        Assert.False(h.Config.AutoPublish);
    }

    private static string Json(object value) => IpcJson.Serialize(value);
}
