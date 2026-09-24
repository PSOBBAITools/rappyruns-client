namespace RappyRuns.Core.Store;

/// <summary>
/// Common Lisp universal time: whole seconds since 1900-01-01 00:00:00 UTC.
/// queue.sexp stores <c>:finished-at</c> and <c>:next-upload-at</c> this way
/// (spec core §3.3), so the queue stays readable by the Lisp client.
/// </summary>
public static class UniversalTime
{
    /// <summary>Universal time of the Unix epoch: Unix seconds = UT - this.</summary>
    public const long UnixEpoch = 2208988800;

    /// <summary>get-universal-time.</summary>
    public static long Now() => FromDateTimeOffset(DateTimeOffset.UtcNow);

    public static long FromDateTimeOffset(DateTimeOffset time) => time.ToUnixTimeSeconds() + UnixEpoch;

    public static DateTimeOffset ToDateTimeOffset(long universalTime) =>
        DateTimeOffset.FromUnixTimeSeconds(universalTime - UnixEpoch);

    public static long ToUnixSeconds(long universalTime) => universalTime - UnixEpoch;

    public static long FromUnixSeconds(long unixSeconds) => unixSeconds + UnixEpoch;
}
