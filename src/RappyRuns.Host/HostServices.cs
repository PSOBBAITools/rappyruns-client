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

    /// <summary>The Pin Share relay's WebSocket connector; null = the real one.</summary>
    Func<string, CancellationToken, Task<RelaySocket>>? PinShareConnect { get; }

    /// <summary>The Pin Share addon installer; null = the real one (the bundled data folder).</summary>
    AddonInstaller? PinShareInstaller { get; }
}

/// <summary>The production <see cref="IHostServices"/>: the real registry, game, capture, tray and log.</summary>
public class HostServices : IHostServices
{
    private MachineInfo? _machine;

    public virtual MachineInfo Machine => _machine ??= MachineProbe.Current();

    public virtual void Log(string line) => RecordingLog.Write(line);

    public virtual IAutostartSetting Autostart(string valueName) => new RegistryAutostart(new Autostart(valueName: valueName));

    public virtual IGameConnector Connector() => new PsobbGameConnector(new PsobbConnector(Log));

    public virtual ICaptureBackend CaptureBackend(Func<bool> wgcDisabled, GdigrabProbe gdigrab) =>
        new Win32FfmpegBackend(PsobbConnector.FindPsobbWindow, wgcDisabled, gdigrab);

    public virtual bool ShowTrayIcon => true;

    public virtual Func<string, CancellationToken, Task<RelaySocket>>? PinShareConnect => null;

    public virtual AddonInstaller? PinShareInstaller => null;

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
