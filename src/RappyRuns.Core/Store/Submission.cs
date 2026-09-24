using System.Globalization;
using System.Text.Json;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Store;

/// <summary>How POST /api/runs answered (api-client.lisp submit-run, spec core §9.4).</summary>
public enum SubmitOutcome
{
    /// <summary>201: a new draft.</summary>
    Created,

    /// <summary>200: the server already has this run (submitter x quest x time_ms).</summary>
    Duplicate,

    /// <summary>400/403: refused for good.</summary>
    Rejected,

    /// <summary>
    /// Transport failure or any other status, 401 included (the Lisp
    /// API-ERROR condition): the entry goes :failed and is retried later.
    /// </summary>
    ApiError,
}

/// <summary>
/// The board standing the server computed for a fresh submission (the
/// <c>standing</c> object). Previous best and delta come as a pair or not at all.
/// </summary>
public sealed record RunStanding(long Rank, long Parties, long? PreviousBestMs, long? DeltaMs);

/// <summary>
/// A submission's result, carrying just the response fields the queue uses.
/// <see cref="FromJson"/> extracts them from a response body exactly as the
/// Lisp client read its parsed payload.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ServerId">The run's <c>id</c> (created/duplicate).</param>
/// <param name="Url">The run page <c>url</c> (created/duplicate).</param>
/// <param name="Message">The server's <c>message</c> (rejected), or the api-error text.</param>
/// <param name="Errors">The server's <c>errors</c> list (rejected), each already as text.</param>
/// <param name="Standing">The board standing (created), when the server sent a usable one.</param>
public sealed record SubmitResult(
    SubmitOutcome Outcome,
    long? ServerId = null,
    string? Url = null,
    string? Message = null,
    IReadOnlyList<string>? Errors = null,
    RunStanding? Standing = null)
{
    /// <summary>An API-ERROR: the entry becomes :failed with <paramref name="message"/> as its reason.</summary>
    public static SubmitResult ApiError(string message) => new(SubmitOutcome.ApiError, Message: message);

    /// <summary>
    /// The fields of a created/duplicate/rejected response body. Anything that
    /// is not a JSON object yields no fields, like the Lisp hash-table-p guards.
    /// </summary>
    public static SubmitResult FromJson(SubmitOutcome outcome, JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body) return new SubmitResult(outcome);
        long? id = body.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.Number
                   && idNode.TryGetInt64(out var idValue) ? idValue : null;
        var url = body.TryGetProperty("url", out var urlNode) && urlNode.ValueKind == JsonValueKind.String
            ? urlNode.GetString() : null;
        var message = body.TryGetProperty("message", out var messageNode) ? Aesthetic(messageNode) : null;
        List<string>? errors = null;
        if (body.TryGetProperty("errors", out var errorsNode) && errorsNode.ValueKind == JsonValueKind.Array)
            errors = errorsNode.EnumerateArray().Select(e => Aesthetic(e) ?? "NIL").ToList();
        return new SubmitResult(outcome, id, url, message, errors, StandingFromJson(body));
    }

    /// <summary>
    /// standing-updates' guard (store.lisp:360): a standing counts only when
    /// rank and parties are integers; the previous best and delta only when
    /// both are integers.
    /// </summary>
    public static RunStanding? StandingFromJson(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("standing", out var standing)
            || standing.ValueKind != JsonValueKind.Object)
            return null;
        if (Integer(standing, "rank") is not { } rank || Integer(standing, "parties") is not { } parties) return null;
        var prev = Integer(standing, "previous_best_ms");
        var delta = Integer(standing, "delta_ms");
        return prev is not null && delta is not null
            ? new RunStanding(rank, parties, prev, delta)
            : new RunStanding(rank, parties, null, null);
    }

    private static long? Integer(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value)
            ? value : null;

    // FORMAT ~a of a parsed JSON value: strings raw, JSON null/false as
    // absent (jzon's false is NIL), other values as their JSON text.
    private static string? Aesthetic(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => null,
        JsonValueKind.True => "T",
        _ => e.GetRawText(),
    };
}

