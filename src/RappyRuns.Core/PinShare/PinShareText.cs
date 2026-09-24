using System.Globalization;
using System.Numerics;
using System.Text;

namespace RappyRuns.Core.PinShare;

/// <summary>
/// Text primitives of the Pin Share exchange files (client/src/pinshare.lisp).
/// Both files are tab/newline framed and read by the Lua addon, so every
/// string that goes in is stripped of control characters and every number
/// is printed in a form Lua's <c>tonumber</c> reads back.
/// </summary>
public static class PinShareText
{
    /// <summary>
    /// <paramref name="value"/> without control characters (code &lt; 32 and 127):
    /// tabs and newlines would break the line/tab framing of both exchange
    /// files. A non-string is "" (Lisp <c>pinshare-clean</c>, pinshare.lisp:48).
    /// </summary>
    public static string Clean(string? value)
    {
        if (value is null) return "";
        var needs = false;
        foreach (var c in value)
        {
            if (c < 32 || c == 127) { needs = true; break; }
        }
        if (!needs) return value;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c >= 32 && c != 127) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits on every <paramref name="separator"/>, keeping empty fields
    /// (Lisp <c>split-on</c>, pinshare.lisp:60): "a\t\tb" is three fields.
    /// </summary>
    public static List<string> SplitOn(char separator, string text) => [.. text.Split(separator)];

    /// <summary>Fields joined by tabs: one in.txt line without its newline (pinshare.lisp:352).</summary>
    public static string Join(IEnumerable<string> fields) => string.Join('\t', fields);

    /// <summary>
    /// The first <paramref name="count"/> characters of <paramref name="text"/>,
    /// counting code points as Lisp characters do (a surrogate pair is one
    /// character there), so a cut never splits an emoji.
    /// </summary>
    public static string TakeChars(string text, int count)
    {
        var index = 0;
        for (var n = 0; n < count && index < text.Length; n++)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
        }
        return text[..index];
    }

    // --- numbers --------------------------------------------------------------

    /// <summary>
    /// Decimal <paramref name="text"/> (optional sign, fraction and exponent)
    /// as a double, or null for anything else (the addon prints "nan" / "inf"
    /// for a broken coordinate). Mirrors <c>parse-pinshare-number</c>
    /// (pinshare.lisp:80): at most 30 digits on either side of the point and
    /// an exponent within ±30. Digits are Unicode decimal digits, as Lisp's
    /// <c>digit-char-p</c> accepts; the result is correctly rounded.
    /// A zero is always +0.0 because Lisp computes through an exact
    /// rational, which has no negative zero.
    /// </summary>
    public static double? ParseNumber(string text)
    {
        var position = 0;
        var end = text.Length;
        char? Peek() => position < end ? text[position] : null;

        string Digits()
        {
            var sb = new StringBuilder();
            while (position < end && char.IsDigit(text[position]))
            {
                sb.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(text[position])));
                position++;
            }
            return sb.ToString();
        }

        int Sign()
        {
            switch (Peek())
            {
                case '-': position++; return -1;
                case '+': position++; return 1;
                default: return 1;
            }
        }

        var mantissaSign = Sign();
        var whole = Digits();
        var fraction = "";
        if (Peek() == '.')
        {
            position++;
            fraction = Digits();
        }
        BigInteger exponent = 0;
        if (Peek() is 'e' or 'E')
        {
            position++;
            var exponentSign = Sign();
            var exponentDigits = Digits();
            if (exponentDigits.Length == 0) return null;
            exponent = exponentSign * BigInteger.Parse(exponentDigits, CultureInfo.InvariantCulture);
        }
        if (position != end
            || (whole.Length == 0 && fraction.Length == 0)
            || whole.Length > 30 || fraction.Length > 30
            || BigInteger.Abs(exponent) > 30)
        {
            return null;
        }
        var literal = string.Concat(
            mantissaSign < 0 ? "-" : "",
            whole.Length == 0 ? "0" : whole,
            fraction.Length == 0 ? "" : "." + fraction,
            "e", exponent.ToString(CultureInfo.InvariantCulture));
        var value = double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
        return value == 0 ? 0.0 : value;
    }

    /// <summary>
    /// An integer-valued decimal as an integer, else null (Lisp
    /// <c>parse-pinshare-integer</c>, pinshare.lisp:125: "60.000" is 60,
    /// "60.5" is not). Deviation: Lisp answers a bignum beyond the 64-bit
    /// range; this answers null (no game id, floor or ttl gets near).
    /// </summary>
    public static long? ParseInteger(string text)
    {
        var number = ParseNumber(text);
        if (number is not { } value || Math.Floor(value) != value) return null;
        if (value < -9.2e18 || value > 9.2e18) return null;
        return (long)value;
    }

    /// <summary>An integer as in.txt prints it (<c>~d</c>).</summary>
    public static string FormatNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A double as text Lua's <c>tonumber</c> reads back, byte-identical to
    /// the Lisp client's <c>pinshare-format-number</c> (pinshare.lisp:335):
    /// PRIN1 of a double-float with the "d" marker turned into "e". That is
    /// the shortest round-trip digits, fixed notation with at least one
    /// fractional digit for 1e-3 &lt;= |x| &lt; 1e7 ("1.5", "60.0"), else
    /// scientific "1.0e7" / "9.999e-4" (no "+", no exponent padding).
    /// </summary>
    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(nameof(value), "not a finite number");

        var negative = value < 0 || (value == 0 && double.IsNegative(value));
        var magnitude = Math.Abs(value);
        if (magnitude == 0) return negative ? "-0.0" : "0.0";

        // Shortest round-trip digits and the decimal exponent, from "R"
        // (.NET Core 3.0+ prints the shortest representation that round-trips).
        var shortest = magnitude.ToString("R", CultureInfo.InvariantCulture);
        string digits;
        int pointPos; // magnitude = 0.<digits> * 10^pointPos
        var ePos = shortest.IndexOfAny(['E', 'e']);
        var mantissa = ePos >= 0 ? shortest[..ePos] : shortest;
        var exp10 = ePos >= 0 ? int.Parse(shortest[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.');
        var intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        var fracPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
        digits = intPart + fracPart;
        pointPos = intPart.Length + exp10;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; pointPos--; }
        digits = digits[lead..].TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (magnitude >= 1e-3 && magnitude < 1e7)
        {
            if (pointPos <= 0)
            {
                sb.Append("0.").Append('0', -pointPos).Append(digits);
            }
            else if (pointPos >= digits.Length)
            {
                sb.Append(digits).Append('0', pointPos - digits.Length).Append(".0");
            }
            else
            {
                sb.Append(digits, 0, pointPos).Append('.').Append(digits, pointPos, digits.Length - pointPos);
            }
        }
        else
        {
            sb.Append(digits[0]).Append('.');
            sb.Append(digits.Length > 1 ? digits[1..] : "0");
            sb.Append('e').Append((pointPos - 1).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
