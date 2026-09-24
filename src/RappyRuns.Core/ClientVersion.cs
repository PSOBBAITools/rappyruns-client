using System.Reflection;

namespace RappyRuns.Core;

/// <summary>
/// The client's own version and semver comparison for the self-updater
/// (port of client/src/version.lisp, spec core §10.3).
/// </summary>
public static class ClientVersion
{
    /// <summary>
    /// Version baked in by a release build (e.g. "1.0.0"); null in a dev
    /// build, which therefore never offers to replace itself.
    /// </summary>
    public static string? Current { get; } = typeof(ClientVersion).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "RappyRunsClientVersion")?.Value;

    /// <summary>The version to show humans; "dev" when running from source.</summary>
    public static string Display => Current ?? "dev";

    /// <summary>
    /// "v1.2.3" or "1.2.3" -> [1, 2, 3]; null for anything else. Exactly three
    /// non-negative components: suffixes like -rc1 are rejected so a malformed
    /// tag can never look newer than a real release.
    /// </summary>
    public static int[]? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var body = text[0] is 'v' or 'V' ? text[1..] : text;
        var parts = body.Split('.');
        if (parts.Length != 3) return null;
        var result = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)) return null;
            if (!int.TryParse(parts[i], out result[i])) return null;
        }
        return result;
    }

    /// <summary>
    /// True only when both versions parse and <paramref name="current"/> is
    /// numerically lower than <paramref name="latest"/>. Anything malformed,
    /// including a null dev version, falls on the do-not-update side.
    /// </summary>
    public static bool UpdateAvailable(string? current, string? latest)
    {
        var a = Parse(current);
        var b = Parse(latest);
        if (a is null || b is null) return false;
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] < b[i];
        }
        return false;
    }
}
