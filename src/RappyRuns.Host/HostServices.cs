using RappyRuns.Core.Game;
using RappyRuns.Core.Media;
using RappyRuns.Win.Game;
using RappyRuns.Win.Media;
using RappyRuns.Win.PinShare;
using RappyRuns.Win.Shell;

namespace RappyRuns.Host;

/// <summary>The autostart checkbox's HKCU Run value (<see cref="Autostart"/> behind a seam).</summary>
public interface IAutostartSetting
{
    /// <summary>True when the Run value exists and points at this exe.</summary>
    bool IsEnabled();

    /// <summary>Writes or deletes the value; never throws.</summary>
    bool SetEnabled(bool enable);
}

/// <summary>
/// What <see cref="ClientHost"/> takes from the machine rather than builds
/// from config: the registry, the game process, screen capture, the tray,
/// the Pin Share relay socket, the log (S32). Production passes nothing and
/// gets <see cref="HostServices"/>; tests pass fakes so a whole host can be
/// built and driven without touching the registry, a game or a tray.
/// </summary>
public interface IHostServices
{
    /// <summary>RAM / CPU / OS facts (the low-memory recording guard, diagnostics).</summary>
    MachineInfo Machine { get; }

    /// <summary>The client log line writer.</summary>
    void Log(string line);

    /// <summary>The autostart setting under <paramref name="valueName"/>.</summary>
    IAutostartSetting Autostart(string valueName);

    /// <summary>Attaches to PSOBB (the poll loop's seam).</summary>
    IGameConnector Connector();

    /// <summary>The recorder's capture backend.</summary>
    ICaptureBackend CaptureBackend(Func<bool> wgcDisabled, GdigrabProbe gdigrab);

    /// <summary>False keeps the tray to its hidden window (no icon in the user's tray).</summary>
    bool ShowTrayIcon { get; }

    /// <summary>Ends the process once the quit sequence ran (ExitProcess in production).</summary>
    void Exit(int code);

    /// <summary>Opens the Pin Share relay's WebSocket.</summary>
    Task<RelaySocket> PinShareConnect(string url, CancellationToken cancellationToken);

    /// <summary>The Pin Share addon installer (the bundled data folder in production).</summary>
    AddonInstaller PinShareInstaller();
}

/// <summary>The production <see cref="IHostServices"/>: the real registry, game, capture, tray and log.</summary>
public sealed class HostServices : IHostServices
{
    public MachineInfo Machine { get; } = MachineProbe.Current();

    public void Log(string line) => RecordingLog.Write(line);

    public IAutostartSetting Autostart(string valueName) => new RegistryAutostart(new Autostart(valueName: valueName));

    public IGameConnector Connector() => new PsobbGameConnector(new PsobbConnector(Log));

    public ICaptureBackend CaptureBackend(Func<bool> wgcDisabled, GdigrabProbe gdigrab) =>
        new Win32FfmpegBackend(PsobbConnector.FindPsobbWindow, wgcDisabled, gdigrab);

    public bool ShowTrayIcon => true;

    public void Exit(int code) => ShellHost.ExitProcess(code);

    public Task<RelaySocket> PinShareConnect(string url, CancellationToken cancellationToken) =>
        RelaySocket.ConnectAsync(url, cancellationToken: cancellationToken);

    public AddonInstaller PinShareInstaller() => new(log: Log);

    private sealed class RegistryAutostart(Autostart autostart) : IAutostartSetting
    {
        public bool IsEnabled() => autostart.IsEnabled();

        public bool SetEnabled(bool enable) => autostart.SetEnabled(enable);
    }
}

/// <summary><see cref="PsobbConnector"/> behind the poll loop's seam.</summary>
internal sealed class PsobbGameConnector(PsobbConnector connector) : IGameConnector
{
    public IProcessMemoryReader? TryAttach() => connector.TryAttach();

    public PsobbRejection? Rejection => connector.Rejection;
}
