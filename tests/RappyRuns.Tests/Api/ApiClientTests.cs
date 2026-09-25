using System.Text.Json;
using System.Text.Json.Nodes;
using RappyRuns.Core.Api;

namespace RappyRuns.Tests.Api;

/// <summary>
/// The status → outcome mapping of every endpoint (spec core §7, a contract) and the
/// request shapes, against an in-memory server.
/// </summary>
public class ApiClientTests
{
    private static readonly FakeSettings Linked = new() { ApiToken = " eta_linked\r\n", AnonToken = "eta_guest" };

    [Fact(DisplayName = "requests carry the ephinea-ta-client User-Agent, JSON content type and bearer token")]
    public async Task RequestShape()
    {
        var (api, handler, _) = Api.Make(201, """{"id":1}""", new FakeSettings { ServerUrl = "https://s.example/", ApiToken = "eta_x" });
        await api.SubmitRunAsync("""{"quest":"q"}""");
        var r = Assert.Single(handler.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://s.example/api/runs", r.Url);
        Assert.Equal(HttpTransport.UserAgent, r.UserAgent);
        Assert.Equal("application/json", r.ContentType);
        Assert.Equal("Bearer eta_x", r.Authorization);
        Assert.Equal("""{"quest":"q"}""", r.Body);
    }

    [Fact(DisplayName = "a GET without a body sends no content type and no token when none is set")]
    public async Task GetWithoutBody()
    {
        var (api, handler, _) = Api.Make(200, "[]");
        await api.FetchQuestsAsync();
        var r = Assert.Single(handler.Requests);
        Assert.Null(r.ContentType);
        Assert.Null(r.Authorization);
        Assert.Null(r.Body);
    }

    [Fact(DisplayName = "GET /api/quests: 200 -> array, else api-error")]
    public async Task FetchQuests()
    {
        var quests = await Api.Make(200, """[{"slug":"a"}]""").Client.FetchQuestsAsync();
        Assert.Equal("a", (string?)quests[0]!["slug"]);
        var ex = await Assert.ThrowsAsync<ApiException>(() => Api.Make(500).Client.FetchQuestsAsync());
        Assert.Equal("GET /api/quests -> 500", ex.Message);
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(200, "<html>").Client.FetchQuestsAsync());
    }

