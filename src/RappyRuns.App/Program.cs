using RappyRuns.App.Host;

namespace RappyRuns.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = CommandLine.Parse(args);
        ApplicationConfiguration.Initialize();

        // A failed start is not rolled back by the Lisp updater (spec core
        // §10.7 #8), so a missing WebView2 Runtime must end in a readable
        // message, never a crash.
        if (!WebViewRuntime.EnsureAvailable())
            return 1;

        Application.Run(new MainForm(options));
        return 0;
    }
}
