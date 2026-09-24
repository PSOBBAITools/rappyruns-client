using System.Text.Json;

namespace RappyRuns.Core.Store;

/// <summary>How PUT /api/runs/{id}/video-file answered (api-client.lisp upload-run-video, spec core §9.5).</summary>
public enum UploadOutcome
{
    /// <summary>The video is attached to the draft.</summary>
    Attached,

    /// <summary>A video was already on file - just as done.</summary>
    Duplicate,

    /// <summary>The server refused the upload (<see cref="UploadResult.Error"/> says why; "pending-limit" is retried later).</summary>
    Rejected,

    /// <summary>Transport failure or unexpected status (the Lisp API-ERROR): back off and retry.</summary>
    ApiError,
}

/// <summary>An upload's result with the response fields the queue reads.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Status">The run's <c>status</c> (attached/duplicate): "held", "approved", ...</param>
/// <param name="Error">The <c>error</c> code (rejected), e.g. "pending-limit".</param>
/// <param name="Message">The <c>message</c> (rejected), or the api-error text.</param>
public sealed record UploadResult(UploadOutcome Outcome, string? Status = null, string? Error = null, string? Message = null)
{
    public static UploadResult ApiError(string message, bool counted = false) =>
        new(UploadOutcome.ApiError, Message: message) { Counted = counted };

    /// <summary>
    /// An api-error that happened after the request body started going out
    /// (a reset mid-body, a timeout while sending or awaiting the reply) or a
    /// 5xx reply: the upload itself is what fails. These count toward
    /// <see cref="RunQueue.MaxUploadFailures"/> and back off exponentially.
    /// A failure before the body (DNS, connect, offline) or a 401 is not
    /// counted: it says nothing about this upload and never gives it up.
    /// </summary>
    public bool Counted { get; init; }

    /// <summary>The fields from a response body; a non-object body yields none.</summary>
    public static UploadResult FromJson(UploadOutcome outcome, JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body) return new UploadResult(outcome);
        return new UploadResult(outcome, Str(body, "status"), Str(body, "error"), Str(body, "message"));
    }

    private static string? Str(JsonElement body, string name) =>
        body.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
}

/// <summary>Bytes sent so far of the in-flight upload for <see cref="ServerId"/>.</summary>
public sealed record UploadProgress(long ServerId, long Done, long Total)
{
    /// <summary>Whole percent, floor(100 * done / total); 0 while the total is unknown.</summary>
    public int Percent => Total > 0 ? (int)Math.Floor((decimal)(100 * Done) / Total) : 0;
}

/// <summary>
/// The video upload calls, implemented by the API layer. Failures come back as
/// <see cref="UploadOutcome.ApiError"/>; exceptions (other than cancellation)
/// escape to the caller's worker.
/// </summary>
public interface IVideoUploader
{
    /// <summary>
    /// Upload <paramref name="videoPath"/> to draft <paramref name="serverId"/>
    /// (with <c>?offset_ms=</c> when <paramref name="offsetMs"/> is known),
    /// reporting (bytes done, total) through <paramref name="progress"/>.
    /// </summary>
    Task<UploadResult> UploadVideoAsync(long serverId, string videoPath, long? offsetMs,
        Action<long, long>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort capture diagnostics after an upload attempt
    /// (send-run-diagnostics!, store.lisp:542). Failures are swallowed by the caller.
    /// </summary>
    Task SendDiagnosticsAsync(long serverId, CancellationToken cancellationToken);
}