    [Fact(DisplayName = "POST /api/pair: 201 + code -> code with interval/expiry defaults")]
    public async Task StartPairing()
    {
        var (api, handler, _) = Api.Make(201, """{"code":"AB12"}""");
        var start = await api.StartPairingAsync("Desktop client (PC)");
        Assert.Equal(new PairingStart("AB12", 2, 600), start);
        Assert.Equal("""{"label":"Desktop client (PC)"}""", handler.Requests[0].Body);
        Assert.Equal(new PairingStart("C", 5, 60),
            await Api.Make(201, """{"code":"C","interval":5,"expires_in":60}""").Client.StartPairingAsync(null));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(200, """{"code":"C"}""").Client.StartPairingAsync(null));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(201, """{"code":5}""").Client.StartPairingAsync(null));
        var (noLabel, h2, _) = Api.Make(201, """{"code":"C"}""");
        await noLabel.StartPairingAsync(null);
        Assert.Equal("{}", h2.Requests[0].Body);
    }

    [Fact(DisplayName = "GET /api/pair/{code}: complete / pending / gone / api-error")]
    public async Task PollPairing()
    {
        Assert.Equal(new PairingPoll(PairingPollStatus.Complete, "eta_t"),
            await Api.Make(200, """{"token":"eta_t"}""").Client.PollPairingAsync("C"));
        Assert.Equal(new PairingPoll(PairingPollStatus.Pending), await Api.Make(200, """{"status":"pending"}""").Client.PollPairingAsync("C"));
        Assert.Equal(new PairingPoll(PairingPollStatus.Pending), await Api.Make(200, "").Client.PollPairingAsync("C"));
        Assert.Equal(new PairingPoll(PairingPollStatus.Gone), await Api.Make(404).Client.PollPairingAsync("C"));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(500).Client.PollPairingAsync("C"));
        var (api, handler, _) = Api.Make(404);
        await api.PollPairingAsync("AB12");
        Assert.Equal("https://s.example/api/pair/AB12", handler.Requests[0].Url);
    }

    [Fact(DisplayName = "POST /api/login: 201 + token ok, 401 unauthorized, else api-error")]
    public async Task Login()
    {
        var (api, handler, _) = Api.Make(201, """{"token":"eta_t"}""");
        Assert.Equal(new LoginResult(LoginStatus.Ok, "eta_t"), await api.LoginAsync("u", "p=q", "L"));
        var body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("u", (string?)body["username"]);
        Assert.Equal("p=q", (string?)body["password"]);
        Assert.Equal("L", (string?)body["label"]);
        Assert.Equal(new LoginResult(LoginStatus.Unauthorized), await Api.Make(401).Client.LoginAsync("u", "p", null));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(201, "{}").Client.LoginAsync("u", "p", null));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(429).Client.LoginAsync("u", "p", null));
    }

    [Fact(DisplayName = "POST /api/register-anonymous: 201 + token -> guest, anything else api-error")]
    public async Task RegisterAnonymous()
    {
        Assert.Equal(new AnonymousRegistration("eta_g", "guest-1"),
            await Api.Make(201, """{"token":"eta_g","username":"guest-1"}""").Client.RegisterAnonymousAsync("L"));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(429, """{"token":"eta_g"}""").Client.RegisterAnonymousAsync("L"));
    }

    [Fact(DisplayName = "POST /api/merge-anonymous: 200 ok, 404 gone, else api-error; linked token")]
    public async Task MergeAnonymous()
    {
        var (api, handler, _) = Api.Make(200, "{}", Linked);
        Assert.Equal(MergeResult.Ok, await api.MergeAnonymousAsync("eta_guest"));
        Assert.Equal("Bearer eta_linked", handler.Requests[0].Authorization);
        Assert.Equal("""{"anonymous_token":"eta_guest"}""", handler.Requests[0].Body);
        Assert.Equal(MergeResult.Gone, await Api.Make(404).Client.MergeAnonymousAsync("g", "t"));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(500).Client.MergeAnonymousAsync("g", "t"));
    }

    [Fact(DisplayName = "GET /api/me: 200 user, 401 unauthorized, else api-error")]
    public async Task FetchMe()
    {
        var me = await Api.Make(200, """{"username":"tpot","role":"admin","auto_publish":1,"features":["pinshare"]}""").Client
            .FetchMeAsync("t");
        Assert.False(me.Unauthorized);
        Assert.Equal("tpot", me.User!.Username);
        Assert.True(me.User.IsModerator);
        Assert.True(me.User.AutoPublish);
        Assert.True(me.User.PinShareAllowed);
        Assert.True((await Api.Make(401).Client.FetchMeAsync("t")).Unauthorized);
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(502).Client.FetchMeAsync("t"));
    }

    [Fact(DisplayName = "POST /api/me/auto-publish sends 0/1 integers; non-200 raises")]
    public async Task AutoPublish()
    {
        var (api, handler, _) = Api.Make(200, "{}", Linked);
        await api.UpdateAutoPublishAsync(true);
        await api.UpdateAutoPublishAsync(false);
        Assert.Equal("""{"enabled":1}""", handler.Requests[0].Body);
        Assert.Equal("""{"enabled":0}""", handler.Requests[1].Body);
        var ex = await Assert.ThrowsAsync<ApiException>(() => Api.Make(401).Client.UpdateAutoPublishAsync(true, "t"));
        Assert.Equal("Invalid or revoked API token", ex.Message);
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(500).Client.UpdateAutoPublishAsync(true, "t"));
    }

    [Theory(DisplayName = "POST /api/runs status mapping")]
    [InlineData(201, SubmitOutcome.Created)]
    [InlineData(200, SubmitOutcome.Duplicate)]
    [InlineData(400, SubmitOutcome.Rejected)]
    [InlineData(403, SubmitOutcome.Rejected)]
    public async Task SubmitRunOutcomes(int status, SubmitOutcome expected) =>
        Assert.Equal(expected, (await Api.Make(status, """{"id":1}""").Client.SubmitRunAsync("{}")).Outcome);

    [Fact(DisplayName = "POST /api/runs 401 and other statuses are api-errors")]
    public async Task SubmitRunErrors()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Api.Make(401).Client.SubmitRunAsync("{}"));
        Assert.Equal("Invalid or revoked API token", ex.Message);
        ex = await Assert.ThrowsAsync<ApiException>(() => Api.Make(500, "oops").Client.SubmitRunAsync("{}"));
        Assert.Equal("POST /api/runs -> 500: oops", ex.Message);
    }

    [Fact(DisplayName = "POST /api/runs uses the submission token (guest when unlinked)")]
    public async Task SubmitRunGuestToken()
    {
        var (api, handler, _) = Api.Make(201, "{}", new FakeSettings { AnonToken = "eta_g" });
        await api.SubmitRunAsync("{}");
        Assert.Equal("Bearer eta_g", handler.Requests[0].Authorization);
        var (linked, h2, _) = Api.Make(201, "{}", Linked);
        await linked.SubmitRunAsync("{}");
        Assert.Equal("Bearer eta_linked", h2.Requests[0].Authorization);
    }

    private static SubmitRunResult Parse(SubmitOutcome outcome, string json) =>
        SubmitRunResult.FromPayload(outcome, JsonNode.Parse(json));

    [Fact(DisplayName = "created runs remember their server id")]
    public void CreatedServerId()
    {
        var r = Parse(SubmitOutcome.Created, """{"id":42,"url":"https://x/runs/42"}""");
        Assert.Equal((42L, "https://x/runs/42"), (r.ServerId!.Value, r.Url));
        Assert.Null(r.Standing);
        Assert.Null(r.Reason);
    }

    [Fact(DisplayName = "duplicate runs remember their server id too")]
    public void DuplicateServerId() => Assert.Equal(42, Parse(SubmitOutcome.Duplicate, """{"id":42}""").ServerId);

    [Fact(DisplayName = "created runs carry the board rank and party count")]
    public void CreatedRank()
    {
        var s = Parse(SubmitOutcome.Created,
            """{"id":7,"standing":{"rank":2,"parties":5,"previous_best_ms":605000,"delta_ms":-3210}}""").Standing!;
        Assert.Equal((2L, 5L), (s.Rank, s.Parties));
    }

    [Fact(DisplayName = "created runs carry the personal-best delta and previous time")]
    public void CreatedDelta()
    {
        var s = Parse(SubmitOutcome.Created,
            """{"id":7,"standing":{"rank":2,"parties":5,"previous_best_ms":605000,"delta_ms":-3210}}""").Standing!;
        Assert.Equal((-3210L, 605000L), (s.DeltaMs!.Value, s.PreviousBestMs!.Value));
    }

    [Fact(DisplayName = "a submission with no standing block adds no standing keys")]
    public void NoStanding() => Assert.Null(Parse(SubmitOutcome.Created, """{"id":7}""").Standing);

    [Fact(DisplayName = "a first-time standing carries a rank but no delta")]
    public void FirstTimeStanding()
    {
        var s = Parse(SubmitOutcome.Created, """{"standing":{"rank":1,"parties":1}}""").Standing!;
        Assert.Equal(1, s.Rank);
        Assert.Null(s.DeltaMs);
    }

    [Fact(DisplayName = "rejected runs carry a reason, not a server id")]
    public void RejectedReason()
    {
        var r = Parse(SubmitOutcome.Rejected, """{"message":"nope","id":3}""");
        Assert.Null(r.ServerId);
        Assert.Contains("nope", r.Reason);
    }

    [Fact(DisplayName = "submission-updates parsing matches the Lisp golden (reasons, standings)")]
    public void SubmissionGolden()
    {
        var golden = Golden.Load("api/api-golden.json");
        foreach (var c in golden.GetProperty("reasons").EnumerateArray())
            Assert.Equal(c.GetProperty("reason").GetString(), Parse(SubmitOutcome.Rejected, c.GetProperty("body").GetString()!).Reason);
        static long? L(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt64();
        foreach (var c in golden.GetProperty("standings").EnumerateArray())
        {
            var r = Parse(SubmitOutcome.Created, c.GetProperty("body").GetString()!);
            var url = c.GetProperty("url");
            Assert.Equal(L(c.GetProperty("server_id")), r.ServerId);
            Assert.Equal(url.ValueKind == JsonValueKind.Null ? null : url.GetString(), r.Url);
            Assert.Equal(L(c.GetProperty("rank")), r.Standing?.Rank);
            Assert.Equal(L(c.GetProperty("parties")), r.Standing?.Parties);
            Assert.Equal(L(c.GetProperty("prev")), r.Standing?.PreviousBestMs);
            Assert.Equal(L(c.GetProperty("delta")), r.Standing?.DeltaMs);
        }
    }

    [Theory(DisplayName = "POST /api/quests status mapping")]
    [InlineData(201, QuestRuleOutcome.Created)]
    [InlineData(409, QuestRuleOutcome.Duplicate)]
    [InlineData(403, QuestRuleOutcome.Forbidden)]
    [InlineData(400, QuestRuleOutcome.Rejected)]
    public async Task QuestRuleOutcomes(int status, QuestRuleOutcome expected) =>
        Assert.Equal(expected, (await Api.Make(status, "{}").Client.CreateQuestRuleAsync("p", "n", "d")).Outcome);

    [Fact(DisplayName = "POST /api/quests sends parent/name/description and the trigger objects")]
    public async Task QuestRuleBody()
    {
        var (api, handler, _) = Api.Make(201, "{}", Linked);
        await api.CreateQuestRuleAsync("parent-slug", "Name", "Desc", TriggerJson.FromParts("monster-dead", 1234),
            TriggerJson.FromParts("warp-in"));
        var body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("parent-slug", (string?)body["parent"]);
        Assert.Equal(1234, (long)body["end"]!["monster"]!);
        Assert.Equal("warp-in", (string?)body["start"]!["type"]);
        Assert.Equal("Bearer eta_linked", handler.Requests[0].Authorization);
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(401).Client.CreateQuestRuleAsync("p", "n", "d"));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(500).Client.CreateQuestRuleAsync("p", "n", "d"));
        var (noTriggers, h2, _) = Api.Make(201, "{}");
        await noTriggers.CreateQuestRuleAsync("p", "n", "d");
        Assert.False(JsonNode.Parse(h2.Requests[0].Body!)!.AsObject().ContainsKey("end"));
    }

    [Fact(DisplayName = "ghost fetch: 200 ok with the raw body, 404 none, 401/other api-error")]
    public async Task GhostFetch()
    {
        var (api, handler, _) = Api.Make(200, """{"time_ms":1000}""", new FakeSettings { AnonToken = "eta_g" });
        var ghost = await api.FetchGhostSplitsAsync("q", ["a", "b"], "Very Hard", 4, 0);
        Assert.True(ghost.Found);
        Assert.Equal("""{"time_ms":1000}""", ghost.Body);
        Assert.Equal("https://s.example/api/quests/q/ghost?slugs=a,b&difficulty=Very%20Hard&party_size=4&pb=0",
            handler.Requests[0].Url);
        Assert.Equal("Bearer eta_g", handler.Requests[0].Authorization);
        Assert.False((await Api.Make(404).Client.FetchGhostSplitsAsync("q")).Found);
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(401).Client.FetchGhostSplitsAsync("q"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => Api.Make(500).Client.FetchGhostSplitsAsync("q", pb: 0));
        Assert.Equal("GET /api/quests/q/ghost?pb=0 -> 500", ex.Message);
    }

    [Fact(DisplayName = "pin set fetch: linked token only; 200 ok, 404 none, 401/other api-error")]
    public async Task PinSetFetch()
    {
        var (api, handler, _) = Api.Make(200, """{"id":7,"name":"TTF route"}""", Linked);
        var set = await api.FetchPinSetAsync("q", ["a"]);
        Assert.True(set.Found);
        Assert.Equal(7, (long)set.Payload!["id"]!);
        Assert.Equal("https://s.example/api/quests/q/pins?slugs=a", handler.Requests[0].Url);
        Assert.Equal("Bearer eta_linked", handler.Requests[0].Authorization);
        var (guest, h2, _) = Api.Make(404, "", new FakeSettings { AnonToken = "eta_g" });
        Assert.False((await guest.FetchPinSetAsync("q")).Found);
        Assert.Null(h2.Requests[0].Authorization);
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(401).Client.FetchPinSetAsync("q"));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(503).Client.FetchPinSetAsync("q"));
        var (unchanged, h3, _) = Api.Make(304, "", Linked);
        var same = await unchanged.FetchPinSetAsync("q", etag: "\"pins-1\"");
        Assert.True(same.NotModified);
        Assert.Equal("\"pins-1\"", same.ETag);
        Assert.Equal("\"pins-1\"", h3.Requests[0].IfNoneMatch);
        Assert.Null(handler.Requests[0].IfNoneMatch);
        var tagged = new FakeHandler(_ => (200, """{"id":7,"items":{}}""")) { ETag = "\"pins-2\"" };
        var fresh = await new ApiClient(new HttpTransport(tagged), Linked).FetchPinSetAsync("q", etag: "\"pins-1\"");
        Assert.False(fresh.NotModified);
        Assert.Equal("\"pins-2\"", fresh.ETag);
    }

    [Theory(DisplayName = "pin set save status mapping")]
    [InlineData(201, PinSetSaveOutcome.Created)]
    [InlineData(200, PinSetSaveOutcome.Updated)]
    [InlineData(400, PinSetSaveOutcome.Rejected)]
    [InlineData(403, PinSetSaveOutcome.Rejected)]
    [InlineData(404, PinSetSaveOutcome.NotFound)]
    public async Task PinSetSaveOutcomes(int status, PinSetSaveOutcome expected) =>
        Assert.Equal(expected, (await Api.Make(status, "{}").Client.SavePinSetAsync("{}")).Outcome);

    [Fact(DisplayName = "pin set save posts new sets and item overwrites to their paths")]
    public async Task PinSetSavePaths()
    {
        var (api, handler, _) = Api.Make(200, """{"pins":3}""", Linked);
        await api.SavePinSetAsync("""{"quest":"q"}""");
        var updated = await api.SavePinSetAsync("{}", 7);
        Assert.Equal("https://s.example/api/pin-sets", handler.Requests[0].Url);
        Assert.Equal("https://s.example/api/pin-sets/7/items", handler.Requests[1].Url);
        Assert.Equal((3L, 0L), (updated.Pins, updated.Arrows));
        Assert.Equal("rejected", (await Api.Make(400, "{}").Client.SavePinSetAsync("{}")).FailureText);
        Assert.Equal("not-found", (await Api.Make(404, "").Client.SavePinSetAsync("{}")).FailureText);
        Assert.Equal("m", (await Api.Make(403, """{"message":"m","error":"e"}""").Client.SavePinSetAsync("{}")).FailureText);
        Assert.Equal("e", (await Api.Make(403, """{"error":"e"}""").Client.SavePinSetAsync("{}")).FailureText);
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(401).Client.SavePinSetAsync("{}"));
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(500).Client.SavePinSetAsync("{}"));
    }

    [Fact(DisplayName = "diagnostics: 200 true, other statuses false")]
    public async Task Diagnostics()
    {
        var (api, handler, _) = Api.Make(200, "{}", new FakeSettings { AnonToken = "eta_g" });
        Assert.True(await api.UploadRunDiagnosticsAsync(9, "log text", "1.0.0"));
        Assert.Equal("https://s.example/api/runs/9/diagnostics", handler.Requests[0].Url);
        Assert.Equal("""{"log":"log text","client_version":"1.0.0"}""", handler.Requests[0].Body);
        Assert.False(await Api.Make(500).Client.UploadRunDiagnosticsAsync(9, "x"));
        var (noVersion, h2, _) = Api.Make(403);
        Assert.False(await noVersion.UploadRunDiagnosticsAsync(9, "x"));
        Assert.Equal("""{"log":"x"}""", h2.Requests[0].Body);
    }
}
