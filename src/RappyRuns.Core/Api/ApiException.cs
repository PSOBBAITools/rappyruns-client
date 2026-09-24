namespace RappyRuns.Core.Api;

/// <summary>
/// Which transport-level failure an <see cref="ApiException"/> stands for, so the
/// UI can show a plain-language hint. Replaces the Lisp client's parsing of
/// "(Windows error N)" out of WinHTTP messages (api-client.lisp:40, spec core §6.1):
/// .NET reports these as typed <see cref="System.Net.Http.HttpRequestError"/>s instead.
/// </summary>
public enum TransportFailure
{
    /// <summary>Not a transport failure: a bad URL, an unexpected status, a refusal.</summary>
    None,

    /// <summary>DNS could not resolve the host (WinHTTP 12007).</summary>
    AddressNotFound,

    /// <summary>The connection was refused or dropped (WinHTTP 12029 / 12030).</summary>
    ConnectFailed,

    /// <summary>A phase timed out (WinHTTP 12002).</summary>
    Timeout,

    /// <summary>The TLS handshake or certificate check failed (WinHTTP 12157 / 12175).</summary>
    Tls,

    /// <summary>Any other I/O failure while talking to the server.</summary>
    Other,
}

/// <summary>
/// The Lisp <c>api-error</c> condition: every failure the caller should treat as
/// "worth retrying / reconfiguring" rather than a definite server verdict.
/// Messages keep the Lisp wording ("GET /api/quests -> 500", "Bad URL: ...",
/// "Invalid or revoked API token") because the UI classifies them by substring
/// (<see cref="ErrorText"/>).
/// </summary>
public sealed class ApiException : Exception
{
    /// <summary>Creates an API error with the Lisp-compatible message.</summary>
    public ApiException(string message, TransportFailure failure = TransportFailure.None, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    /// <summary>The transport failure class, or <see cref="TransportFailure.None"/>.</summary>
    public TransportFailure Failure { get; }

    /// <summary>The HTTP status that made the call fail, when there was one.</summary>
    public int? Status { get; init; }

    /// <summary>"Invalid or revoked API token" (401 on an authenticated endpoint).</summary>
    public static ApiException InvalidToken() => new("Invalid or revoked API token") { Status = 401 };
}
