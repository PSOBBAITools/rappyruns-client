using System.Net;
using System.Text;
using RappyRuns.Core.Api;

namespace RappyRuns.Tests.Api;

/// <summary>One request as the fake server saw it.</summary>
internal sealed record SeenRequest(
    string Method,
    string Url,
    string? Body,
    byte[]? RawBody,
    string? Authorization,
    string? ContentType,
    string? UserAgent,
    string? Accept,
    long? ContentLength);

/// <summary>An in-memory HTTP server: records requests, answers with a scripted responder.</summary>
internal sealed class FakeHandler(Func<SeenRequest, (int Status, string Body)> respond) : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? raw = null;
        if (request.Content is not null) raw = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var seen = new SeenRequest(
            request.Method.Method,
            request.RequestUri!.AbsoluteUri,
            raw is null ? null : Encoding.UTF8.GetString(raw),
            raw,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.TryGetValues("Content-Type", out var ct) == true ? string.Join(",", ct) : null,
            request.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(" ", ua) : null,
            request.Headers.TryGetValues("Accept", out var accept) ? string.Join(",", accept) : null,
            request.Content?.Headers.ContentLength);
        lock (Requests) Requests.Add(seen);
        var (status, body) = respond(seen);
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
            RequestMessage = request,
        };
    }

    /// <summary>A fixed answer for every request.</summary>
    public static FakeHandler Always(int status, string body = "") => new(_ => (status, body));
}

/// <summary>A handler that fails every request with an exception.</summary>
internal sealed class ThrowingHandler(Func<Exception> make) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(make());
}

/// <summary>In-memory settings with a save counter.</summary>
internal sealed class FakeSettings : IAuthSettings
{
    public string ServerUrl { get; set; } = "https://s.example";
    public string ApiToken { get; set; } = "";
    public string AnonToken { get; set; } = "";
    public int Saves { get; private set; }
    public void Save() => Saves++;
}

internal static class Api
{
    public static (ApiClient Client, FakeHandler Handler, FakeSettings Settings) Make(
        Func<SeenRequest, (int, string)> respond, FakeSettings? settings = null)
    {
        var handler = new FakeHandler(respond);
        var s = settings ?? new FakeSettings();
        return (new ApiClient(new HttpTransport(handler), s), handler, s);
    }

    public static (ApiClient Client, FakeHandler Handler, FakeSettings Settings) Make(int status, string body = "",
        FakeSettings? settings = null) => Make(_ => (status, body), settings);
}
