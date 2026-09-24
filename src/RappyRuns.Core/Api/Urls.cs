using System.Globalization;
using System.Text;

namespace RappyRuns.Core.Api;

/// <summary>A URL split the way the Lisp client's <c>parse-url</c> splits it.</summary>
/// <param name="Scheme">Lower-cased scheme ("http", "https").</param>
/// <param name="Host">Host name without the port.</param>
/// <param name="Port">Explicit port, else 443 for https and 80 otherwise.</param>
/// <param name="Path">Everything from the first '/' after the authority (query included); "/" when absent.</param>
public sealed record UrlParts(string Scheme, string Host, int Port, string Path);

/// <summary>A ws:// or wss:// URL split for the Pin Share WebSocket (<c>parse-websocket-url</c>).</summary>
public sealed record WebSocketUrlParts(bool Secure, string Host, int Port, string Path);

/// <summary>
/// URL helpers shared by every request (port of api-client.lisp: <c>api-url</c>,
/// <c>parse-url</c>, <c>valid-http-url-p</c>, <c>url-encode-component</c>, and the
/// path builders; spec core §6.2, §7.4).
/// </summary>
public static class Urls
{
    /// <summary>
    /// <c>api-url</c>: the server URL with every trailing '/' removed, then
    /// <paramref name="path"/> appended verbatim. "https://x/" + "/api/me" is
    /// "https://x/api/me"; an empty path leaves the bare trimmed server URL.
    /// </summary>
    public static string ApiUrl(string serverUrl, string path) => serverUrl.TrimEnd('/') + path;

    /// <summary>
    /// <c>parse-url</c>: <c>scheme://host[:port][/path]</c>. A URL without "://"
    /// throws <see cref="ApiException"/> "Bad URL: ...". A non-numeric port throws
    /// <see cref="FormatException"/> — deliberately NOT an ApiException, pinned by the
    /// Lisp test "parse-url junk port signals a non-api error (pinned)". The HTTP
    /// transport wraps that case into an ApiException (see <see cref="HttpTransport"/>).
    /// </summary>
    public static UrlParts Parse(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) throw new ApiException($"Bad URL: {url}");
        var scheme = url[..schemeEnd].ToLowerInvariant();
        var rest = url[(schemeEnd + 3)..];
        var pathStart = rest.IndexOf('/');
        var authority = pathStart >= 0 ? rest[..pathStart] : rest;
        var path = pathStart >= 0 ? rest[pathStart..] : "/";
        var colon = authority.IndexOf(':');
        var host = colon >= 0 ? authority[..colon] : authority;
        int port;
        if (colon >= 0)
        {
            // CL PARSE-INTEGER: optional surrounding whitespace and sign, digits only.
            var text = authority[(colon + 1)..].Trim();
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out port))
                throw new FormatException($"junk in port of {url}");
        }
        else
        {
            port = scheme == "https" ? 443 : 80;
        }
        return new UrlParts(scheme, host, port, path);
    }

    /// <summary>
    /// <c>parse-websocket-url</c>: ws:// and wss:// with the http(s) default ports.
    /// Anything else throws <see cref="ApiException"/> "Bad URL: ...".
    /// </summary>
    public static WebSocketUrlParts ParseWebSocket(string url)
    {
        string httpUrl;
        if (url.StartsWith("wss://", StringComparison.Ordinal)) httpUrl = "https" + url[3..];
        else if (url.StartsWith("ws://", StringComparison.Ordinal)) httpUrl = "http" + url[2..];
        else throw new ApiException($"Bad URL: {url}");
        var parts = Parse(httpUrl);
        return new WebSocketUrlParts(parts.Scheme == "https", parts.Host, parts.Port, parts.Path);
    }

    /// <summary>
    /// <c>valid-http-url-p</c>: only http(s) URLs without whitespace or control
    /// characters (anything ≤ U+0020) may be handed to the shell to open in a
    /// browser, so a mangled Server URL setting can never reach ShellExecute.
    /// </summary>
    public static bool IsValidHttpUrl(string? url) =>
        url is not null
        && (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal))
        && url.All(c => c > ' ');

    /// <summary>
    /// <c>url-encode-component</c>: UTF-8 percent-encoding for a query value;
    /// [A-Za-z0-9-_.~] pass through, every other byte becomes %XX (upper-case hex).
    /// </summary>
    public static string EncodeComponent(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary><c>pairing-url</c>: the browser page that approves a pairing code (code sent raw).</summary>
    public static string PairingUrl(string serverUrl, string code) => ApiUrl(serverUrl, $"/pair?code={code}");

    /// <summary>
    /// <c>video-file-path</c>: "/api/runs/{id}/video-file", with "?offset_ms=N" only
    /// when the recorder measured the offset.
    /// </summary>
    public static string VideoFilePath(long serverId, long? offsetMs) =>
        offsetMs is { } offset
            ? string.Create(CultureInfo.InvariantCulture, $"/api/runs/{serverId}/video-file?offset_ms={offset}")
            : string.Create(CultureInfo.InvariantCulture, $"/api/runs/{serverId}/video-file");

    /// <summary>
    /// The <c>fetch-ghost-splits</c> path (spec core §7.4): "/api/quests/{slug}/ghost"
    /// plus, joined by '&amp;' after one '?', only the parameters given, in this order:
    /// <c>slugs</c> (comma-joined, raw — slugs are [a-z0-9-]), <c>difficulty</c>
    /// (url-encoded), <c>party_size</c>, <c>pb</c>. No parameters, no '?'.
    /// An empty <paramref name="extraSlugs"/> list counts as absent (Lisp NIL).
    /// </summary>
    public static string GhostPath(string slug, IReadOnlyList<string>? extraSlugs = null, string? difficulty = null,
        int? partySize = null, int? pb = null)
    {
        var parameters = new List<string>();
        if (extraSlugs is { Count: > 0 }) parameters.Add("slugs=" + string.Join(",", extraSlugs));
        if (difficulty is not null) parameters.Add("difficulty=" + EncodeComponent(difficulty));
        if (partySize is { } size) parameters.Add(string.Create(CultureInfo.InvariantCulture, $"party_size={size}"));
        if (pb is { } p) parameters.Add(string.Create(CultureInfo.InvariantCulture, $"pb={p}"));
        var path = $"/api/quests/{slug}/ghost";
        return parameters.Count > 0 ? path + "?" + string.Join("&", parameters) : path;
    }

    /// <summary>
    /// The <c>fetch-pin-set</c> path: "/api/quests/{slug}/pins", plus "?slugs=a,b"
    /// when the loaded quest has other category slugs.
    /// </summary>
    public static string PinsPath(string slug, IReadOnlyList<string>? extraSlugs = null) =>
        extraSlugs is { Count: > 0 }
            ? $"/api/quests/{slug}/pins?slugs={string.Join(",", extraSlugs)}"
            : $"/api/quests/{slug}/pins";
}
