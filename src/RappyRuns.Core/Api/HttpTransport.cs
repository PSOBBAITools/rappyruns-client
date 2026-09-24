using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace RappyRuns.Core.Api;

/// <summary>Status code and UTF-8 decoded body of a completed request.</summary>
public sealed record HttpResult(int Status, string Body);

/// <summary>
/// Per-phase timeouts, approximating WinHttpSetTimeouts (spec core §6).
/// WinHTTP's resolve and connect timeouts are folded into <see cref="Connect"/>
/// (applied as <see cref="SocketsHttpHandler.ConnectTimeout"/> by the default
/// handler, and as part of the headers deadline otherwise). <see cref="Send"/>
/// applies per written chunk, <see cref="Receive"/> per read chunk — like WinHTTP,
/// a long transfer is fine as long as it keeps moving.
/// </summary>
/// <param name="Connect">DNS + TCP connect (+ TLS) budget; WinHTTP 10 s + 10 s.</param>
/// <param name="Send">Budget per request-body write (and, for small bodies, for the whole send).</param>
/// <param name="Receive">Budget for the response headers after the body is sent, and per body read.</param>
public sealed record HttpTimeouts(TimeSpan Connect, TimeSpan Send, TimeSpan Receive)
{
    /// <summary>Ordinary API calls: 10+10 s / 30 s / 30 s (winhttp.lisp call-with-winhttp-request).</summary>
    public static HttpTimeouts Normal { get; } = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    /// <summary>
    /// Video uploads: 10+10 s / 60 s per write / 300 s receive — the server answers
    /// only after relaying the last chunk to storage (winhttp.lisp winhttp-upload-file).
    /// </summary>
    public static HttpTimeouts Upload { get; } = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300));
}

/// <summary>
/// The HTTP layer (port of winhttp.lisp + api-client.lisp <c>http-request</c>, spec
/// core §6): User-Agent "ephinea-ta-client", system proxy, OS certificate store,
/// redirects followed, UTF-8 bodies, NO retries (callers own retry policy).
/// Every failure that is not an HTTP status surfaces as <see cref="ApiException"/>
/// with a <see cref="TransportFailure"/> class; cancellation through the caller's
/// token surfaces as <see cref="OperationCanceledException"/>.
/// Thread-safe; share one instance. The handler is injectable for tests.
/// </summary>
public sealed class HttpTransport : IDisposable
{
    /// <summary>
    /// The agent string WinHttpOpen used. Required: the GitHub API answers 403 to
    /// requests without a User-Agent, and HttpClient sends none by default.
    /// </summary>
    public const string UserAgent = "ephinea-ta-client";

    private const int ReadChunk = 8192;
    private const int DownloadChunk = 65536;
    private const int UploadChunk = 1024 * 1024;

    private readonly HttpClient _client;
    private readonly HttpTimeouts _normal;
    private readonly HttpTimeouts _upload;

    /// <summary>A transport over <see cref="CreateDefaultHandler"/>.</summary>
    public HttpTransport() : this(CreateDefaultHandler(), true)
    {
    }

    /// <summary>
    /// A transport over <paramref name="handler"/> (tests pass a fake). Timeouts
    /// default to <see cref="HttpTimeouts.Normal"/> / <see cref="HttpTimeouts.Upload"/>.
    /// </summary>
    public HttpTransport(HttpMessageHandler handler, bool disposeHandler = true,
        HttpTimeouts? normal = null, HttpTimeouts? upload = null)
    {
        _client = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _normal = normal ?? HttpTimeouts.Normal;
        _upload = upload ?? HttpTimeouts.Upload;
    }

