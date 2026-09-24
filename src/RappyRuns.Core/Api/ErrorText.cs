using RappyRuns.Core.I18n;

namespace RappyRuns.Core.Api;

/// <summary>
/// Human wording for failed server and token checks (api-client.lisp:32-80,
/// spec core §6.1): a plain-language hint instead of the raw condition.
/// </summary>
public static class ErrorText
{
    /// <summary>
    /// <c>windows-error-code</c>: N from a "... (Windows error N)" message, else null.
    /// Kept so messages that still carry a WinHTTP code (e.g. relayed from the Lisp
    /// era or a WinHTTP-based component) classify the same way.
    /// </summary>
    public static int? WindowsErrorCode(string message)
    {
        const string marker = "(Windows error ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        var i = start + marker.Length;
        while (i < message.Length && char.IsWhiteSpace(message[i])) i++;
        var sign = 1;
        if (i < message.Length && message[i] is '+' or '-')
        {
            if (message[i] == '-') sign = -1;
            i++;
        }
        var digits = 0;
        long value = 0;
        while (i < message.Length && char.IsAsciiDigit(message[i]) && digits < 10)
        {
            value = value * 10 + (message[i] - '0');
            digits++;
            i++;
        }
        return digits == 0 ? null : (int)(sign * value);
    }

    /// <summary><c>connection-error-hint</c>: the i18n key for a WinHTTP error code, or null.</summary>
    public static string? HintKey(int? windowsErrorCode) => windowsErrorCode switch
    {
        12007 => "hint-address",
        12029 or 12030 => "hint-connect",
        12002 => "hint-timeout",
        12157 or 12175 => "hint-tls",
        _ => null,
    };

    /// <summary>The i18n hint key for a .NET transport failure class, or null.</summary>
    public static string? HintKey(TransportFailure failure) => failure switch
    {
        TransportFailure.AddressNotFound => "hint-address",
        TransportFailure.ConnectFailed => "hint-connect",
        TransportFailure.Timeout => "hint-timeout",
        TransportFailure.Tls => "hint-tls",
        _ => null,
    };

    private static string? Hint(Language language, ApiException error)
    {
        var key = HintKey(error.Failure) ?? HintKey(WindowsErrorCode(error.Message));
        return key is null ? null : Strings.Default.Tr(language, key);
    }

    /// <summary>
    /// <c>server-status-error-text</c>: the server-status line for a failed server
    /// check. API errors get a hint when the cause is a known transport failure,
    /// point at the Save &amp; verify fix for "Bad URL", and call a "-> status"
    /// message an unexpected response; any other exception reads "check failed".
    /// </summary>
    public static string ServerStatus(Language language, Exception error)
    {
        var s = Strings.Default;
        if (error is not ApiException api) return s.Tr(language, "server-check-failed", error.Message);
        var message = api.Message;
        var hint = Hint(language, api);
        if (hint is not null) return s.Tr(language, "server-error-prefix", hint);
        if (message.Contains("Bad URL", StringComparison.Ordinal)) return s.Tr(language, "server-bad-url");
        if (message.Contains("-> ", StringComparison.Ordinal)) return s.Tr(language, "server-unexpected", message);
        return s.Tr(language, "server-error-prefix", message);
    }

    /// <summary>
    /// <c>token-status-error-text</c>: the token-status line when /api/me could not
    /// be reached at all (a definite 401 is worded by the caller).
    /// </summary>
    public static string TokenStatus(Language language, Exception error)
    {
        var detail = error is ApiException api ? Hint(language, api) ?? api.Message : error.Message;
        return Strings.Default.Tr(language, "token-could-not-verify", detail);
    }
}
