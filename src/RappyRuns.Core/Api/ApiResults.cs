using System.Text.Json.Nodes;

namespace RappyRuns.Core.Api;

/// <summary>POST /api/pair accepted: the code to show in the browser and the polling schedule.</summary>
/// <param name="Code">One-time pairing code.</param>
/// <param name="IntervalSeconds">Seconds between polls (server's <c>interval</c>, default 2).</param>
/// <param name="ExpiresInSeconds">Code lifetime (server's <c>expires_in</c>, default 600).</param>
public sealed record PairingStart(string Code, double IntervalSeconds, double ExpiresInSeconds);

/// <summary>GET /api/pair/{code} verdicts (<c>poll-pairing</c>).</summary>
public enum PairingPollStatus
{
    /// <summary>200 without a token: the user has not decided yet.</summary>
    Pending,

    /// <summary>404: the code expired or was already used.</summary>
    Gone,

    /// <summary>200 with a token: approved.</summary>
    Complete,
}

/// <summary>One pairing poll; <see cref="Token"/> is set only when <see cref="PairingPollStatus.Complete"/>.</summary>
public sealed record PairingPoll(PairingPollStatus Status, string? Token = null);

/// <summary>POST /api/login verdicts (<c>login-with-password</c>).</summary>
public enum LoginStatus
{
    /// <summary>201 with a token.</summary>
    Ok,

    /// <summary>401: username or password rejected.</summary>
    Unauthorized,
}

/// <summary>POST /api/login result; <see cref="Token"/> is set only when <see cref="LoginStatus.Ok"/>.</summary>
public sealed record LoginResult(LoginStatus Status, string? Token = null);

/// <summary>POST /api/register-anonymous success: the guest token and its generated username.</summary>
public sealed record AnonymousRegistration(string Token, string? Username);

/// <summary>POST /api/merge-anonymous verdicts. Either way the guest token is now useless.</summary>
public enum MergeResult
{
    /// <summary>200: the guest's runs now belong to the linked account.</summary>
    Ok,

    /// <summary>404: the server no longer knows the guest (purged or already merged).</summary>
    Gone,
}

/// <summary>
/// The GET /api/me user, with the derived flags the client acts on (spec core §7.1,
/// ui-shell §8.8). <see cref="Raw"/> keeps the whole object for anything else.
/// </summary>
/// <param name="Username">The account name shown in "Token: OK (name)".</param>
/// <param name="Role">"user", "moderator" or "admin" (null when absent).</param>
/// <param name="AutoPublish"><c>auto_publish</c> is exactly the integer 1 (Lisp <c>eql 1</c>).</param>
/// <param name="Features">String elements of <c>features</c> (empty when absent or not an array).</param>
/// <param name="Raw">The parsed response object.</param>
public sealed record MeUser(string? Username, string? Role, bool AutoPublish, IReadOnlyList<string> Features, JsonObject Raw)
{
    /// <summary><c>moderator-role-p</c>: moderator or admin may author quest rules.</summary>
    public bool IsModerator => IsModeratorRole(Role);

    /// <summary><c>pinshare-feature-p</c>: the account is in the Pin Share rollout.</summary>
    public bool PinShareAllowed => Features.Contains("pinshare");

    /// <summary><c>moderator-role-p</c> on a raw role string; mirrors the server's MODELS:MODERATOR-P.</summary>
    public static bool IsModeratorRole(string? role) => role is "moderator" or "admin";

    /// <summary>
    /// <c>pinshare-feature-p</c> on a raw /api/me object: true only when <c>features</c>
    /// is an array containing "pinshare". An older server (no field), a non-array
    /// value, or no user at all (unlinked) mean no.
    /// </summary>
    public static bool HasPinShareFeature(JsonNode? user) =>
        ApiJson.Get(user, "features") is JsonArray features
        && features.Any(f => ApiJson.AsString(f) == "pinshare");

    /// <summary>Builds the typed view of a parsed /api/me object.</summary>
    public static MeUser FromJson(JsonObject user)
    {
        var features = ApiJson.Get(user, "features") is JsonArray array
            ? array.Select(ApiJson.AsString).OfType<string>().ToList()
            : [];
        return new MeUser(
            ApiJson.GetString(user, "username"),
            ApiJson.GetString(user, "role"),
            ApiJson.GetInteger(user, "auto_publish") == 1,
            features,
            user);
    }
}