    /// <summary>
    /// The production handler: system proxy (WinHTTP's AUTOMATIC_PROXY
    /// equivalent), automatic redirects (GitHub's renamed-repo 301s and the asset
    /// CDN redirect depend on it), no decompression (WinHTTP did none), and the
    /// connect budget as <see cref="SocketsHttpHandler.ConnectTimeout"/>.
    /// Connections are pooled (WinHTTP opened one per request); harmless and faster.
    /// </summary>
    public static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = true,
        UseProxy = true,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = HttpTimeouts.Normal.Connect,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// <c>http-request</c>: a request with an optional UTF-8 <paramref name="body"/>.
    /// Adds <c>Authorization: Bearer</c> when <paramref name="token"/> is non-empty and
    /// <c>Content-Type</c> (exactly <paramref name="contentType"/>, no charset) only when
    /// there is a body; <paramref name="headers"/> are extra headers (e.g. GitHub's Accept).
    /// Any status is returned — mapping statuses to outcomes is the caller's job.
    /// </summary>
    public async Task<HttpResult> SendAsync(string method, string url, string? body = null, string? token = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null, string contentType = "application/json",
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(method, url, token, headers);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_normal.Connect + _normal.Send + _normal.Receive);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var bytes = await ReadBodyAsync(response, _normal.Receive, cts).ConfigureAwait(false);
            return new HttpResult((int)response.StatusCode, Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (Map(ex, method, url, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>
    /// <c>winhttp-upload-file</c>: streams <paramref name="filePath"/> as the request body
    /// in 1 MB chunks with Content-Length set from the file size, calling
    /// <paramref name="onProgress"/>(bytesSoFar, total) after every chunk. A file that
    /// shrinks or grows mid-upload fails with an ApiException rather than hanging the
    /// server. File-open errors (missing file) propagate as their IO exceptions.
    /// </summary>
    public async Task<HttpResult> UploadFileAsync(string method, string url, string filePath, string contentType,
        string? token = null, Action<long, long>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var total = new FileInfo(filePath).Length;
        // WinHttpSendRequest's total length was a DWORD; keep the same ceiling.
        if (total >= 1L << 32) throw new ApiException($"file too large to upload ({total} bytes)");
        using var request = CreateRequest(method, url, token, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var content = new FileUploadContent(filePath, total, onProgress, cts, _upload);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        request.Content = content;
        cts.CancelAfter(_upload.Connect + _upload.Send);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var bytes = await ReadBodyAsync(response, _upload.Receive, cts).ConfigureAwait(false);
            return new HttpResult((int)response.StatusCode, Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (Map(ex, method, url, cancellationToken) is { } mapped)
        {
            // Whether the body had started tells an upload failure from a
            // failure to reach the server at all (the queue counts only the former).
            throw content.Started && mapped is ApiException { BodyStarted: false } api
                ? new ApiException(api.Message, api.Failure, api.InnerException) { Status = api.Status, BodyStarted = true }
                : mapped;
        }
    }

    /// <summary>
    /// <c>winhttp-download</c>: GET <paramref name="url"/> and stream the body to
    /// <paramref name="targetPath"/> in 64 KB chunks. Only a 200 creates the file
    /// (other statuses leave nothing behind). <paramref name="onProgress"/> gets
    /// (bytesSoFar, Content-Length or null for chunked). Returns the status. A
    /// transport failure mid-body may leave a partial file; the caller deletes it.
    /// </summary>
    public async Task<int> DownloadToFileAsync(string url, string targetPath,
        IEnumerable<KeyValuePair<string, string>>? headers = null, Action<long, long?>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest("GET", url, null, headers);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_normal.Connect + _normal.Send + _normal.Receive);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status != 200) return status;
            var total = response.Content.Headers.ContentLength;
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[DownloadChunk];
            long written = 0;
            while (true)
            {
                cts.CancelAfter(_normal.Receive);
                var n = await body.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (n == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                written += n;
                onProgress?.Invoke(written, total);
            }
            return status;
        }
        catch (Exception ex) when (Map(ex, "GET", url, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    private static HttpRequestMessage CreateRequest(string method, string url, string? token,
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var uri = ToUri(url);
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (headers is not null)
        {
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        }
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Validates the URL the way <c>parse-url</c> does, but wraps the junk-port error
    /// into an ApiException "Bad URL" (the Lisp let a bare parse error escape past
    /// callers that only caught api-error — a crash risk, not worth keeping).
    /// </summary>
    private static Uri ToUri(string url)
    {
        UrlParts parts;
        try
        {
            parts = Urls.Parse(url);
        }
        catch (FormatException)
        {
            throw new ApiException($"Bad URL: {url}");
        }
        if (parts.Scheme is not ("http" or "https") || parts.Host.Length == 0
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ApiException($"Bad URL: {url}");
        return uri;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, TimeSpan perRead,
        CancellationTokenSource cts)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[ReadChunk];
        while (true)
        {
            cts.CancelAfter(perRead);
            var n = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (n == 0) break;
            memory.Write(buffer, 0, n);
        }
        return memory.ToArray();
    }

    /// <summary>Exception → ApiException (null keeps the original: caller cancellation, file errors).</summary>
    private static Exception? Map(Exception ex, string method, string url, CancellationToken callerToken)
    {
        string What() => $"{method} {SafePath(url)}";
        // HttpClient wraps errors thrown while streaming a request body.
        if (ex is not ApiException)
        {
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is ApiException api) return api;
            }
        }
        switch (ex)
        {
            case ApiException:
                return null;
            case OperationCanceledException when callerToken.IsCancellationRequested:
                return null;
            case OperationCanceledException:
                return new ApiException($"{What()} timed out", TransportFailure.Timeout, ex);
            case HttpRequestException hre:
                return new ApiException($"{What()} failed: {hre.Message}", Classify(hre), ex);
            case IOException io when io is not FileNotFoundException and not DirectoryNotFoundException:
                return new ApiException($"{What()} failed: {io.Message}", TransportFailure.Other, ex);
            case NotSupportedException or UriFormatException or InvalidOperationException:
                return new ApiException($"Bad URL: {url}", TransportFailure.None, ex);
            default:
                return null;
        }
    }

    private static string SafePath(string url)
    {
        try
        {
            return Urls.Parse(url).Path;
        }
        catch (Exception)
        {
            return url;
        }
    }

    private static TransportFailure Classify(HttpRequestException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return TransportFailure.AddressNotFound;
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                    return TransportFailure.Timeout;
                case AuthenticationException:
                    return TransportFailure.Tls;
            }
        }
        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => TransportFailure.AddressNotFound,
            HttpRequestError.ConnectionError => TransportFailure.ConnectFailed,
            HttpRequestError.SecureConnectionError => TransportFailure.Tls,
            _ => TransportFailure.Other,
        };
    }

    /// <summary>The streaming request body of an upload, with per-write timeouts and progress.</summary>
    private sealed class FileUploadContent(
        string path,
        long total,
        Action<long, long>? onProgress,
        CancellationTokenSource timer,
        HttpTimeouts timeouts) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                UploadChunk, useAsync: true);
            var buffer = new byte[UploadChunk];
            long sent = 0;
            while (true)
            {
                var n = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                if (sent + n > total) break;
                timer.CancelAfter(timeouts.Send);
                Started = true;
                await stream.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                sent += n;
                onProgress?.Invoke(sent, total);
            }
            if (sent != total)
                throw new ApiException($"file changed during upload (sent {sent} of {total} bytes)");
            // The long wait: the server relays the last chunk to storage before answering.
            timer.CancelAfter(timeouts.Receive);
        }

        /// <summary>The first chunk has been handed to the connection (volatile: set on the send path, read by the caller).</summary>
        public bool Started
        {
            get => Volatile.Read(ref _started);
            private set => Volatile.Write(ref _started, value);
        }

        private bool _started;

        protected override bool TryComputeLength(out long length)
        {
            length = total;
            return true;
        }
    }
}
