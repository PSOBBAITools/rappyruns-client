using System.Globalization;
using System.Text;

namespace RappyRuns.Core.PinShare;

/// <summary>
/// One complete out.txt line. <see cref="Fields"/> includes the seq field
/// itself, so the command is <c>Fields[1]</c> and its arguments follow - the
/// shape of the Lisp <c>(seq . fields)</c> conses.
/// </summary>
public sealed record OutboxLine(long Seq, IReadOnlyList<string> Fields)
{
    public string Command => Fields[1];

    /// <summary>The arguments after the command (Lisp <c>(cddr fields)</c>).</summary>
    public IReadOnlyList<string> Args => Fields.Count > 2 ? [.. Fields.Skip(2)] : [];

    /// <summary>Test/diagnostic convenience: a line from its fields (seq parsed from the first).</summary>
    public static OutboxLine Of(long seq, params string[] fields) => new(seq, fields);
}

/// <summary>
/// exchange\out.txt, addon → relay: one command per line,
/// <c>&lt;seq&gt;\t&lt;command&gt;\t&lt;args...&gt;</c>, seq only growing
/// (the addon starts it from <c>os.time()*1000</c>).
/// </summary>
public static class Outbox
{
    /// <summary>
    /// out.txt <paramref name="text"/> → its lines, oldest first (Lisp
    /// <c>parse-pinshare-outbox</c>, pinshare.lisp:67). The addon may be
    /// mid-write: only lines already terminated by a newline count, a CR
    /// before it is dropped, and lines without an integer seq and a command
    /// are skipped.
    /// </summary>
    public static List<OutboxLine> Parse(string text)
    {
        var result = new List<OutboxLine>();
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0) return result;
        foreach (var line in PinShareText.SplitOn('\n', text[..lastNewline]))
        {
            var fields = PinShareText.SplitOn('\t', line.TrimEnd('\r'));
            if (ParseSeq(fields[0]) is { } seq && fields.Count > 1)
                result.Add(new OutboxLine(seq, fields));
        }
        return result;
    }

    /// <summary>
    /// Lisp <c>parse-integer</c> as the outbox uses it: surrounding
    /// whitespace allowed, an optional sign, decimal digits. Deviation: a seq
    /// past the 64-bit range is refused (Lisp reads a bignum); the addon's
    /// millisecond clock is nowhere near.
    /// </summary>
    internal static long? ParseSeq(string field)
    {
        var text = field.Trim(' ', '\t', '\n', '\r', '\f');
        var i = 0;
        var negative = false;
        if (i < text.Length && text[i] is '+' or '-')
        {
            negative = text[i] == '-';
            i++;
        }
        if (i >= text.Length) return null;
        var digits = new StringBuilder();
        for (; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i])) return null;
            digits.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(text[i])));
        }
        return long.TryParse((negative ? "-" : "") + digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seq)
            ? seq
            : null;
    }
}
