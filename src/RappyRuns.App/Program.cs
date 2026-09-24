using RappyRuns.App.Host;
using RappyRuns.Core;
using RappyRuns.Core.Api;
using RappyRuns.Core.Config;
using RappyRuns.Core.Media;
using RappyRuns.Core.Update;
using RappyRuns.Host;
using RappyRuns.Win.Shell;
using RappyRuns.Win.Update;

namespace RappyRuns.App;

/// <summary>
/// Startup (spec core §2.1 / ui-shell §4.1, adapted): single instance →
/// WebView2 runtime check → config → autostart repair → the pre-window update
/// pass → queue and quest definitions (ClientHost) → the main window. The rest
/// happens when the window is shown (<see cref="ClientHost.Start"/>) and when
/// the page says hello (<see cref="ClientHost"/>: the startup marker).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = HostOptions.FromEnvironment(args);
        // An isolated developer run never writes into the installed client's log.
        if (options.RecordingLogPath is { } logPath)
        {
            Directory.CreateDirectory(options.ConfigDir);
            RecordingLog.PathOverride = logPath;
        }

        // Before any window or thread: a second launch raises the running
        // client (Lisp or C#: same mutex and tray class) and exits.
        if (!options.MultiInstance && SingleInstance.ClaimForProcess())
        {
            SingleInstance.SignalExisting();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        InstallCrashLogging();

        // A failed start is not rolled back by the old Lisp updater (spec core
        // §10.7 #8), so a missing WebView2 Runtime must end in a readable
        // message, never a crash.
        if (!WebViewRuntime.EnsureAvailable())
            return 1;

        var config = ConfigStore.Open(options.ConfigDir, args);
        if (!options.Isolated && !options.MultiInstance)
        {
            var repair = new Autostart(valueName: options.AutostartValueName).ReconcileAtStartup();
            if (repair is AutostartRepair.Rewritten or AutostartRepair.Failed) RecordingLog.Write($"autostart: {repair}");
        }

        var transport = new HttpTransport();
        var updater = new SelfUpdater(transport, () => config.ResolveUpdateRepo(), ClientVersion.Current, UpdateLauncher.InstallDir);
        var note = StartupUpdateNote.None;
        // A developer copy never runs the startup pass: it would write the
        // installed client's startup marker and swap the exe it runs from
        // (the same guard as the marker in ClientHost.Hello).
        if (config.AutoUpdate && ClientVersion.Current is not null && !options.Isolated && !options.MultiInstance)
            note = StartupUpdate.Run(updater, config.Directory, config.Language);

        var host = new ClientHost(config, options, transport, updater, note);
        Application.Run(new MainForm(host, config.StartupMinimized));
        // Only reached when the window closed without the quit path (it
        // normally ends the process with ExitProcess).
        host.Shell.Quit();
        return 0;
    }

    /// <summary>
    /// ui-shell §4.4: no crash handler may take the client down silently. The
    /// poll loop and the workers catch their own errors; anything that still
    /// escapes lands in the recording log.
    /// </summary>
    private static void InstallCrashLogging()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => RecordingLog.Write("unhandled UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RecordingLog.Write($"unhandled exception (terminating {e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RecordingLog.Write("unobserved task exception: " + e.Exception);
            e.SetObserved();
        };
    }
}