/// <summary>
/// Sends one queue entry to the server (POST /api/runs). The API layer
/// implements it. Transport failures and unexpected statuses must come back
/// as <see cref="SubmitOutcome.ApiError"/> rather than throw; an exception
/// escapes the submission pass (the Lisp client only caught API-ERROR).
/// </summary>
public interface IRunSubmitter
{
    /// <param name="entry">The entry's plist (the detector run + status keys) to serialize (spec core §7.2).</param>
    /// <param name="token">The bearer token (<see cref="Config.ConfigStore.SubmissionToken"/>, never empty here).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<SubmitResult> SubmitAsync(Plist entry, string token, CancellationToken cancellationToken);
}

/// <summary>
/// POST /api/register-anonymous (api-client.lisp:310): returns the new guest
/// token. Any failure - refusal, rate limit, transport - is an exception; the
/// caller treats every exception as "not now".
/// </summary>
public interface IAnonymousRegistrar
{
    Task<string> RegisterAnonymousAsync(string label, CancellationToken cancellationToken);
}

/// <summary>The pure submission-to-entry mapping (store.lisp:374 submission-updates).</summary>
public static class Submission
{
    /// <summary>
    /// The ordered key/value updates for a queue entry after a submission.
    /// Created: <c>:status :submitted :url :server-id</c> plus the standing
    /// keys; duplicate: <c>:status :duplicate :url :server-id</c>; rejected:
    /// <c>:status :rejected :reason</c>; api-error: <c>:status :failed :reason</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, SexpNode>> Updates(SubmitResult result)
    {
        var updates = new List<KeyValuePair<string, SexpNode>>();
        void Add(string key, SexpNode value) => updates.Add(new(key, value));
        SexpNode OrNil(long? v) => v is { } x ? SexpNode.Int(x) : SexpNode.Nil;
        SexpNode StrOrNil(string? v) => v is null ? SexpNode.Nil : SexpNode.Str(v);

        switch (result.Outcome)
        {
            case SubmitOutcome.Created:
                Add(RunKeys.Status, SexpNode.Kw(RunStatus.Submitted));
                Add(RunKeys.Url, StrOrNil(result.Url));
                Add(RunKeys.ServerId, OrNil(result.ServerId));
                if (result.Standing is { } s)
                {
                    Add(RunKeys.StandingRank, SexpNode.Int(s.Rank));
                    Add(RunKeys.StandingParties, SexpNode.Int(s.Parties));
                    if (s.PreviousBestMs is { } prev && s.DeltaMs is { } delta)
                    {
                        Add(RunKeys.StandingPrevMs, SexpNode.Int(prev));
                        Add(RunKeys.StandingDeltaMs, SexpNode.Int(delta));
                    }
                }
                break;
            case SubmitOutcome.Duplicate:
                Add(RunKeys.Status, SexpNode.Kw(RunStatus.Duplicate));
                Add(RunKeys.Url, StrOrNil(result.Url));
                Add(RunKeys.ServerId, OrNil(result.ServerId));
                break;
            case SubmitOutcome.Rejected:
                Add(RunKeys.Status, SexpNode.Kw(RunStatus.Rejected));
                Add(RunKeys.Reason, SexpNode.Str(RejectionReason(result.Message, result.Errors)));
                break;
            case SubmitOutcome.ApiError:
                Add(RunKeys.Status, SexpNode.Kw(RunStatus.Failed));
                Add(RunKeys.Reason, SexpNode.Str(result.Message ?? ""));
                break;
        }
        return updates;
    }

    /// <summary>
    /// <c>(format nil "~@[~a ~]~@[~{~a~^; ~}~]" message errors)</c>: the message
    /// followed by a space (kept even when no errors follow - parity), then
    /// the errors joined by "; ".
    /// </summary>
    public static string RejectionReason(string? message, IReadOnlyList<string>? errors)
    {
        var text = message is null ? "" : message + " ";
        if (errors is { Count: > 0 }) text += string.Join("; ", errors);
        return text;
    }

    /// <summary>
    /// anonymous-client-label (store.lisp:60): the guest token's label, so the
    /// site's token list says which computer it belongs to.
    /// </summary>
    public static string AnonymousClientLabel(string? machineName) =>
        string.IsNullOrEmpty(machineName)
            ? "Desktop client [guest]"
            : string.Create(CultureInfo.InvariantCulture, $"Desktop client ({machineName}) [guest]");

    /// <summary>
    /// The computer name for token labels (Lisp <c>machine-instance</c>), or
    /// null when Windows cannot say. The one copy every label builder uses.
    /// </summary>
    public static string? SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
