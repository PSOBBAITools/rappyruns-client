using System.Globalization;
using System.Text;

namespace RappyRuns.Core.Sexp;

/// <summary>
/// Writes data the way the Lisp client's write-sexp-file does (prin1 with
/// *package* = KEYWORD, spec core §4.1), so a Lisp client reading the file
/// back after a downgrade gets the same values (§4.2): keywords as :NAME,
/// T/NIL as COMMON-LISP:T / COMMON-LISP:NIL, floats always with a decimal
/// point, everything on one line.
/// </summary>
public static class SexpWriter
{
    public static string Write(SexpNode node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Writes one datum to <paramref name="path"/> as UTF-8 without BOM,
    /// atomically (temp file + replace; <see cref="DurableFile"/>) so a crash
    /// never leaves half a file. With <paramref name="durable"/> (the default)
    /// the temp file is flushed to disk first, so a power cut cannot either;
    /// a writer on the tracking thread passes false so it never waits on the disk.
    /// </summary>
    public static void WriteFile(string path, SexpNode node, bool durable = true)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");
        var bytes = new UTF8Encoding(false).GetBytes(Write(node));
        DurableFile.Replace(path, temp, stream => stream.Write(bytes), durable);
    }

    private static void Write(SexpNode node, StringBuilder sb)
    {
        switch (node)
        {
            case SKeyword k:
                sb.Append(':').Append(SymbolName(k.Name));
                break;
            case SSymbol s when s.IsNilSymbol:
                sb.Append("COMMON-LISP:NIL");
                break;
            case SSymbol s when s.IsTSymbol:
                sb.Append("COMMON-LISP:T");
                break;
            case SSymbol s:
                if (s.Package is not null) sb.Append(SymbolName(s.Package)).Append("::");
                sb.Append(SymbolName(s.Name));
                break;
            case SString str:
                sb.Append('"');
                foreach (var c in str.Value)
                {
                    if (c is '"' or '\\') sb.Append('\\');
                    sb.Append(c);
                }
                sb.Append('"');
                break;
            case SInteger i:
                sb.Append(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case SFloat f:
                sb.Append(FormatFloat(f));
                break;
            case SList l when l.IsNil:
                sb.Append("COMMON-LISP:NIL");
                break;
            case SList l:
                sb.Append('(');
                for (var i = 0; i < l.Items.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    Write(l.Items[i], sb);
                }
                if (l.Tail is not null)
                {
                    sb.Append(" . ");
                    Write(l.Tail, sb);
                }
                sb.Append(')');
                break;
            case SVector v:
                // Never produced by the client; written as a list, which the
                // Lisp side reads equally well where vectors were accepted.
                Write(new SList(v.Items), sb);
                break;
            default:
                throw new ArgumentException($"cannot write {node.GetType().Name}");
        }
    }

    // A name that reads back as itself: plain when it is all upper-case
    // symbol constituents, otherwise |escaped|.
    private static string SymbolName(string name)
    {
        var plain = name.Length > 0 && name.All(c =>
            !char.IsWhiteSpace(c) && c is not ('(' or ')' or '"' or ';' or '\'' or '|' or '\\' or ':' or '#' or '`' or ',')
            && !char.IsLower(c));
        if (plain && !LooksNumeric(name)) return name;
        return "|" + name.Replace("\\", "\\\\").Replace("|", "\\|") + "|";
    }

    private static bool LooksNumeric(string name) =>
        name.Length > 0 && (char.IsAsciiDigit(name[0]) || (name.Length > 1 && name[0] is '+' or '-' or '.' && char.IsAsciiDigit(name[1])));

    /// <summary>
    /// Lisp prin1 of a float: shortest round-trip digits, always a decimal
    /// point, exponent form outside [1e-3, 1e7), d0 marker for doubles.
    /// </summary>
    public static string FormatFloat(SFloat f)
    {
        var value = f.Value;
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException("NaN/Infinity cannot be written as Lisp data");

        var digits = f.IsDouble
            ? value.ToString("R", CultureInfo.InvariantCulture)
            : ((float)value).ToString("R", CultureInfo.InvariantCulture);
        var abs = Math.Abs(value);
        var exponentForm = abs != 0 && (abs < 1e-3 || abs >= 1e7);

        string mantissa;
        int exponent;
        var e = digits.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            mantissa = digits[..e];
            exponent = int.Parse(digits[(e + 1)..], CultureInfo.InvariantCulture);
        }
        else
        {
            mantissa = digits;
            exponent = 0;
        }

        if (!exponentForm)
        {
            // Plain positional notation.
            var plain = exponent == 0 ? mantissa : ShiftDecimal(mantissa, exponent);
            if (!plain.Contains('.')) plain += ".0";
            return f.IsDouble ? plain + "d0" : plain;
        }

        // Normalise to one digit before the point.
        var negative = mantissa.StartsWith('-');
        var m = negative ? mantissa[1..] : mantissa;
        var point = m.IndexOf('.');
        var intPart = point < 0 ? m : m[..point];
        var fracPart = point < 0 ? "" : m[(point + 1)..];
        var all = (intPart + fracPart).TrimStart('0');
        var leadingZeros = (intPart + fracPart).Length - all.Length;
        exponent += intPart.Length - 1 - leadingZeros;
        all = all.TrimEnd('0');
        if (all.Length == 0) all = "0";
        var normalized = all[..1] + "." + (all.Length > 1 ? all[1..] : "0");
        return (negative ? "-" : "") + normalized + (f.IsDouble ? "d" : "e") + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static string ShiftDecimal(string mantissa, int exponent)
    {
        var negative = mantissa.StartsWith('-');
        var m = negative ? mantissa[1..] : mantissa;
        var point = m.IndexOf('.');
        var digits = point < 0 ? m : m[..point] + m[(point + 1)..];
        var pos = (point < 0 ? m.Length : point) + exponent;
        string result;
        if (pos <= 0) result = "0." + new string('0', -pos) + digits;
        else if (pos >= digits.Length) result = digits + new string('0', pos - digits.Length) + ".0";
        else result = digits[..pos] + "." + digits[pos..];
        return (negative ? "-" : "") + result;
    }
}
