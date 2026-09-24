using System.Text.Json.Nodes;
using RappyRuns.Core.Api;

namespace RappyRuns.Core.Update;

/// <summary>
/// Self-update constants (spec core §10.1). These are contracts shared with the Lisp
/// client, the release pipeline and the site's download button.
/// </summary>
public static class UpdateConstants
{
    /// <summary>
    /// owner/name of the public repository whose releases carry the client. The old
    /// names (psobb-teapot/rappyruns-client, rappyruns-client-releases,
    /// ephinea-ta-client-releases) live on as GitHub redirects that pre-move clients
    /// depend on: never create or fork a repository under those names.
    /// </summary>
    public const string DefaultRepo = "PSOBBAITools/rappyruns-client";

    /// <summary>The release asset (also what the site's download button points at).</summary>
    public const string AssetName = "RappyRunsClient.zip";

    /// <summary>The canonical exe name; updates always install under it, whatever the running exe is called.</summary>
    public const string ExeName = "RappyRunsClient.exe";

    /// <summary>The downloaded zip's file name in %TEMP%.</summary>
    public const string ZipFileName = "RappyRunsClient-update.zip";

    /// <summary>The extraction folder in %TEMP%.</summary>
    public const string StageDirName = "rappyruns-update-stage";

    /// <summary>The Lisp updater's helper script in %TEMP% (swept at startup; C# never writes it).</summary>
    public const string LegacyScriptName = "rappyruns-update.ps1";

    /// <summary>The probe file that tests whether the install folder accepts writes.</summary>
    public const string WriteProbeName = "eta-write-probe.tmp";

    /// <summary>Suffix of the moved-aside running exe.</summary>
    public const string OldSuffix = ".old";

    /// <summary>
    /// <c>resolve-update-repo</c>: a non-empty config <c>:update-repo</c> override (for
    /// test releases in another repository), else <see cref="DefaultRepo"/>.
    /// </summary>
    public static string ResolveRepo(string? configured) =>
        string.IsNullOrEmpty(configured) ? DefaultRepo : configured;

    /// <summary><c>update-release-page-url</c>: the human release page for manual downloads.</summary>
    public static string ReleasePageUrl(string repo) => $"https://github.com/{repo}/releases/latest";

    /// <summary>The GitHub API URL of the latest (non-prerelease, non-draft) release.</summary>
    public static string LatestReleaseApiUrl(string repo) => $"https://api.github.com/repos/{repo}/releases/latest";
}

/// <summary>The latest release as far as the updater cares.</summary>
/// <param name="Tag">The release tag, e.g. "v1.0.0".</param>
/// <param name="AssetUrl">The <see cref="UpdateConstants.AssetName"/> download URL (from the same response as the tag).</param>
/// <param name="AssetSize">The asset's byte size when the API gave a positive integer; null skips the size check.</param>
public sealed record ReleaseInfo(string Tag, string AssetUrl, long? AssetSize)
{
    /// <summary>
    /// <c>parse-release-json</c>: a GitHub /releases/latest body → the release, or null
    /// when it is not JSON, has no string <c>tag_name</c>, no <c>assets</c> array, or no
    /// asset named exactly "RappyRunsClient.zip" with a string download URL. The first
    /// asset with that name decides (a later duplicate is not considered).
    /// </summary>
    public static ReleaseInfo? Parse(string? body)
    {
        var release = ApiJson.TryParse(body);
        if (release is not JsonObject) return null;
        var tag = ApiJson.GetString(release, "tag_name");
        if (tag is null || ApiJson.Get(release, "assets") is not JsonArray assets) return null;
        foreach (var asset in assets)
        {
            if (asset is not JsonObject || ApiJson.GetString(asset, "name") != UpdateConstants.AssetName) continue;
            var url = ApiJson.GetString(asset, "browser_download_url");
            if (url is null) return null;
            var size = ApiJson.GetInteger(asset, "size");
            return new ReleaseInfo(tag, url, size is > 0 ? size : null);
        }
        return null;
    }
}

/// <summary><c>startup-update-decision</c> values.</summary>
public enum UpdateDecision
{
    /// <summary>No release could be fetched (offline, rate limited, none published). Silent at startup.</summary>
    CheckFailed,

    /// <summary>Not newer, or a dev build.</summary>
    UpToDate,

    /// <summary>Newer, but the install folder refuses writes → offer the manual download page.</summary>
    NotWritable,

    /// <summary>Download and hand over before any window shows.</summary>
    Apply,
}

/// <summary>Pure update policy (updater.lisp), kept separate so tests pin its boundaries.</summary>
public static class UpdatePolicy
{
    /// <summary>
    /// <c>startup-update-decision</c>: null release → CheckFailed; not newer (or dev
    /// build, <paramref name="currentVersion"/> null) → UpToDate; not writable →
    /// NotWritable; otherwise Apply.
    /// </summary>
    public static UpdateDecision StartupDecision(ReleaseInfo? release, string? currentVersion, bool writable)
    {
        if (release is null) return UpdateDecision.CheckFailed;
        if (!ClientVersion.UpdateAvailable(currentVersion, release.Tag)) return UpdateDecision.UpToDate;
        return writable ? UpdateDecision.Apply : UpdateDecision.NotWritable;
    }
}
