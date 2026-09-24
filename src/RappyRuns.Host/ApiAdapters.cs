using System.Text.Json;
using System.Text.Json.Nodes;
using RappyRuns.Core.Api;
using RappyRuns.Core.Config;
using RappyRuns.Core.Media;
using RappyRuns.Core.Sexp;
using RappyRuns.Core.Store;
using ApiSubmitOutcome = RappyRuns.Core.Api.SubmitOutcome;
using StoreSubmitOutcome = RappyRuns.Core.Store.SubmitOutcome;

namespace RappyRuns.Host;

/// <summary>The API client's settings, backed by config.sexp.</summary>
public sealed class ConfigAuthSettings(ConfigStore config) : IAuthSettings
{
    public string ServerUrl => config.ServerUrl;

    public string ApiToken
    {
        get => config.ApiToken;
        set => config.ApiToken = value;
    }

    public string AnonToken
    {
        get => config.AnonToken;
        set => config.AnonToken = value;
    }

    public void Save() => config.Save();
}

/// <summary>
/// Connects the run queue's network seams (store port) to the HTTP client
/// (API port). Every transport failure becomes the Lisp API-ERROR outcome here,
/// because the queue only expects those, never exceptions.
/// </summary>
public sealed class QueueNetwork(ApiClient api, Func<Plist, string> runJson, Func<string> diagnosticsLog)
    : IRunSubmitter, IAnonymousRegistrar, IVideoUploader
{
    public async Task<SubmitResult> SubmitAsync(Plist entry, string token, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = runJson(entry);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // One unencodable entry fails alone (:failed with the reason); an
            // escaping exception would abort every later pass at this entry.
            var reason = $"could not encode the run: {e.GetType().Name}: {e.Message}";
            RecordingLog.Write("submit: " + reason);
            return SubmitResult.ApiError(reason);
        }
        try
        {
            var r = await api.SubmitRunAsync(body, token, cancellationToken).ConfigureAwait(false);
            var outcome = r.Outcome switch
            {
                ApiSubmitOutcome.Created => StoreSubmitOutcome.Created,
                ApiSubmitOutcome.Duplicate => StoreSubmitOutcome.Duplicate,
                ApiSubmitOutcome.Rejected => StoreSubmitOutcome.Rejected,
                _ => StoreSubmitOutcome.ApiError,
            };
            // The store reproduces the Lisp payload reading from the raw body.
            return SubmitResult.FromJson(outcome, ToElement(r.Payload));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or IOException)
        {
            return SubmitResult.ApiError(e.Message);
        }
    }

    public async Task<string> RegisterAnonymousAsync(string label, CancellationToken cancellationToken)
    {
        var r = await api.RegisterAnonymousAsync(label, cancellationToken).ConfigureAwait(false);
        return r.Token;
    }

    /// <summary>
    /// Is this upload failure the upload's own (<see cref="UploadResult.Counted"/>,
    /// S17)? A 5xx reply, or any failure after the body started going out. Not:
    /// DNS / connect failures, a 401, other statuses, local file errors.
    /// </summary>
    internal static bool CountsAgainstUpload(Exception e) =>
        e is ApiException { Status: >= 500 } or ApiException { Status: null, BodyStarted: true };

    public async Task<UploadResult> UploadVideoAsync(long serverId, string videoPath, long? offsetMs,
        Action<long, long>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var r = await api.UploadRunVideoAsync(serverId, videoPath, offsetMs, progress, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var outcome = r.Outcome switch
            {
                VideoUploadOutcome.Attached => UploadOutcome.Attached,
                VideoUploadOutcome.Duplicate => UploadOutcome.Duplicate,
                _ => UploadOutcome.Rejected,
            };
            return UploadResult.FromJson(outcome, ToElement(r.Payload));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or IOException)
        {
            return UploadResult.ApiError(e.Message, CountsAgainstUpload(e));
        }
    }

    public async Task SendDiagnosticsAsync(long serverId, CancellationToken cancellationToken)
    {
        try
        {
            await api.UploadRunDiagnosticsAsync(serverId, diagnosticsLog(), Core.ClientVersion.Current,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RecordingLog.Write("diagnostics upload failed: " + e.Message);
        }
    }

    private static JsonElement? ToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);
}
