using System.Runtime.CompilerServices;
using System.Text;
using RappyRuns.Core.Sexp;

[assembly: InternalsVisibleTo("RappyRuns.Tests")]
[assembly: InternalsVisibleTo("RappyRuns.Win")]

namespace RappyRuns.Core.Media;

/// <summary>
/// The handful of Common Lisp semantics the recording code depends on and C#
/// does not share: exact rational <c>round</c> (half to even) and <c>floor</c>
/// (toward negative infinity), how <c>~a</c>/<c>~s</c> print the values the
/// recording log shows, and <c>getf</c> over a run plist where only NIL is
/// false (media spec, notation).
/// </summary>
internal static class Lisp
{
    /// <summary>CL <c>(floor n d)</c> for a positive divisor: rounds toward -inf, unlike C# <c>/</c>.</summary>
    public static long Floor(long n, long d)
    {
        var q = n / d;
        if (n % d != 0 && (n < 0) != (d < 0)) q--;
        return q;
    }

    /// <summary>CL <c>(mod n d)</c>: the remainder matching <see cref="Floor"/> (sign of the divisor).</summary>
    public static long Mod(long n, long d) => n - Floor(n, d) * d;

    /// <summary>
    /// CL <c>(round n d)</c> on exact integers: the nearest integer to n/d,
    /// ties to even. Computed without floating point so the capture's elapsed
    /// milliseconds (recording.lisp:1131) match the Lisp value exactly.
    /// </summary>
    public static long Round(long n, long d)
    {
        if (d < 0) { n = -n; d = -d; }
        var q = Floor(n, d);
        var r = n - q * d;           // 0 <= r < d
        var twice = 2 * r;
        if (twice > d || (twice == d && (q & 1) != 0)) q++;
        return q;
    }

    /// <summary>How <c>~a</c> prints a generalized boolean: T or NIL.</summary>
    public static string Bool(bool value) => value ? "T" : "NIL";

    /// <summary><c>~a</c> of an optional integer: the digits, or NIL.</summary>
    public static string Opt(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NIL";

    /// <summary><c>~a</c> of an optional string: the text, or NIL.</summary>
    public static string Opt(string? value) => value ?? "NIL";

    /// <summary><c>~s</c> of a string: double quotes, with <c>\</c> and <c>"</c> escaped.</summary>
    public static string Prin1(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c is '\\' or '"') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary><c>~a</c> of a list of integers: "(1 2 3)".</summary>
    public static string List(params long[] items) =>
        "(" + string.Join(' ', items.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")";

    /// <summary><c>(getf plist key)</c> as a Lisp truth value: present and not NIL.</summary>
    public static bool Truthy(Plist plist, string key) => plist.Get(key) is { IsTrue: true };

    /// <summary><c>(getf plist key)</c> when it is an integer, else null (absent, NIL or another type).</summary>
    public static long? GetLong(Plist plist, string key) => plist.Get(key)?.AsLong;

    /// <summary>
    /// <c>~a</c> of a plist value the way a filename or log line shows it: a
    /// string verbatim, anything else in its printed form; null for absent/NIL.
    /// </summary>
    public static string? Princ(Plist plist, string key)
    {
        var node = plist.Get(key);
        if (node is null || node.IsNil) return null;
        return node switch
        {
            SString s => s.Value,
            SKeyword k => k.Name,
            // SexpWriter, not node.ToString(): the derived records' generated
            // ToString walks PrintMembers into Elements and recurses.
            _ => SexpWriter.Write(node),
        };
    }

    /// <summary>
    /// <c>(nconc plist (list key value))</c> on a shared entry: the key goes to
    /// the END, exactly where the Lisp client puts it (recording.lisp:1140),
    /// while <see cref="Plist.Set"/> would put a new key in front. The run plist
    /// is shared with the submission queue, so it is mutated in place.
    /// </summary>
    public static void Append(Plist plist, string key, SexpNode value)
    {
        var entries = plist.Keys.Select(k => (k, plist.Get(k)!)).ToList();
        foreach (var (k, _) in entries) plist.Remove(k);
        plist.Set(key, value);
        for (var i = entries.Count - 1; i >= 0; i--) plist.Set(entries[i].k, entries[i].Item2);
    }

    /// <summary>1900-01-01T00:00:00Z, the epoch of CL universal time.</summary>
    public static readonly DateTimeOffset UniversalEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>CL <c>decode-universal-time</c> in the local time zone.</summary>
    public static DateTime DecodeUniversalTimeLocal(long universalTime) =>
        UniversalEpoch.AddSeconds(universalTime).ToLocalTime().DateTime;

    /// <summary>CL <c>encode-universal-time</c> of a local wall-clock time.</summary>
    public static long EncodeUniversalTime(DateTime local) =>
        (long)Math.Floor((new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)) - UniversalEpoch).TotalSeconds);

    /// <summary>CL <c>get-universal-time</c>.</summary>
    public static long UniversalTimeNow() => (long)Math.Floor((DateTimeOffset.UtcNow - UniversalEpoch).TotalSeconds);
}
