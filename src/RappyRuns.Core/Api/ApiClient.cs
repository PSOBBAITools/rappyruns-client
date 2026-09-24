using System.Globalization;
using System.Text.Json.Nodes;

namespace RappyRuns.Core.Api;

/// <summary>
/// Where the API client gets its server URL and tokens (the Lisp code read
/// <c>config-value</c> directly). The app backs this with the config store;
/// <see cref="AuthService"/> also writes the tokens through it.
/// </summary>
public interface IAuthSettings
{
    /// <summary>config <c>:server-url</c>.</summary>
    string ServerUrl { get; }

    /// <summary>config <c>:api-token</c>, raw (may carry pasted whitespace; "" = unlinked).</summary>
    string ApiToken { get; set; }

    /// <summary>config <c>:anon-token</c>, the auto-registered guest ("" = none).</summary>
    string AnonToken { get; set; }

    /// <summary>Persist the settings (config.sexp). Called after every token change.</summary>
    void Save();
}

/// <summary>
/// The Rappy Runs JSON API (port of api-client.lisp, spec core §7). Each method maps
/// HTTP statuses to typed outcomes exactly as the Lisp client did — that mapping is a
/// contract the queue and UI rely on. "Worth retrying" failures (transport errors,
/// 401 on authenticated calls, unexpected statuses) throw <see cref="ApiException"/>.
/// Tokens default per the spec's table: L = the linked <c>:api-token</c>,
/// S = <see cref="Tokens.Submission"/>; every method accepts an explicit override.
/// Methods are thread-safe and never retry.
/// </summary>
public sealed class ApiClient(HttpTransport transport, IAuthSettings settings)
{
    /// <summary>The transport (shared with the updater).</summary>
    public HttpTransport Transport { get; } = transport;

    private string Url(string path) => Urls.ApiUrl(settings.ServerUrl, path);

    private string LinkedToken(string? token) => token ?? Tokens.Normalize(settings.ApiToken);

    private string SubmissionToken(string? token) => token ?? Tokens.Submission(settings.ApiToken, settings.AnonToken);

    private static string Body(Action<JsonObject> fill)
    {
        var obj = new JsonObject();
        fill(obj);
        return obj.ToJsonString();
    }

