using System.Text;

namespace RappyRuns.Core.Media;

/// <summary>
/// Windows command-line assembly per the CommandLineToArgvW rules
/// (ffmpeg-win32.lisp:98-124). The spawn uses it to build CreateProcessW's
/// single string, and the recording log prints it as "capture argv:" - the
/// first thing a remote diagnosis reads, so it must match the Lisp output.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// <c>quote-windows-arg</c>: unchanged unless empty or containing a space,
    /// tab or double quote; otherwise quoted, with backslashes before a quote
    /// doubled plus one and trailing backslashes doubled.
    /// </summary>
    public static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    break;
                case '"':
                    sb.Append('\\', 2 * backslashes + 1);
                    backslashes = 0;
                    sb.Append('"');
                    break;
                default:
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('\\', 2 * backslashes);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary><c>argv->command-line</c>: program and args quoted, space-separated.</summary>
    public static string Build(string program, IEnumerable<string> args) =>
        string.Join(' ', new[] { program }.Concat(args).Select(QuoteArg));
}
