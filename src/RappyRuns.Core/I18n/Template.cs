using System.Globalization;
using System.Text;

namespace RappyRuns.Core.I18n;

/// <summary>
/// Formats the templates in strings.json. They are a mechanical conversion of
/// the Lisp client's FORMAT control strings (desktop/tools/export-i18n.lisp):
/// <list type="bullet">
/// <item><c>{n}</c> argument n</item>
/// <item><c>{n?text}</c> text only when argument n is non-null (FORMAT <c>~@[text~]</c>)</item>
/// <item><c>{n#one|many}</c> "one" when argument n is 1, else "many" (<c>~:p</c>, <c>~:@p</c>)</item>
/// </list>
/// The UI has the same formatter in TypeScript (ui/src/lib/i18n.ts); both are
/// checked against one golden file of Lisp FORMAT output.
/// </summary>
public static class Template
{
    public static string Format(string template, IReadOnlyList<object?> args)
    {
        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        Render(template, ref i, args, sb, stopAtClose: false);
        return sb.ToString();
    }

    // Renders until the end of the template, or until the '}' closing a
    // conditional body when stopAtClose (i is then left after it). A null
    // sb skips the text but still walks it, to find the closing brace.
    private static void Render(string t, ref int i, IReadOnlyList<object?> args, StringBuilder? sb, bool stopAtClose)
    {
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '}' && stopAtClose)
            {
                i++;
                return;
            }
            if (c != '{')
            {
                sb?.Append(c);
                i++;
                continue;
            }

            i++;
            var start = i;
            while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
            if (i == start || i >= t.Length) throw new FormatException($"Bad placeholder in \"{t}\"");
            var n = int.Parse(t.AsSpan(start, i - start), CultureInfo.InvariantCulture);
            if (n >= args.Count) throw new FormatException($"Missing argument {n} for \"{t}\"");
            var arg = args[n];

            switch (t[i])
            {
                case '}':
                    i++;
                    sb?.Append(Display(arg));
                    break;
                case '?':
                    i++;
                    Render(t, ref i, args, arg is null ? null : sb, stopAtClose: true);
                    break;
                case '#':
                    i++;
                    var close = t.IndexOf('}', i);
                    if (close < 0) throw new FormatException($"Unclosed plural in \"{t}\"");
                    var forms = t[i..close].Split('|');
                    if (forms.Length != 2) throw new FormatException($"Plural needs two forms in \"{t}\"");
                    sb?.Append(IsOne(arg) ? forms[0] : forms[1]);
                    i = close + 1;
                    break;
                default:
                    throw new FormatException($"Bad placeholder in \"{t}\"");
            }
        }
        if (stopAtClose) throw new FormatException($"Unclosed conditional in \"{t}\"");
    }

    private static string Display(object? arg) => arg switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => arg.ToString() ?? "",
    };

    private static bool IsOne(object? arg) => arg switch
    {
        int v => v == 1,
        long v => v == 1,
        double v => v == 1,
        decimal v => v == 1,
        _ => false,
    };
}
