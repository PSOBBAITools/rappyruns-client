using System.Security.Cryptography;
using RappyRuns.Core.PinShare;

namespace RappyRuns.Win.PinShare;

/// <summary>The Pin Share settings the relay reads every tick (config <c>:pinshare-enabled</c>, <c>:pinshare-channel</c>, <c>:pinshare-server</c>).</summary>
public sealed record PinShareConfig(bool Enabled, string? Channel, string? Server);

/// <summary>
/// The session the relay should be serving right now: game exe, cleaned
/// passphrase ("" = local-only pin-set drawing) and server URL. A session
/// runs while this stays equal (Lisp <c>pinshare-session-current-p</c>).
/// </summary>
public sealed record PinShareWanted(string GameExe, string Channel, string ServerUrl);

/// <summary>
/// The resident Pin Share relay supervisor (Lisp <c>pinshare-loop</c> /
/// <c>pinshare-run-session</c>, pinshare-win32.lisp:454-498). One dedicated
/// background thread: idle (re-checking every second) until Pin Share is on,
/// the account is in the rollout, there is a passphrase or a pin set, and a
/// verified game is attached; then it installs / updates the addon next to
/// the game and runs a relay session until any of that changes.
/// Thread-safety: <see cref="GameExe"/> is written by the poll loop,
/// <see cref="Status"/> and <see cref="ChannelItems"/> by the relay thread;
/// all are single-reference swaps read by the UI.
/// </summary>
public sealed class PinShareSupervisor : IDisposable
{
    private readonly Func<PinShareConfig> _config;
    private readonly PinSharePermission _permission;
    private readonly PinSetTracker? _pinSets;
    private readonly AddonInstaller _installer;
    private readonly Action<string>? _log;
    private readonly Func<string, CancellationToken, Task<RelaySocket>> _connect;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _sessions = [];
    private readonly object _statusLock = new();
    private Thread? _thread;
    private int _disposed;
    private volatile string? _gameExe;
    private volatile ChannelItems? _channelItems;
    private PinShareStatus _status = PinShareStatus.Off;

    /// <param name="config">Reads the current Pin Share settings (called every tick; keep it cheap).</param>
    /// <param name="permission">The rollout verdict (<c>/api/me</c> features).</param>
    /// <param name="pinSets">The pin set chosen on the site for the loaded quest; null when pin sets are not wired.</param>
    /// <param name="installer">Addon installer; default looks for <c>data\pin-share</c> next to the exe.</param>
    /// <param name="log">The client log.</param>
    /// <param name="connect">Opens the relay socket (tests inject; default <see cref="RelaySocket.ConnectAsync"/>).</param>
    public PinShareSupervisor(
        Func<PinShareConfig> config,
        PinSharePermission permission,
        PinSetTracker? pinSets = null,
        AddonInstaller? installer = null,
        Action<string>? log = null,
        Func<string, CancellationToken, Task<RelaySocket>>? connect = null)
    {
        _config = config;
        _permission = permission;
        _pinSets = pinSets;
        _log = log;
        _installer = installer ?? new AddonInstaller(log: log);
        _connect = connect ?? ((url, ct) => RelaySocket.ConnectAsync(url, cancellationToken: ct));
    }

    /// <summary>
    /// Full path of the attached, signature-verified PsoBB.exe, or null while
    /// no game is attached. The poll loop sets it on attach and clears it on
    /// detach: the addon's exchange folder is found next to it, and the relay
    /// only runs while a game is there to draw pins.
    /// </summary>
    public string? GameExe
    {
        get => _gameExe;
        set => _gameExe = value;
    }

    /// <summary>What the relay is doing, for the Settings status line (<see cref="PinShareStatus.Describe"/>).</summary>
    public PinShareStatus Status
    {
        get { lock (_statusLock) return _status; }
        internal set
        {
            lock (_statusLock)
            {
                if (_status == value) return;
                _status = value;
            }
            StatusChanged?.Invoke(value);
        }
    }

    /// <summary>Raised on the relay thread when <see cref="Status"/> changes.</summary>
    public event Action<PinShareStatus>? StatusChanged;

