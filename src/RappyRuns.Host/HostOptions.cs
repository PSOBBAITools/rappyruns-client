using RappyRuns.Core.Config;
using RappyRuns.Win.Shell;

namespace RappyRuns.Host;

/// <summary>
/// Where the client keeps its files and how it coexists with other copies.
/// Production uses the Lisp client's folders and names (they are contracts);
/// two developer switches change that for side-by-side checks:
/// <list type="bullet">
/// <item><c>RAPPYRUNS_CONFIG_DIR=&lt;folder&gt;</c> (<see cref="Isolated"/>): config.sexp,
/// queue.sexp, trigger-log.txt, the recording log and the WebView2 profile live
/// there; the startup marker and the %TEMP% update sweep are skipped and the
/// autostart checkbox uses its own Run value, so the installed client's files
/// are never touched.</item>
/// <item><c>--no-single-instance</c> or <c>RAPPYRUNS_DEV_MULTI=1</c> (<see cref="MultiInstance"/>):
/// no single-instance mutex and a tray window class of its own, so this copy
/// runs next to the installed (Lisp or C#) client. Recording and the Pin Share
/// relay are forced off (fixed pipe names; the addon's exchange files next to
/// the game), and the startup marker is not written. Combine
/// it with RAPPYRUNS_CONFIG_DIR, or both clients write the same queue.sexp.</item>
/// </list>
/// </summary>
public sealed class HostOptions
{
    public const string ConfigDirVariable = "RAPPYRUNS_CONFIG_DIR";
    public const string MultiVariable = "RAPPYRUNS_DEV_MULTI";
    public const string NoSingleInstanceFlag = "--no-single-instance";

    /// <summary>The folder with config.sexp and queue.sexp.</summary>
    public required string ConfigDir { get; init; }

    /// <summary>The config folder was redirected (developer run).</summary>
    public bool Isolated { get; init; }

    /// <summary>Skip the single-instance guard (developer run).</summary>
    public bool MultiInstance { get; init; }

    /// <summary>Launched with --debug (developer settings; DevTools).</summary>
    public bool Debug { get; init; }

    /// <summary>The folder of the running exe (login.txt, data\, ffmpeg\).</summary>
    public string ExeDir { get; init; } = Credentials.DefaultDirectory();

    /// <summary>The tray window class: the contract name, or a private one next to another instance.</summary>
    public string TrayClassName => MultiInstance ? SingleInstance.TrayClassName + "-dev" : SingleInstance.TrayClassName;

    /// <summary>The HKCU Run value the autostart checkbox edits.</summary>
    public string AutostartValueName => Isolated || MultiInstance ? Autostart.ValueName + "-dev" : Autostart.ValueName;

    /// <summary>The WebView2 profile (next to the config, never next to the exe).</summary>
    public string WebViewDataDir => Isolated
        ? Path.Combine(ConfigDir, "WebView2")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ConfigStore.FolderName, "WebView2");

    /// <summary>The recording/diagnostics log override for an isolated run, else null (the %TEMP% contract path).</summary>
    public string? RecordingLogPath => Isolated ? Path.Combine(ConfigDir, "ephinea-ta-recording.log") : null;

    /// <summary>trigger-log.txt, next to the config (spec core §16.1).</summary>
    public string TriggerLogPath => Path.Combine(ConfigDir, "trigger-log.txt");

    /// <summary>Reads the developer switches from the environment and the command line.</summary>
    public static HostOptions FromEnvironment(IReadOnlyList<string> args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        var dir = Environment.GetEnvironmentVariable(ConfigDirVariable);
        var isolated = !string.IsNullOrWhiteSpace(dir);
        return new HostOptions
        {
            ConfigDir = isolated ? Path.GetFullPath(dir!) : ConfigStore.DefaultDirectory(),
            Isolated = isolated,
            MultiInstance = Has(NoSingleInstanceFlag) || Environment.GetEnvironmentVariable(MultiVariable) == "1",
            Debug = Has("--debug"),
        };
    }
}
