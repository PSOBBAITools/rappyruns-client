namespace RappyRuns.App.Host;

/// <summary>
/// Command-line switches (spec core §2.3). Matching is case-insensitive and
/// exact; anything else is ignored, and the self-updater restarts the client
/// with no arguments at all.
/// </summary>
internal sealed record CommandLine(bool Debug, bool Minimized)
{
    public static CommandLine Parse(IEnumerable<string> args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        return new CommandLine(Debug: Has("--debug"), Minimized: Has("--minimized"));
    }
}
