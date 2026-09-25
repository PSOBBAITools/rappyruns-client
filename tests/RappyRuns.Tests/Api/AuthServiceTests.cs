using RappyRuns.Core.Api;
using RappyRuns.Core.I18n;

namespace RappyRuns.Tests.Api;

/// <summary>Pairing, login.txt, the anonymous guest and check-token (spec core §8, ui-shell §2).</summary>
public class AuthServiceTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    private static (AuthService Auth, FakeHandler Handler, FakeSettings Settings) Make(
        Func<SeenRequest, (int, string)> respond, FakeSettings? settings = null, string? machine = "PC",
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var (api, handler, s) = Api.Make(respond, settings);
        return (new AuthService(api, s, machine, delay ?? NoWait), handler, s);
    }

    [Fact(DisplayName = "token labels name the machine, or fall back without one")]
    public void Labels()
    {
        Assert.Equal("Desktop client (PC)", AuthService.PairingLabel("PC"));
        Assert.Equal("Desktop client", AuthService.PairingLabel(null));
        Assert.Equal("Desktop client (PC) [login.txt]", AuthService.FileLoginLabel("PC"));
    }

    [Fact(DisplayName = "pairing: opens the approval page, polls through pending and transport errors, saves the token")]
    public async Task PairingCompletes()
    {
        var polls = 0;
        var (auth, handler, settings) = Make(r =>
        {
            if (r.Url.EndsWith("/api/pair", StringComparison.Ordinal)) return (201, """{"code":"AB12","interval":2,"expires_in":10}""");
            return ++polls switch
            {
                1 => (200, "{}"),
                2 => (503, ""),
                _ => (200, """{"token":"eta_new"}"""),
            };
        });
        string? opened = null;
        var waiting = false;
        string? linked = null;
        auth.TokenLinked += t => linked = t;
        var result = await auth.RunPairingAsync(url => opened = url, () => waiting = true);
        Assert.Equal(new PairingResult(PairingOutcome.Completed, "eta_new"), result);
        Assert.Equal("https://s.example/pair?code=AB12", opened);
        Assert.True(waiting);
        Assert.Equal("eta_new", settings.ApiToken);
        Assert.Equal(1, settings.Saves);
        Assert.Equal("eta_new", linked);
        Assert.Equal("""{"label":"Desktop client (PC)"}""", handler.Requests[0].Body);
        Assert.Null(result.StatusText(Language.En));
    }

    [Fact(DisplayName = "pairing: a 404 poll means expired")]
    public async Task PairingGone()
    {
        var (auth, _, settings) = Make(r => r.Method == "POST" ? (201, """{"code":"C"}""") : (404, ""));
        var result = await auth.RunPairingAsync(_ => { });
        Assert.Equal(PairingOutcome.Expired, result.Outcome);
        Assert.Equal("", settings.ApiToken);
        Assert.Contains("pairing expired", result.StatusText(Language.En));
    }

    [Fact(DisplayName = "pairing: polls ceil(expires_in / interval) times, then expires")]
    public async Task PairingRunsOut()
    {
        var waits = new List<TimeSpan>();
        var (auth, handler, _) = Make(r => r.Method == "POST" ? (201, """{"code":"C","interval":3,"expires_in":10}""") : (200, "{}"),
            delay: (d, _) => { waits.Add(d); return Task.CompletedTask; });
        var result = await auth.RunPairingAsync(_ => { });
        Assert.Equal(PairingOutcome.Expired, result.Outcome);
        Assert.Equal(4, handler.Requests.Count(r => r.Method == "GET"));
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(3), w));
    }

    [Fact(DisplayName = "pairing: a token pasted meanwhile wins")]
    public async Task PairingSuperseded()
    {
        FakeSettings? s = null;
        var (auth, handler, settings) = Make(r => r.Method == "POST" ? (201, """{"code":"C"}""") : (200, "{}"),
            delay: (_, _) => { s!.ApiToken = "eta_pasted"; return Task.CompletedTask; });
        s = settings;
        Assert.Equal(PairingOutcome.Superseded, (await auth.RunPairingAsync(_ => { })).Outcome);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "GET");
        Assert.Equal("eta_pasted", settings.ApiToken);
    }

    [Fact(DisplayName = "pairing: a token pasted while the completing poll was out wins (S49)")]
    public async Task PairingSupersededDuringPoll()
    {
        FakeSettings? s = null;
        var linked = false;
        var (auth, _, settings) = Make(r =>
        {
            if (r.Method == "POST") return (201, """{"code":"C"}""");
            s!.ApiToken = "eta_pasted";
            return (200, """{"token":"eta_new"}""");
        });
        s = settings;
        auth.TokenLinked += _ => linked = true;
        Assert.Equal(PairingOutcome.Superseded, (await auth.RunPairingAsync(_ => { })).Outcome);
        Assert.Equal("eta_pasted", settings.ApiToken);
        Assert.False(linked);
    }

    [Fact(DisplayName = "login.txt: a token set while the login was out wins (S49)")]
    public async Task FileLoginSuperseded()
    {
        FakeSettings? s = null;
        var linked = false;
        var (auth, _, settings) = Make(_ =>
        {
            s!.ApiToken = "eta_pasted";
            return (201, """{"token":"eta_new"}""");
        }, new FakeSettings { ApiToken = "eta_old" });
        s = settings;
        auth.TokenLinked += _ => linked = true;
        Assert.Equal(FileLoginOutcome.Superseded, (await auth.LoginWithCredentialsAsync("u", "p")).Outcome);
        Assert.Equal("eta_pasted", settings.ApiToken);
        Assert.False(linked);
    }

    [Fact(DisplayName = "pairing: failing to start reports pairing-failed in red")]
    public async Task PairingFailsToStart()
    {
        var (auth, _, _) = Make(_ => (500, ""));
        var opened = false;
        var result = await auth.RunPairingAsync(_ => opened = true);
        Assert.Equal(PairingOutcome.FailedToStart, result.Outcome);
        Assert.False(opened);
        Assert.True(result.IsError);
        Assert.Equal("Token: pairing could not start (POST /api/pair -> 500)", result.StatusText(Language.En));
    }

    [Fact(DisplayName = "pairing: cancellation ends quietly; only one pairing at a time")]
    public async Task PairingCancelAndSingleWorker()
    {
        var gate = new TaskCompletionSource();
        var (auth, _, _) = Make(r => r.Method == "POST" ? (201, """{"code":"C"}""") : (200, "{}"),
            delay: async (d, ct) => { gate.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        using var cts = new CancellationTokenSource();
        var first = auth.RunPairingAsync(_ => { }, cancellationToken: cts.Token);
        await gate.Task;
        Assert.True(auth.IsPairing);
        Assert.Equal(PairingOutcome.AlreadyRunning, (await auth.RunPairingAsync(_ => { })).Outcome);
        await cts.CancelAsync();
        Assert.Equal(PairingOutcome.Cancelled, (await first).Outcome);
        Assert.False(auth.IsPairing);
    }

    [Fact(DisplayName = "login.txt: ok saves the token with the [login.txt] label")]
    public async Task FileLoginOk()
    {
        var (auth, handler, settings) = Make(_ => (201, """{"token":"eta_file"}"""));
        var linked = 0;
        auth.TokenLinked += _ => linked++;
        var result = await auth.LoginWithCredentialsAsync("u", "p");
        Assert.Equal(FileLoginOutcome.Ok, result.Outcome);
        Assert.Equal("eta_file", settings.ApiToken);
        Assert.Equal(1, linked);
        Assert.Contains("\"label\":\"Desktop client (PC) [login.txt]\"", handler.Requests[0].Body);
        Assert.Null(result.StatusText(Language.En));
    }

    [Fact(DisplayName = "login.txt: unreadable file, rejected password and failures read differently")]
    public async Task FileLoginFailures()
    {
        var (bad, badHandler, _) = Make(_ => (201, """{"token":"x"}"""));
        Assert.Equal(FileLoginOutcome.BadFile, (await bad.LoginWithCredentialsAsync(null, null)).Outcome);
        Assert.Empty(badHandler.Requests);
        var invalid = await Make(_ => (401, "")).Auth.LoginWithCredentialsAsync("u", "p");
        Assert.Equal(FileLoginOutcome.Invalid, invalid.Outcome);
        Assert.Contains("rejected", invalid.StatusText(Language.En));
        var failed = await Make(_ => (500, "")).Auth.LoginWithCredentialsAsync("u", "p");
        Assert.Equal(new FileLoginResult(FileLoginOutcome.Failed, "POST /api/login -> 500"), failed);
        Assert.Equal("Token: login.txt login failed (POST /api/login -> 500)", failed.StatusText(Language.En));
    }

    // ---- ensure-submission-token: the queue's one implementation over the real adapter ----

    private sealed class EnsureRig : IDisposable
    {
        private readonly TempDir _dir = new("rr-ensure");

        public EnsureRig(Func<SeenRequest, (int, string)> respond, string? serverUrl = null, HttpTransport? transport = null)
        {
            Config = RappyRuns.Core.Config.ConfigStore.Open(_dir.Path);
            Config.ServerUrl = serverUrl ?? "https://s.example";
            Handler = new FakeHandler(respond);
            var api = new ApiClient(transport ?? new HttpTransport(Handler), new RappyRuns.Host.ConfigAuthSettings(Config));
            Network = new RappyRuns.Host.QueueNetwork(api, _ => "{}", () => "");
        }

        public RappyRuns.Core.Config.ConfigStore Config { get; }
        public FakeHandler Handler { get; }
        public RappyRuns.Host.QueueNetwork Network { get; }

        public Task<string?> Ensure() =>
            RappyRuns.Core.Store.RunQueue.EnsureSubmissionTokenAsync(Config, Network, "PC");

        public void Dispose()
        {
            _dir.Dispose();
        }
    }

    [Fact(DisplayName = "ensure-submission-token returns an existing token without network")]
    public async Task EnsureExisting()
    {
        using var rig = new EnsureRig(_ => (500, ""));
        rig.Config.AnonToken = "eta_g";
        Assert.Equal("eta_g", await rig.Ensure());
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact(DisplayName = "ensure-submission-token registers a guest once and saves it")]
    public async Task EnsureRegisters()
    {
        using var rig = new EnsureRig(_ => (201, """{"token":"eta_new_guest","username":"guest-9"}"""));
        Assert.Equal("eta_new_guest", await rig.Ensure());
        Assert.Equal("eta_new_guest", rig.Config.AnonToken);
        Assert.Equal("""{"label":"Desktop client (PC) [guest]"}""", rig.Handler.Requests[0].Body);
        Assert.Equal("eta_new_guest", await rig.Ensure());
        Assert.Single(rig.Handler.Requests);
    }

    [Fact(DisplayName = "ensure-submission-token parks the queue when registration is unreachable or refused")]
    public async Task EnsureUnreachable()
    {
        // Port 9 (discard) refuses immediately; no guest token is saved.
        using var offline = new EnsureRig(_ => (500, ""), "http://127.0.0.1:9", new HttpTransport());
        Assert.Null(await offline.Ensure());
        Assert.Equal("", offline.Config.SubmissionToken);
        using var limited = new EnsureRig(_ => (429, """{"error":"rate"}"""));
        Assert.Null(await limited.Ensure());
        Assert.Equal("", limited.Config.AnonToken);
    }

    [Fact(DisplayName = "check-token: unlinked makes no request and revokes Pin Share")]
    public async Task CheckUnlinked()
    {
        var (auth, handler, _) = Make(_ => (200, "{}"), new FakeSettings { AnonToken = "eta_g" });
        var result = await auth.CheckTokenAsync();
        Assert.Equal(TokenCheckKind.Unlinked, result.Kind);
        Assert.Empty(handler.Requests);
        Assert.False(result.PinShareAllowed);
        Assert.Null(result.IsModerator);
        Assert.False(result.IsError);
        Assert.StartsWith("Not linked", result.StatusText(Language.En));
    }

    [Fact(DisplayName = "check-token: ok verifies and applies flags first; the merge is the app's next step (S48)")]
    public async Task CheckOk()
    {
        var order = new List<string>();
        var (auth, handler, settings) = Make(r =>
        {
            order.Add(r.Url);
            return r.Url.EndsWith("/api/me", StringComparison.Ordinal)
                ? (200, """{"username":"tpot","role":"moderator","auto_publish":1,"features":["pinshare"]}""")
                : (200, "{}");
        }, new FakeSettings { ApiToken = " eta_x\n", AnonToken = "eta_g" });
        MeUser? verified = null;
        var result = await auth.CheckTokenAsync(u => { verified = u; order.Add("verified"); });
        Assert.Equal(TokenCheckKind.Ok, result.Kind);
        Assert.Equal(["https://s.example/api/me", "verified"], order);
        Assert.Equal("tpot", verified!.Username);
        Assert.Null(result.Merge);
        Assert.Equal("eta_g", settings.AnonToken); // left for MergeGuestAsync
        Assert.Equal("Bearer eta_x", handler.Requests[0].Authorization);
        Assert.Equal((true, true, true, true), (result.PinShareAllowed!.Value, result.IsModerator!.Value, result.AutoPublish!.Value, result.RetryQueue));
        Assert.Equal("Token: OK (tpot)", result.StatusText(Language.En));
        Assert.Equal("Token OK - authenticated as tpot.", result.DialogText(Language.En));
    }

    [Fact(DisplayName = "guest merge: merges into the given token's account and clears the guest")]
    public async Task MergeGuestOk()
    {
        var (auth, handler, settings) = Make(_ => (200, "{}"), new FakeSettings { ApiToken = "eta_x", AnonToken = "eta_g" });
        Assert.Equal(MergeResult.Ok, await auth.MergeGuestAsync("eta_x"));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://s.example/api/merge-anonymous", request.Url);
        Assert.Equal("Bearer eta_x", request.Authorization);
        Assert.Equal("", settings.AnonToken);
        Assert.Equal(1, settings.Saves);
    }

    [Fact(DisplayName = "guest merge: a gone guest is dropped too; a transport error keeps it")]
    public async Task MergeGuestOutcomes()
    {
        var (gone, _, goneSettings) = Make(_ => (404, ""), new FakeSettings { ApiToken = "eta_x", AnonToken = "eta_g" });
        Assert.Equal(MergeResult.Gone, await gone.MergeGuestAsync("eta_x"));
        Assert.Equal("", goneSettings.AnonToken);

        var (failing, _, failingSettings) = Make(_ => (500, ""), new FakeSettings { ApiToken = "eta_x", AnonToken = "eta_g" });
        Assert.Null(await failing.MergeGuestAsync("eta_x"));
        Assert.Equal("eta_g", failingSettings.AnonToken);
        Assert.Equal(0, failingSettings.Saves);
    }

    [Fact(DisplayName = "guest merge: a guest registered while the merge was out is kept (S48)")]
    public async Task MergeGuestKeepsNewGuest()
    {
        var settings = new FakeSettings { ApiToken = "eta_x", AnonToken = "eta_g" };
        var (auth, _, _) = Make(_ =>
        {
            settings.AnonToken = "eta_g2"; // the queue registered a fresh guest meanwhile
            return (200, "{}");
        }, settings);
        Assert.Equal(MergeResult.Ok, await auth.MergeGuestAsync("eta_x"));
        Assert.Equal("eta_g2", settings.AnonToken);
    }

    [Fact(DisplayName = "guest merge: no guest, no request")]
    public async Task MergeNoGuest()
    {
        var (auth, handler, _) = Make(_ => (200, "{}"), new FakeSettings { ApiToken = "eta_x" });
        Assert.Null(await auth.MergeGuestAsync("eta_x"));
        Assert.Empty(handler.Requests);
    }

    [Fact(DisplayName = "check-token: flags follow the features and role")]
    public async Task CheckFlags()
    {
        var (auth, handler, _) = Make(_ => (200, """{"username":"u","features":[]}"""), new FakeSettings { ApiToken = "eta_x" });
        var result = await auth.CheckTokenAsync();
        Assert.Single(handler.Requests);
        Assert.False(result.PinShareAllowed);
        Assert.False(result.IsModerator);
    }

    [Fact(DisplayName = "check-token: 401 is invalid, revokes Pin Share and asks for the login.txt relogin")]
    public async Task CheckUnauthorized()
    {
        var (auth, _, _) = Make(_ => (401, ""), new FakeSettings { ApiToken = "eta_x", AnonToken = "eta_g" });
        var result = await auth.CheckTokenAsync(_ => throw new InvalidOperationException("not on 401"));
        Assert.Equal(TokenCheckKind.Unauthorized, result.Kind);
        Assert.True(result.ShouldRelogin);
        Assert.True(result.IsError);
        Assert.False(result.PinShareAllowed);
        Assert.Null(result.IsModerator);
        Assert.False(result.RetryQueue);
        Assert.Equal("Token: invalid or revoked", result.StatusText(Language.En));
        Assert.StartsWith("The server rejected", result.DialogText(Language.En));
    }

    [Fact(DisplayName = "check-token: transport errors do not doubt the token and keep the Pin Share verdict")]
    public async Task CheckError()
    {
        var (auth, _, _) = Make(_ => (502, ""), new FakeSettings { ApiToken = "eta_x" });
        var result = await auth.CheckTokenAsync();
        Assert.Equal(TokenCheckKind.Error, result.Kind);
        Assert.Null(result.PinShareAllowed);
        Assert.Null(result.IsModerator);
        Assert.False(result.ShouldRelogin);
        Assert.Null(result.DialogText(Language.En));
        Assert.Equal("Token: could not verify (GET /api/me -> 502)", result.StatusText(Language.En));
    }

    [Fact(DisplayName = "pin share refresh: verdict on 200/401, keep on errors, stale answers dropped")]
    public async Task PinShareRefresh()
    {
        Assert.True(await Make(_ => (200, """{"features":["pinshare"]}"""), new FakeSettings { ApiToken = "t" }).Auth.RefreshPinShareAllowedAsync());
        Assert.False(await Make(_ => (200, """{"features":[]}"""), new FakeSettings { ApiToken = "t" }).Auth.RefreshPinShareAllowedAsync());
        Assert.False(await Make(_ => (401, ""), new FakeSettings { ApiToken = "t" }).Auth.RefreshPinShareAllowedAsync());
        Assert.Null(await Make(_ => (500, ""), new FakeSettings { ApiToken = "t" }).Auth.RefreshPinShareAllowedAsync());
        Assert.Null(await Make(_ => (200, "{}")).Auth.RefreshPinShareAllowedAsync());
        FakeSettings? s = null;
        var (stale, _, settings) = Make(_ => { s!.ApiToken = "other"; return (200, """{"features":["pinshare"]}"""); },
            new FakeSettings { ApiToken = "t" });
        s = settings;
        Assert.Null(await stale.RefreshPinShareAllowedAsync());
    }
}