    /// <summary>The relay server's latest pins and arrows while connected (what Save files), else null.</summary>
    public ChannelItems? ChannelItems
    {
        get => _channelItems;
        internal set => _channelItems = value;
    }

    internal PinSet? CurrentPinSet => _pinSets?.Current;

    internal void Log(string line) => _log?.Invoke(line);

    internal Task<RelaySocket> Connect(string url, CancellationToken cancellationToken) => _connect(url, cancellationToken);

    /// <summary>Starts the supervisor thread (idle until Pin Share is on). Call once.</summary>
    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("already started");
        _thread = new Thread(Loop) { IsBackground = true, Name = "rappyruns-pinshare" };
        _thread.Start();
    }

    /// <summary>
    /// Stops the thread: the running session ends within a tick and leaves
    /// in.txt without items. Waits up to <paramref name="timeout"/>.
    /// </summary>
    public void Stop(TimeSpan? timeout = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _stop.Cancel();
        _thread?.Join(timeout ?? TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        Stop();
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _thread is not { IsAlive: true }) _stop.Dispose();
    }

    /// <summary>
    /// (wanted, status): the session to serve right now, or null with the
    /// status saying why the relay is idle (Lisp <c>pinshare-wanted</c>,
    /// pinshare-win32.lisp:291), first match wins.
    /// </summary>
    public (PinShareWanted? Wanted, PinShareStatus Status) Wanted()
    {
        var config = _config();
        var channel = PinShareSettings.Channel(config.Channel);
        var exe = GameExe;
        if (!config.Enabled) return (null, new PinShareStatus(PinShareStatusKind.Off));
        // Staged rollout: the server decides who may use the relay.
        if (!_permission.Allowed) return (null, new PinShareStatus(PinShareStatusKind.NotAllowed));
        // No passphrase: nothing to share, but a pin set chosen on the site
        // is still drawn - a session with channel "" writes in.txt and never
        // connects.
        if (channel == "" && CurrentPinSet is null) return (null, new PinShareStatus(PinShareStatusKind.NoChannel));
        if (exe is null) return (null, new PinShareStatus(PinShareStatusKind.WaitingGame));
        return (new PinShareWanted(exe, channel, PinShareSettings.ServerUrl(config.Server)), PinShareStatus.Off);
    }

    internal bool SessionCurrent(PinShareWanted wanted) =>
        !_stop.IsCancellationRequested && wanted == Wanted().Wanted;

    private bool Wait(double seconds) => _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var (wanted, status) = Wanted();
            if (wanted is null)
            {
                Status = status;
                Wait(1);
                continue;
            }
            try
            {
                RunSession(wanted);
            }
            catch (Exception e)
            {
                Log($"pin share relay failed: {e}");
                Status = new PinShareStatus(PinShareStatusKind.Error, e.Message);
                Wait(5);
            }
        }
    }

    private void RunSession(PinShareWanted wanted)
    {
        var addonDir = PinShareSettings.AddonDir(wanted.GameExe);
        if (_installer.EnsureAddon(addonDir) is { } problem)
        {
            Status = problem;
            Wait(5);
            return;
        }
        var outPath = Path.Combine(addonDir, "exchange", "out.txt");
        var inPath = Path.Combine(addonDir, "exchange", "in.txt");
        var tmpPath = Path.Combine(addonDir, "exchange", "in.txt.tmp");
        var relay = new PinShareRelay
        {
            Channel = wanted.Channel,
            // Fresh per session: the addon only re-sends its name to a NEW session.
            Session = RandomNumberGenerator.GetHexString(8, lowercase: true),
        };
        // The old PowerShell relay still running would fight us over both
        // files; stand aside until its heartbeat goes stale.
        while (SessionCurrent(wanted)
               && Inbox.IsForeignRelay(ExchangeFiles.ReadText(inPath), _sessions, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            Status = new PinShareStatus(PinShareStatusKind.Conflict);
            Wait(2);
        }
        if (!SessionCurrent(wanted)) return;
        _sessions.Add(relay.Session);
        relay.SkipBacklog(Outbox.Parse(ExchangeFiles.ReadText(outPath)));
        new RelaySession(this, relay, wanted, outPath, inPath, tmpPath).Run();
    }
}
