using System.Globalization;
using System.Text.Json;

namespace RappyRuns.Core.PinShare;

/// <summary>A ws:// / wss:// URL split the way the Lisp client did (api-client.lisp:88).</summary>
public sealed record WebSocketUrl(bool Secure, string Host, int Port, string Path);

/// <summary>
/// Pin Share settings, paths and the rollout gate
/// (pinshare.lisp:487-540).
/// </summary>
public static class PinShareSettings
{
    /// <summary>The public relay server, used when <c>:pinshare-server</c> is blank.</summary>
    public const string DefaultServer = "wss://pin-share-server-production.up.railway.app";

    /// <summary>The passphrase length the server keeps.</summary>
    public const int MaxChannelChars = 64;

    /// <summary>
    /// The configured passphrase (<c>:pinshare-channel</c>), cleaned the way
    /// the server will clean it: control characters out, spaces trimmed, 64
    /// characters (Lisp <c>pinshare-channel</c>, pinshare.lisp:523).
    /// </summary>
    public static string Channel(string? configured) =>
        PinShareText.TakeChars(PinShareText.Clean(configured).Trim(' '), MaxChannelChars);

    /// <summary>The relay server URL: <c>:pinshare-server</c> when set, else the public relay (pinshare.lisp:530).</summary>
    public static string ServerUrl(string? configured) =>
        string.IsNullOrEmpty(configured) ? DefaultServer : configured;

    /// <summary>addons\Pin Share\ next to the game exe at <paramref name="gameExePath"/> (pinshare.lisp:536).</summary>
    public static string AddonDir(string gameExePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameExePath)) ?? "", "addons", "Pin Share");

    /// <summary>
    /// True when the <c>/api/me</c> <paramref name="user"/> lists the
    /// "pinshare" feature. A server that predates the field, a non-array
    /// value, or an unlinked client (null) means no (pinshare.lisp:509).
    /// </summary>
    public static bool FeatureAllowed(JsonElement? user)
    {
        if (user is not { } u || JsonItem.From(u)?.Get("features") is not { ValueKind: JsonValueKind.Array } features) return false;
        return features.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == "pinshare");
    }

    /// <summary>
    /// (secure, host, port, path) for a ws:// or wss:// URL, with the
    /// http(s) default ports 80 / 443 and "/" for no path (Lisp
    /// <c>parse-websocket-url</c> over <c>parse-url</c>). Throws
    /// <see cref="FormatException"/> for another scheme or a bad port.
    /// </summary>
    public static WebSocketUrl ParseWebSocketUrl(string url)
    {
        bool secure;
        string rest;
        if (url.StartsWith("wss://", StringComparison.Ordinal))
        {
            secure = true;
            rest = url[6..];
        }
        else if (url.StartsWith("ws://", StringComparison.Ordinal))
        {
            secure = false;
            rest = url[5..];
        }
        else
        {
            throw new FormatException($"Bad URL: {url}");
        }
        var pathStart = rest.IndexOf('/');
        var authority = pathStart >= 0 ? rest[..pathStart] : rest;
        var path = pathStart >= 0 ? rest[pathStart..] : "/";
        var colon = authority.IndexOf(':');
        var host = colon >= 0 ? authority[..colon] : authority;
        var port = colon >= 0
            ? int.Parse(authority[(colon + 1)..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : secure ? 443 : 80;
        return new WebSocketUrl(secure, host, port, path);
    }
}