/// <summary>GET /api/me: <see cref="User"/> on 200, <see cref="Unauthorized"/> on 401.</summary>
public sealed record MeResult(bool Unauthorized, MeUser? User);

/// <summary>POST /api/runs outcomes (a contract: the queue maps them to entry statuses).</summary>
public enum SubmitOutcome
{
    /// <summary>201: stored as a new draft → entry :submitted.</summary>
    Created,

    /// <summary>200: the server already had this run → entry :duplicate.</summary>
    Duplicate,

    /// <summary>400 / 403: refused for good → entry :rejected.</summary>
    Rejected,
}

/// <summary>
/// The board standing a fresh submission carries (<c>standing-updates</c>): rank
/// and party count, plus the previous best and delta only when both are integers.
/// </summary>
public sealed record RunStanding(long Rank, long Parties, long? PreviousBestMs, long? DeltaMs)
{
    /// <summary>
    /// Parses <c>payload.standing</c>; null unless <c>rank</c> and <c>parties</c> are both
    /// integers (older servers and aborted runs send none).
    /// </summary>
    public static RunStanding? FromPayload(JsonNode? payload)
    {
        var standing = ApiJson.Get(payload, "standing");
        if (standing is not JsonObject) return null;
        var rank = ApiJson.GetInteger(standing, "rank");
        var parties = ApiJson.GetInteger(standing, "parties");
        if (rank is null || parties is null) return null;
        var prev = ApiJson.GetInteger(standing, "previous_best_ms");
        var delta = ApiJson.GetInteger(standing, "delta_ms");
        return prev is not null && delta is not null
            ? new RunStanding(rank.Value, parties.Value, prev, delta)
            : new RunStanding(rank.Value, parties.Value, null, null);
    }
}

/// <summary>
/// POST /api/runs result (<c>submit-run</c> + the parsing half of
/// <c>submission-updates</c>). <see cref="ServerId"/>/<see cref="Url"/> are set for
/// created and duplicate, <see cref="Standing"/> only for created,
/// <see cref="Reason"/> only for rejected.
/// </summary>
public sealed record SubmitRunResult(
    SubmitOutcome Outcome,
    long? ServerId,
    string? Url,
    RunStanding? Standing,
    string? Reason,
    JsonNode? Payload)
{
    /// <summary>Maps an outcome and parsed payload to the typed result.</summary>
    public static SubmitRunResult FromPayload(SubmitOutcome outcome, JsonNode? payload) => outcome switch
    {
        SubmitOutcome.Created => new(outcome, ApiJson.GetInteger(payload, "id"), ApiJson.GetString(payload, "url"),
            RunStanding.FromPayload(payload), null, payload),
        SubmitOutcome.Duplicate => new(outcome, ApiJson.GetInteger(payload, "id"), ApiJson.GetString(payload, "url"),
            null, null, payload),
        _ => new(outcome, null, null, null, RejectionReason(payload), payload),
    };

    /// <summary>
    /// The :reason text of a rejected run, exactly FORMAT
    /// <c>"~@[~a ~]~@[~{~a~^; ~}~]"</c> over <c>message</c> and <c>errors</c>: the
    /// message followed by a space (kept even with no errors), then the errors joined
    /// with "; ". An empty errors array prints nothing.
    /// </summary>
    public static string RejectionReason(JsonNode? payload)
    {
        var message = ApiJson.Get(payload, "message");
        var errors = ApiJson.Get(payload, "errors");
        var text = message is null ? "" : ApiJson.Princ(message) + " ";
        if (errors is JsonArray array)
            text += string.Join("; ", array.Select(ApiJson.Princ));
        else if (errors is not null)
            text += ApiJson.Princ(errors);
        return text;
    }
}

/// <summary>POST /api/quests outcomes (<c>create-quest-rule</c>).</summary>
public enum QuestRuleOutcome
{
    /// <summary>201.</summary>
    Created,

    /// <summary>409: a rule with that name exists.</summary>
    Duplicate,

    /// <summary>403: the token is not a moderator's.</summary>
    Forbidden,

    /// <summary>400: invalid input; the payload has the server's message.</summary>
    Rejected,
}

