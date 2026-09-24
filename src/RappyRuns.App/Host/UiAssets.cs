using System.Reflection;

namespace RappyRuns.App.Host;

/// <summary>
/// Serves the Svelte build embedded in the exe (resources named "ui/...")
/// under a private host name, so the single-file exe needs no folder on disk.
/// </summary>
internal static class UiAssets
{
    public const string Origin = "https://app.rappyruns.internal";

    private static readonly Assembly Assembly = typeof(UiAssets).Assembly;

    /// <summary>The embedded file for a request path, or null.</summary>
    public static Stream? Open(string path)
    {
        var relative = path.TrimStart('/');
        if (relative.Length == 0) relative = "index.html";
        // RecursiveDir in the csproj yields backslashes; requests use slashes.
        return Assembly.GetManifestResourceStream("ui/" + relative)
            ?? Assembly.GetManifestResourceStream("ui/" + relative.Replace('/', '\\'));
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or "" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        _ => "application/octet-stream",
    };
}
