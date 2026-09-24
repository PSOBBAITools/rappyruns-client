using System.Text;

namespace RappyRuns.Core.Config;

/// <summary>A login.txt username/password pair.</summary>
public sealed record LoginCredentials(string Username, string Password);

/// <summary>
/// login.txt: the file-based alternative to browser pairing (credentials.lisp,
/// spec core §8.3). The user sets a client password on the site and drops
///
/// <code>
/// username=TheirDiscordName
/// password=their-client-password
/// </code>
///
/// next to the exe; at startup the client exchanges the pair for an API token
/// over POST /api/login (the API/UI layers run that flow).
/// </summary>
public static class Credentials
{
    public const string FileName = "login.txt";

    private static readonly char[] Junk = [' ', '\t', '\r', '﻿'];

    /// <summary>
    /// The folder next to the running exe (the Lisp client used the image
    /// path; spec core §2.3 allows <see cref="Environment.ProcessPath"/>).
    /// </summary>
    public static string DefaultDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>credentials-path: <c>&lt;exeDir&gt;\login.txt</c>.</summary>
    public static string PathIn(string exeDirectory) => Path.Combine(exeDirectory, FileName);

    /// <summary>credentials-present-p.</summary>
    public static bool Present(string exeDirectory)
    {
        try
        {
            return File.Exists(PathIn(exeDirectory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// parse-credentials (credentials.lisp:34): KEY=VALUE lines, tolerant of
    /// what Notepad produces - a UTF-8 BOM, CRLF line ends, blank lines,
    /// #-comment lines and spaces around keys. Only the first = splits, so
    /// passwords may contain =. Later lines win. Null unless both values are
    /// present and non-empty.
    /// </summary>
    public static LoginCredentials? Parse(string? text)
    {
        string? username = null;
        string? password = null;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim(Junk);
            if (line.Length == 0 || line[0] == '#') continue;
            var separator = line.IndexOf('=');
            if (separator < 0) continue;
            var key = line[..separator].Trim(Junk).ToLowerInvariant();
            var value = line[(separator + 1)..].Trim(Junk);
            if (key == "username") username = value;
            else if (key == "password") password = value;
        }
        return username is { Length: > 0 } && password is { Length: > 0 }
            ? new LoginCredentials(username, password)
            : null;
    }

    /// <summary>
    /// read-credentials: <see cref="Parse"/> of the file as strict UTF-8;
    /// null when the file is absent or unreadable (e.g. saved as Shift-JIS).
    /// </summary>
    public static LoginCredentials? Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path, new UTF8Encoding(false, throwOnInvalidBytes: true)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }
}