/// <summary>POST /api/quests result with the parsed response.</summary>
public sealed record QuestRuleResult(QuestRuleOutcome Outcome, JsonNode? Payload);

/// <summary>
/// GET /api/quests/{slug}/ghost: <see cref="Found"/> on 200 (with the raw body for
/// the ghost module to parse; <see cref="Payload"/> is null when it is not JSON),
/// not found on 404 (nothing to race).
/// </summary>
public sealed record GhostFetchResult(bool Found, string? Body, JsonNode? Payload);

/// <summary>
/// GET /api/quests/{slug}/pins: <see cref="Found"/> on 200 with the parsed set and
/// its <see cref="ETag"/>; not found on 404 (no set chosen, or it went private);
/// <see cref="NotModified"/> on 304 (the If-None-Match still names the set:
/// <see cref="Found"/> but no <see cref="Payload"/> - check NotModified first).
/// </summary>
public sealed record PinSetFetchResult(bool Found, JsonNode? Payload, string? ETag = null, bool NotModified = false);

/// <summary>POST /api/pin-sets[/{id}/items] outcomes (<c>save-pin-set</c>).</summary>
public enum PinSetSaveOutcome
{
    /// <summary>201: a new private set.</summary>
    Created,

    /// <summary>200: an existing set's items replaced.</summary>
    Updated,

    /// <summary>400 / 403: refused; the payload has the server's message.</summary>
    Rejected,

    /// <summary>404: the set is gone or not the user's.</summary>
    NotFound,
}

/// <summary>
/// Pin set save result, with the fields the report dialog shows
/// (gui.lisp <c>save-pin-set-in-background</c>).
/// </summary>
public sealed record PinSetSaveResult(PinSetSaveOutcome Outcome, JsonNode? Payload)
{
    /// <summary>The new set's page (created).</summary>
    public string? Url => ApiJson.GetString(Payload, "url");

    /// <summary>Pins stored (updated); 0 when absent.</summary>
    public long Pins => ApiJson.GetInteger(Payload, "pins") ?? 0;

    /// <summary>Arrows stored (updated); 0 when absent.</summary>
    public long Arrows => ApiJson.GetInteger(Payload, "arrows") ?? 0;

    /// <summary>
    /// The failure text: the server's <c>message</c>, else its <c>error</c>, else the
    /// outcome name in lower case ("rejected", "not-found").
    /// </summary>
    public string FailureText =>
        ApiJson.GetString(Payload, "message") ?? ApiJson.GetString(Payload, "error")
        ?? (Outcome == PinSetSaveOutcome.NotFound ? "not-found" : Outcome.ToString().ToLowerInvariant());
}

/// <summary>POST /api/runs/{id}/video-file outcomes (<c>upload-run-video</c>).</summary>
public enum VideoUploadOutcome
{
    /// <summary>200/201: the video is on the server now.</summary>
    Attached,

    /// <summary>200/201 with <c>duplicate</c> true: a video was already on file (just as done).</summary>
    Duplicate,

    /// <summary>
    /// 400/403/404/409/411/413: permanent, except the <c>pending-limit</c> error which
    /// is worth retrying an hour later.
    /// </summary>
    Rejected,
}

/// <summary>Video upload result with the fields the queue uses (spec core §9.5).</summary>
public sealed record VideoUploadResult(VideoUploadOutcome Outcome, JsonNode? Payload)
{
    /// <summary>The run's status after the upload ("held", "approved", ...).</summary>
    public string? Status => ApiJson.GetString(Payload, "status");

    /// <summary>The machine-readable error code of a rejection (e.g. "pending-limit").</summary>
    public string? Error => ApiJson.GetString(Payload, "error");

    /// <summary>The server's human-readable message.</summary>
    public string? Message => ApiJson.GetString(Payload, "message");

    /// <summary>:held — landed as a draft the player publishes in the browser.</summary>
    public bool Held => Status == "held";

    /// <summary>:approved.</summary>
    public bool Approved => Status == "approved";

    /// <summary>A rejection that should be retried later (+3600 s) rather than given up.</summary>
    public bool IsPendingLimit => Outcome == VideoUploadOutcome.Rejected && Error == "pending-limit";

    /// <summary>The :upload-error text of a permanent rejection: message, else error, else "rejected".</summary>
    public string RejectionText => Message ?? Error ?? "rejected";
}