    /// <summary>
    /// GET /api/quests (no token): the server's quest array (<c>slug</c>, <c>name</c>,
    /// <c>episode</c>, <c>category</c>, <c>game_names</c>, <c>game_number</c>,
    /// <c>start</c>/<c>end</c>). Non-200, or a body that is not a JSON array, throws.
    /// </summary>
    public async Task<JsonArray> FetchQuestsAsync(CancellationToken cancellationToken = default)
    {
        var r = await Transport.SendAsync("GET", Url("/api/quests"), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (r.Status != 200) throw new ApiException($"GET /api/quests -> {r.Status}") { Status = r.Status };
        return ApiJson.TryParse(r.Body) as JsonArray
            ?? throw new ApiException("GET /api/quests: the response is not a JSON array");
    }

    /// <summary>
    /// POST /api/pair (no token) <c>{"label"?}</c>: 201 with a string <c>code</c> →
    /// the code with <c>interval</c> (default 2) and <c>expires_in</c> (default 600);
    /// anything else throws.
    /// </summary>
    public async Task<PairingStart> StartPairingAsync(string? label, CancellationToken cancellationToken = default)
    {
        var body = Body(o => { if (label is not null) o["label"] = label; });
        var r = await Transport.SendAsync("POST", Url("/api/pair"), body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = ApiJson.TryParse(r.Body);
        var code = ApiJson.GetString(payload, "code");
        if (r.Status != 201 || payload is not JsonObject || code is null)
            throw new ApiException($"POST /api/pair -> {r.Status}") { Status = r.Status };
        var interval = ApiJson.GetNumber(payload, "interval") ?? 2;
        var expires = ApiJson.GetNumber(payload, "expires_in") ?? 600;
        return new PairingStart(code, interval, expires);
    }

    /// <summary>
    /// GET /api/pair/{code} (no token): 200 + string token → complete, 200 → pending,
    /// 404 → gone; anything else throws (transient; the flow keeps polling).
    /// </summary>
    public async Task<PairingPoll> PollPairingAsync(string code, CancellationToken cancellationToken = default)
    {
        var r = await Transport.SendAsync("GET", Url($"/api/pair/{code}"), cancellationToken: cancellationToken).ConfigureAwait(false);
        var token = ApiJson.GetString(ApiJson.TryParse(r.Body), "token");
        return r.Status switch
        {
            200 when token is not null => new PairingPoll(PairingPollStatus.Complete, token),
            200 => new PairingPoll(PairingPollStatus.Pending),
            404 => new PairingPoll(PairingPollStatus.Gone),
            _ => throw new ApiException($"GET /api/pair -> {r.Status}") { Status = r.Status },
        };
    }

    /// <summary>
    /// POST /api/login (no token) <c>{"username","password","label"?}</c>: 201 + token →
    /// ok, 401 → unauthorized; anything else throws.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string username, string password, string? label,
        CancellationToken cancellationToken = default)
    {
        var body = Body(o =>
        {
            o["username"] = username;
            o["password"] = password;
            if (label is not null) o["label"] = label;
        });
        var r = await Transport.SendAsync("POST", Url("/api/login"), body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var token = ApiJson.GetString(ApiJson.TryParse(r.Body), "token");
        return r.Status switch
        {
            201 when token is not null => new LoginResult(LoginStatus.Ok, token),
            401 => new LoginResult(LoginStatus.Unauthorized),
            _ => throw new ApiException($"POST /api/login -> {r.Status}") { Status = r.Status },
        };
    }

    /// <summary>
    /// POST /api/register-anonymous (no token) <c>{"label"?}</c>: 201 + token → the guest;
    /// anything else (including the rate limit) throws.
    /// </summary>
    public async Task<AnonymousRegistration> RegisterAnonymousAsync(string? label, CancellationToken cancellationToken = default)
    {
        var body = Body(o => { if (label is not null) o["label"] = label; });
        var r = await Transport.SendAsync("POST", Url("/api/register-anonymous"), body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var payload = ApiJson.TryParse(r.Body);
        var token = ApiJson.GetString(payload, "token");
        if (r.Status != 201 || token is null)
            throw new ApiException($"POST /api/register-anonymous -> {r.Status}") { Status = r.Status };
        return new AnonymousRegistration(token, ApiJson.GetString(payload, "username"));
    }

    /// <summary>
    /// POST /api/merge-anonymous (L) <c>{"anonymous_token"}</c>: 200 → ok, 404 → gone;
    /// anything else throws, leaving the guest token for a retry.
    /// </summary>
    public async Task<MergeResult> MergeAnonymousAsync(string anonToken, string? token = null,
        CancellationToken cancellationToken = default)
    {
        var body = Body(o => o["anonymous_token"] = anonToken);
        var r = await Transport.SendAsync("POST", Url("/api/merge-anonymous"), body, LinkedToken(token),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return r.Status switch
        {
            200 => MergeResult.Ok,
            404 => MergeResult.Gone,
            _ => throw new ApiException($"POST /api/merge-anonymous -> {r.Status}: {r.Body}") { Status = r.Status },
        };
    }

    /// <summary>
    /// GET /api/me (L): 200 → the user, 401 → unauthorized (a definite verdict on the
    /// token, unlike the exceptions thrown for transport failures, other statuses and
    /// a 200 whose body is not a JSON object).
    /// </summary>
    public async Task<MeResult> FetchMeAsync(string? token = null, CancellationToken cancellationToken = default)
    {
        var r = await Transport.SendAsync("GET", Url("/api/me"), token: LinkedToken(token), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        switch (r.Status)
        {
            case 200:
                return ApiJson.TryParse(r.Body) is JsonObject user
                    ? new MeResult(false, MeUser.FromJson(user))
                    : throw new ApiException("GET /api/me: the response is not a JSON object") { Status = 200 };
            case 401:
                return new MeResult(true, null);
            default:
                throw new ApiException($"GET /api/me -> {r.Status}") { Status = r.Status };
        }
    }

    /// <summary>
    /// POST /api/me/auto-publish (L) <c>{"enabled": 0|1}</c> (integers dodge the
    /// false-vs-null ambiguity): returns on 200, throws otherwise so the caller can
    /// roll its checkbox back.
    /// </summary>
    public async Task UpdateAutoPublishAsync(bool enabled, string? token = null, CancellationToken cancellationToken = default)
    {
        var body = Body(o => o["enabled"] = enabled ? 1 : 0);
        var r = await Transport.SendAsync("POST", Url("/api/me/auto-publish"), body, LinkedToken(token),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        switch (r.Status)
        {
            case 200: return;
            case 401: throw ApiException.InvalidToken();
            default: throw new ApiException($"POST /api/me/auto-publish -> {r.Status}: {r.Body}") { Status = r.Status };
        }
    }

    /// <summary>
    /// POST /api/runs (S) with an already-built run JSON body (<c>run-json</c>, built by
    /// the detector side). 201 → created (id, url, standing), 200 → duplicate (id, url),
    /// 400/403 → rejected (reason), 401 → throws "Invalid or revoked API token", any
    /// other status throws "POST /api/runs -> N: body".
    /// </summary>
    public async Task<SubmitRunResult> SubmitRunAsync(string runJson, string? token = null,
        CancellationToken cancellationToken = default)
    {
        var r = await Transport.SendAsync("POST", Url("/api/runs"), runJson, SubmissionToken(token),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = ApiJson.TryParse(r.Body);
        return r.Status switch
        {
            201 => SubmitRunResult.FromPayload(SubmitOutcome.Created, payload),
            200 => SubmitRunResult.FromPayload(SubmitOutcome.Duplicate, payload),
            400 or 403 => SubmitRunResult.FromPayload(SubmitOutcome.Rejected, payload),
            401 => throw ApiException.InvalidToken(),
            _ => throw new ApiException($"POST /api/runs -> {r.Status}: {r.Body}") { Status = r.Status },
        };
    }

    /// <summary>
    /// POST /api/quests (L, moderator): create a quest rule derived from
    /// <paramref name="parent"/>, clearing on <paramref name="end"/> (and optionally
    /// starting on <paramref name="start"/>); triggers are wire objects from
    /// <see cref="TriggerJson"/>. 201 created / 409 duplicate / 403 forbidden /
    /// 400 rejected; 401 and anything else throw.
    /// </summary>
    public async Task<QuestRuleResult> CreateQuestRuleAsync(string parent, string name, string description,
        JsonObject? end = null, JsonObject? start = null, string? token = null, CancellationToken cancellationToken = default)
    {
        var body = Body(o =>
        {
            o["parent"] = parent;
            o["name"] = name;
            o["description"] = description;
            if (end is not null) o["end"] = end.DeepClone();
            if (start is not null) o["start"] = start.DeepClone();
        });
        var r = await Transport.SendAsync("POST", Url("/api/quests"), body, LinkedToken(token),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = ApiJson.TryParse(r.Body);
        return r.Status switch
        {
            201 => new QuestRuleResult(QuestRuleOutcome.Created, payload),
            409 => new QuestRuleResult(QuestRuleOutcome.Duplicate, payload),
            403 => new QuestRuleResult(QuestRuleOutcome.Forbidden, payload),
            400 => new QuestRuleResult(QuestRuleOutcome.Rejected, payload),
            401 => throw ApiException.InvalidToken(),
            _ => throw new ApiException($"POST /api/quests -> {r.Status}: {r.Body}") { Status = r.Status },
        };
    }

    /// <summary>
    /// GET /api/quests/{slug}/ghost (S; path from <see cref="Urls.GhostPath"/>): 200 →
    /// found with the raw body, 404 → none; 401 and anything else throw. The client
    /// always passes <c>pb: 0</c> (at start time the run is known not to be a PB).
    /// </summary>
    public async Task<GhostFetchResult> FetchGhostSplitsAsync(string slug, IReadOnlyList<string>? extraSlugs = null,
        string? difficulty = null, int? partySize = null, int? pb = null, string? token = null,
        CancellationToken cancellationToken = default)
    {
        var path = Urls.GhostPath(slug, extraSlugs, difficulty, partySize, pb);
        var r = await Transport.SendAsync("GET", Url(path), token: SubmissionToken(token), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return r.Status switch
        {
            200 => new GhostFetchResult(true, r.Body, ApiJson.TryParse(r.Body)),
            404 => new GhostFetchResult(false, null, null),
            401 => throw ApiException.InvalidToken(),
            _ => throw new ApiException($"GET {path} -> {r.Status}") { Status = r.Status },
        };
    }

    /// <summary>
    /// GET /api/quests/{slug}/pins?slugs=... (L): the pin set chosen on the site for the
    /// loaded quest. 200 → found, 404 → none (none chosen, or it went private);
    /// 401 and anything else throw.
    /// </summary>
    public async Task<PinSetFetchResult> FetchPinSetAsync(string slug, IReadOnlyList<string>? extraSlugs = null,
        string? token = null, CancellationToken cancellationToken = default)
    {
        var path = Urls.PinsPath(slug, extraSlugs);
        var r = await Transport.SendAsync("GET", Url(path), token: LinkedToken(token), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return r.Status switch
        {
            200 => new PinSetFetchResult(true, ApiJson.TryParse(r.Body)),
            404 => new PinSetFetchResult(false, null),
            401 => throw ApiException.InvalidToken(),
            _ => throw new ApiException($"GET {path} -> {r.Status}") { Status = r.Status },
        };
    }

    /// <summary>
    /// POST /api/pin-sets (new private set) or, with <paramref name="setId"/>,
    /// POST /api/pin-sets/{id}/items (replace the user's own set's items). The body is
    /// <c>pinshare-save-body</c>'s JSON (built by the Pin Share module).
    /// 201 created / 200 updated / 400,403 rejected / 404 not found; 401 and anything
    /// else throw.
    /// </summary>
    public async Task<PinSetSaveResult> SavePinSetAsync(string body, long? setId = null, string? token = null,
        CancellationToken cancellationToken = default)
    {
        var path = setId is { } id
            ? string.Create(CultureInfo.InvariantCulture, $"/api/pin-sets/{id}/items")
            : "/api/pin-sets";
        var r = await Transport.SendAsync("POST", Url(path), body, LinkedToken(token), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var payload = ApiJson.TryParse(r.Body);
        return r.Status switch
        {
            201 => new PinSetSaveResult(PinSetSaveOutcome.Created, payload),
            200 => new PinSetSaveResult(PinSetSaveOutcome.Updated, payload),
            400 or 403 => new PinSetSaveResult(PinSetSaveOutcome.Rejected, payload),
            404 => new PinSetSaveResult(PinSetSaveOutcome.NotFound, payload),
            401 => throw ApiException.InvalidToken(),
            _ => throw new ApiException($"POST {path} -> {r.Status}") { Status = r.Status },
        };
    }

    /// <summary>
    /// POST /api/runs/{id}/video-file[?offset_ms=N] (S): streams the mp4 at
    /// <paramref name="filePath"/> (Content-Type video/mp4) with the upload timeouts.
    /// <paramref name="onProgress"/>(bytesSent, total) runs after every 1 MB chunk on
    /// the upload's thread. 200/201 → attached (duplicate when the reply's
    /// <c>duplicate</c> is truthy); 400/403/404/409/411/413 → rejected; 401 and anything
    /// else throw (worth retrying).
    /// </summary>
    public async Task<VideoUploadResult> UploadRunVideoAsync(long serverId, string filePath, long? offsetMs = null,
        Action<long, long>? onProgress = null, string? token = null, CancellationToken cancellationToken = default)
    {
        var r = await Transport.UploadFileAsync("POST", Url(Urls.VideoFilePath(serverId, offsetMs)), filePath, "video/mp4",
            SubmissionToken(token), onProgress, cancellationToken).ConfigureAwait(false);
        var payload = ApiJson.TryParse(r.Body);
        return r.Status switch
        {
            200 or 201 => new VideoUploadResult(
                ApiJson.Truthy(ApiJson.Get(payload, "duplicate")) ? VideoUploadOutcome.Duplicate : VideoUploadOutcome.Attached,
                payload),
            400 or 403 or 404 or 409 or 411 or 413 => new VideoUploadResult(VideoUploadOutcome.Rejected, payload),
            401 => throw ApiException.InvalidToken(),
            _ => throw new ApiException(string.Create(CultureInfo.InvariantCulture,
                $"POST /api/runs/{serverId}/video-file -> {r.Status}: {r.Body}")) { Status = r.Status },
        };
    }

    /// <summary>
    /// POST /api/runs/{id}/diagnostics (S) <c>{"log", "client_version"?}</c>: true on 200,
    /// false on any other status; transport failures throw. Callers treat the whole
    /// call as best-effort (catch everything).
    /// </summary>
    public async Task<bool> UploadRunDiagnosticsAsync(long serverId, string log, string? version = null,
        string? token = null, CancellationToken cancellationToken = default)
    {
        var body = Body(o =>
        {
            o["log"] = log;
            if (version is not null) o["client_version"] = version;
        });
        var path = string.Create(CultureInfo.InvariantCulture, $"/api/runs/{serverId}/diagnostics");
        var r = await Transport.SendAsync("POST", Url(path), body, SubmissionToken(token), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return r.Status == 200;
    }
}
