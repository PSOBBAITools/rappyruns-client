namespace RappyRuns.Core.Api;

/// <summary>
/// Token rules shared by the HTTP code and the settings UI (spec core §8.1).
/// </summary>
public static class Tokens
{
    private static readonly char[] TrimChars = [' ', '\t', '\r', '\n'];

    /// <summary>
    /// <c>normalize-token</c>: a pasted token trimmed of the spaces, tabs and line
    /// breaks that come along when copying from a browser; null becomes "".
    /// </summary>
    public static string Normalize(string? token) => (token ?? "").Trim(TrimChars);

    /// <summary>
    /// <c>submission-token</c>: the token runs, uploads, diagnostics and ghost
    /// fetches go out with — the linked account's token when set, else the
    /// anonymous guest token, else "".
    /// </summary>
    public static string Submission(string? apiToken, string? anonToken)
    {
        var real = Normalize(apiToken);
        return real.Length > 0 ? real : Normalize(anonToken);
    }

    /// <summary>
    /// <c>unlinked-p</c>: no linked account. A guest token does not count as linked.
    /// </summary>
    public static bool IsUnlinked(string? apiToken) => Normalize(apiToken).Length == 0;
}
