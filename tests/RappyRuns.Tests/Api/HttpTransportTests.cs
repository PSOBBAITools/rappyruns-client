using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using RappyRuns.Core.Api;

namespace RappyRuns.Tests.Api;

/// <summary>The HTTP layer: uploads, downloads, timeouts and error mapping (spec core §6).</summary>
public class HttpTransportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rr-http-" + Guid.NewGuid().ToString("N"));

    public HttpTransportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, int size)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }

    [Fact(DisplayName = "video upload streams the file with video/mp4, Content-Length and per-chunk progress")]
    public async Task UploadStreams()
    {
        var path = WriteFile("run.mp4", 2 * 1024 * 1024 + 10);
        var (api, handler, _) = Api.Make(201, """{"status":"held"}""", new FakeSettings { AnonToken = "eta_g" });
        var progress = new List<(long, long)>();
        var result = await api.UploadRunVideoAsync(7, path, 1234, (done, total) => progress.Add((done, total)));
        var r = Assert.Single(handler.Requests);
        Assert.Equal("https://s.example/api/runs/7/video-file?offset_ms=1234", r.Url);
        Assert.Equal("video/mp4", r.ContentType);
        Assert.Equal("Bearer eta_g", r.Authorization);
        Assert.Equal(new FileInfo(path).Length, r.ContentLength);
        Assert.Equal(File.ReadAllBytes(path), r.RawBody);
        Assert.Equal(3, progress.Count);
        Assert.Equal((new FileInfo(path).Length, new FileInfo(path).Length), progress[^1]);
        Assert.Equal(VideoUploadOutcome.Attached, result.Outcome);
        Assert.True(result.Held);
        Assert.False(result.Approved);
    }

    [Theory(DisplayName = "video upload status mapping")]
    [InlineData(200, "{}", VideoUploadOutcome.Attached)]
    [InlineData(201, """{"duplicate":true,"status":"approved"}""", VideoUploadOutcome.Duplicate)]
    [InlineData(200, """{"duplicate":false}""", VideoUploadOutcome.Attached)]
    [InlineData(200, """{"duplicate":null}""", VideoUploadOutcome.Attached)]
    [InlineData(200, """{"duplicate":0}""", VideoUploadOutcome.Duplicate)]
    [InlineData(400, "{}", VideoUploadOutcome.Rejected)]
    [InlineData(403, "{}", VideoUploadOutcome.Rejected)]
    [InlineData(404, "{}", VideoUploadOutcome.Rejected)]
    [InlineData(409, "{}", VideoUploadOutcome.Rejected)]
    [InlineData(411, "{}", VideoUploadOutcome.Rejected)]
    [InlineData(413, "{}", VideoUploadOutcome.Rejected)]
    public async Task UploadOutcomes(int status, string body, VideoUploadOutcome expected)
    {
        var path = WriteFile("v.mp4", 100);
        Assert.Equal(expected, (await Api.Make(status, body).Client.UploadRunVideoAsync(1, path)).Outcome);
    }

    [Fact(DisplayName = "video upload: pending-limit is retryable, other rejections carry their text")]
    public async Task UploadRejectionFields()
    {
        var path = WriteFile("v.mp4", 10);
        var limit = await Api.Make(409, """{"error":"pending-limit","message":"too many"}""").Client.UploadRunVideoAsync(1, path);
        Assert.True(limit.IsPendingLimit);
        var other = await Api.Make(400, """{"error":"bad-file"}""").Client.UploadRunVideoAsync(1, path);
        Assert.False(other.IsPendingLimit);
        Assert.Equal("bad-file", other.RejectionText);
        Assert.Equal("rejected", (await Api.Make(413, "").Client.UploadRunVideoAsync(1, path)).RejectionText);
    }

    [Fact(DisplayName = "video upload: 401 and unexpected statuses are api-errors")]
    public async Task UploadErrors()
    {
        var path = WriteFile("v.mp4", 10);
        await Assert.ThrowsAsync<ApiException>(() => Api.Make(401).Client.UploadRunVideoAsync(1, path));
        var ex = await Assert.ThrowsAsync<ApiException>(() => Api.Make(502, "bad gateway").Client.UploadRunVideoAsync(1, path));
        Assert.Equal("POST /api/runs/1/video-file -> 502: bad gateway", ex.Message);
    }

    [Fact(DisplayName = "download writes the file only on 200, with progress")]
    public async Task Download()
    {
        var payload = new byte[200_000];
        Random.Shared.NextBytes(payload);
        var handler = new DownloadHandler(200, payload);
        using var transport = new HttpTransport(handler);
        var target = Path.Combine(_dir, "a.zip");
        var progress = new List<(long, long?)>();
        Assert.Equal(200, await transport.DownloadToFileAsync("https://x/a.zip", target, onProgress: (d, t) => progress.Add((d, t))));
        Assert.Equal(payload, File.ReadAllBytes(target));
        Assert.Equal((200_000L, (long?)200_000), progress[^1]);
        Assert.Equal(HttpTransport.UserAgent, handler.UserAgent);

        var missing = Path.Combine(_dir, "b.zip");
        using var notFound = new HttpTransport(new DownloadHandler(404, "nope"u8.ToArray()));
        Assert.Equal(404, await notFound.DownloadToFileAsync("https://x/b.zip", missing));
        Assert.False(File.Exists(missing));
    }

    [Fact(DisplayName = "a connection refused maps to the could-not-connect hint")]
    public async Task ConnectionRefused()
    {
        // Port 9 (discard) refuses immediately on a machine without the service.
        using var transport = new HttpTransport();
        var ex = await Assert.ThrowsAsync<ApiException>(() => transport.SendAsync("GET", "http://127.0.0.1:9/api/me"));
        Assert.Equal(TransportFailure.ConnectFailed, ex.Failure);
    }

    [Theory(DisplayName = "handler failures map to transport failure classes")]
    [InlineData(HttpRequestError.NameResolutionError, TransportFailure.AddressNotFound)]
    [InlineData(HttpRequestError.ConnectionError, TransportFailure.ConnectFailed)]
    [InlineData(HttpRequestError.SecureConnectionError, TransportFailure.Tls)]
    [InlineData(HttpRequestError.ResponseEnded, TransportFailure.Other)]
    public async Task ErrorClasses(HttpRequestError error, TransportFailure expected)
    {
        using var transport = new HttpTransport(new ThrowingHandler(() => new HttpRequestException(error, "boom")));
        var ex = await Assert.ThrowsAsync<ApiException>(() => transport.SendAsync("GET", "https://x/api/me"));
        Assert.Equal(expected, ex.Failure);
        Assert.DoesNotContain("-> ", ex.Message);
    }

    [Fact(DisplayName = "inner socket and TLS exceptions refine the class")]
    public async Task InnerExceptions()
    {
        using var dns = new HttpTransport(new ThrowingHandler(() =>
            new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))));
        Assert.Equal(TransportFailure.AddressNotFound,
            (await Assert.ThrowsAsync<ApiException>(() => dns.SendAsync("GET", "https://x/"))).Failure);
        using var tls = new HttpTransport(new ThrowingHandler(() =>
            new HttpRequestException("x", new AuthenticationException("cert"))));
        Assert.Equal(TransportFailure.Tls, (await Assert.ThrowsAsync<ApiException>(() => tls.SendAsync("GET", "https://x/"))).Failure);
    }

    [Fact(DisplayName = "a stalled server times out as an api-error with the timeout hint")]
    public async Task Timeout()
    {
        var quick = new HttpTimeouts(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        using var transport = new HttpTransport(new StallingHandler(), normal: quick);
        var ex = await Assert.ThrowsAsync<ApiException>(() => transport.SendAsync("GET", "https://x/"));
        Assert.Equal(TransportFailure.Timeout, ex.Failure);
    }

    [Fact(DisplayName = "caller cancellation surfaces as OperationCanceledException, not an api-error")]
    public async Task Cancellation()
    {
        using var transport = new HttpTransport(new StallingHandler());
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync("GET", "https://x/", cancellationToken: cts.Token));
    }

    [Theory(DisplayName = "bad URLs are api-errors (the junk port too, unlike parse-url)")]
    [InlineData("example.com/x")]
    [InlineData("http://h:abc/")]
    [InlineData("ftp://h/")]
    [InlineData("https:///x")]
    public async Task BadUrls(string url)
    {
        using var transport = new HttpTransport(FakeHandler.Always(200));
        var ex = await Assert.ThrowsAsync<ApiException>(() => transport.SendAsync("GET", url));
        Assert.StartsWith("Bad URL", ex.Message);
    }

    [Fact(DisplayName = "the response body is decoded as UTF-8")]
    public async Task Utf8Body()
    {
        using var transport = new HttpTransport(FakeHandler.Always(200, "テスト"));
        Assert.Equal(new HttpResult(200, "テスト"), await transport.SendAsync("GET", "https://x/"));
    }

    [Fact(DisplayName = "end to end over real sockets: headers, body, status")]
    public async Task RealSockets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var head = new StringBuilder();
            var buffer = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(buffer) == 0) break;
                head.Append((char)buffer[0]);
            }
            var headers = head.ToString();
            var length = int.Parse(headers.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Split(':')[1].Trim());
            var body = new byte[length];
            var read = 0;
            while (read < length) read += await stream.ReadAsync(body.AsMemory(read));
            var reply = Encoding.UTF8.GetBytes("{\"ok\":\"あ\"}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 201 Created\r\nContent-Type: application/json\r\nContent-Length: {reply.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(reply);
            return (headers, Encoding.UTF8.GetString(body));
        });
        using var transport = new HttpTransport();
        var result = await transport.SendAsync("POST", $"http://127.0.0.1:{port}/api/pair", """{"label":"é"}""", "eta_t");
        var (seenHeaders, seenBody) = await server;
        Assert.Equal(new HttpResult(201, """{"ok":"あ"}"""), result);
        Assert.Equal("""{"label":"é"}""", seenBody);
        Assert.Contains("User-Agent: ephinea-ta-client\r\n", seenHeaders);
        Assert.Contains("Authorization: Bearer eta_t\r\n", seenHeaders);
        Assert.Contains("Content-Type: application/json\r\n", seenHeaders);
        Assert.StartsWith("POST /api/pair HTTP/1.1", seenHeaders);
    }

    private sealed class DownloadHandler(int status, byte[] body) : HttpMessageHandler
    {
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgent = string.Join(" ", request.Headers.GetValues("User-Agent"));
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
        }
    }

    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
