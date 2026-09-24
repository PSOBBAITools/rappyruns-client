using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RappyRuns.Core;
using RappyRuns.Core.Api;
using RappyRuns.Core.Config;
using RappyRuns.Core.Game;
using RappyRuns.Core.Ghost;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Media;
using RappyRuns.Core.PinShare;
using RappyRuns.Core.Store;
using RappyRuns.Core.Update;
using RappyRuns.Win.Game;
using RappyRuns.Win.Media;
using RappyRuns.Win.Overlay;
using RappyRuns.Win.PinShare;
using RappyRuns.Win.Shell;
using RappyRuns.Win.Update;
using RoomRow = RappyRuns.Core.Game.RoomRow;

namespace RappyRuns.Host;

/// <summary>What the pre-window update pass left for the main window to say (report-startup-update).</summary>
public enum StartupUpdateNote
{
    /// <summary>No automatic pass ran, or the release check failed (silent, like the Lisp startup check).</summary>
    None,
    UpToDate,
    Rejected,
    NotWritable,
    DownloadFailed,
}

/// <summary><see cref="PsobbConnector"/> behind the poll loop's seam.</summary>
internal sealed class PsobbGameConnector(PsobbConnector connector) : IGameConnector
{
    public IProcessMemoryReader? TryAttach() => connector.TryAttach();

    public PsobbRejection? Rejection => connector.Rejection;
}

/// <summary>
/// The client: owns every service, wires them together and runs the startup
/// and quit sequences (spec core §2, ui-shell §4, adapted to WebView2 - see
/// <see cref="Start"/> and <see cref="Hello"/>). The IPC methods live in
/// Ipc/*.cs and reach the services through this class.
/// </summary>
public sealed class ClientHost : IDisposable
{
    internal const string WindowTitle = "Rappy Runs Client";

    private readonly CancellationTokenSource _shutdown = new();
    private readonly PollLoopServices _pollServices;
    private readonly StartupUpdateNote _startupNote;
    private readonly MachineInfo _machine;
    private readonly HwEncoderStatus _hw = new();
    private readonly Autostart _autostart;
    private volatile bool _autostartEnabled;
    private string? _lastRunsJson;
    private string? _lastRoomsJson;
    private (int Kills, int Switches, int? Newest, bool Moderator)? _roomsSignature;
    private string? _lastTitle;
    private Func<Form?> _mainWindow = () => null;
    private Action<string> _setTitle = _ => { };
    private bool _helloDone; // under _runsLock
    private int _started;
    private int _applyingUpdate;
    private readonly TokenCheckGate _tokenChecks = new();

    public ClientHost(ConfigStore config, HostOptions options, HttpTransport transport, SelfUpdater updater,
        StartupUpdateNote startupNote = StartupUpdateNote.None)
    {
        Config = config;
        Options = options;
        Transport = transport;
        Updater = updater;
        _startupNote = startupNote;
        _machine = MachineProbe.Current();
        _autostart = new Autostart(valueName: options.AutostartValueName);
        _autostartEnabled = _autostart.IsEnabled();

        Api = new ApiClient(transport, new ConfigAuthSettings(config));
        Auth = new AuthService(Api, new ConfigAuthSettings(config), Submission.SafeMachineName());

        // Game.
        Clock = SystemGameClock.Instance;
        Catalog = new QuestCatalog();
        LoadBuiltinQuests(Catalog, QuestCatalog.ResolvePath(options.ExeDir), RecordingLog.Write);
        var detector = new Detector(Catalog, Clock);
        RunLogs = new RunLogs(Clock);
        TriggerLog = new TriggerLog(options.TriggerLogPath, Clock);
        Frames = new GameFrameProcessor(detector, RunLogs, Clock, new GameFrameOptions
        {
            TriggerLogEnabled = () => config.TriggerLog,
            CameraWanted = () => config.GhostOverlay && config.GhostMarker,
        }, TriggerLog, RecordingLog.Write);
        var connector = new PsobbConnector(RecordingLog.Write);

        // Queue.
        Queue = new RunQueue(config.QueuePath);
        Queue.Load();
        Queue.SaveFailed += (_, e) => RecordingLog.Write("queue.sexp save failed: " + e.Message);
        Network = new QueueNetwork(Api, RunPayload.RunJson,
            () => RecordingLog.DiagnosticsReport(ClientVersion.Display, _machine, _hw));

        // Recording.
        HwProbe = new HwEncoderProbe(_hw, FfmpegPath);
        Gdigrab = new GdigrabProbe(PsobbConnector.FindPsobbWindow, FfmpegPath, () => config.GetBool(ConfigKeys.WgcDisable));
        RecordingSettings RecordingSettings()
        {
            var settings = PollLoop.RecordingSettingsFor(config);
            // Fixed pipe names: a second instance must never record.
            return options.MultiInstance ? settings with { RecordEnabled = false } : settings;
        }
        Recorder = new Recorder(
            new Win32FfmpegBackend(PsobbConnector.FindPsobbWindow, () => config.GetBool(ConfigKeys.WgcDisable), Gdigrab),
            new RecorderEnvironment
            {
                Settings = RecordingSettings,
                RecordDir = RecordDir,
                FfmpegPath = FfmpegPath,
                Hw = _hw,
                StartHwEncoderProbe = HwProbe.Start,
                LowMemory = () => _machine.LowMemory,
                Notify = n => Notify(Msg.Of(n.TitleKey), Msg.Of(n.TextKey), n.Icon == NoticeIcon.Info ? NotifyKind.Info : NotifyKind.Warning),
            });
        // The kept file is tied to its queue entry so Upload can find it.
        Recorder.OnKeep = (path, run, untrimmed) => Queue.LinkVideoFile(run, path, untrimmed);

        // Ghost race and overlay.
        Ghost = new GhostSession();
        Overlay = new GameOverlay(() => Ghost.LiveCamera, log: RecordingLog.Write);

        // Pin Share.
        Permission = new PinSharePermission(config.PinshareAllowed, allowed =>
        {
            config.PinshareAllowed = allowed;
            SaveConfig();
        });
        PinSets = new PinSetTracker(
            q => Catalog.FindAll(q.QuestNumber, q.Episode, q.QuestName).Select(d => d.Slug).ToList(),
            () => config.PinshareEnabled && Permission.Allowed && !config.IsUnlinked,
            async (slug, extra, ct) =>
            {
                var r = await Api.FetchPinSetAsync(slug, extra, cancellationToken: ct).ConfigureAwait(false);
                return r.Found && r.Payload is not null ? JsonSerializer.SerializeToElement(r.Payload) : null;
            },
            RecordingLog.Write);
        PinShare = new PinShareSupervisor(
            // The addon's exchange files sit next to the game: one relay per game only.
            () => new PinShareConfig(config.PinshareEnabled && !options.MultiInstance, config.PinshareChannel, config.PinshareServer),
            Permission, PinSets, log: RecordingLog.Write);

        // UI state.
        Ui = new UiState(BuildSettings, config.Moderator, Permission.Allowed);
        Permission.Changed += allowed => Ui.SetPinshareAllowed(allowed);
        PinShare.StatusChanged += _ => RefreshPinShareLines();
        config.Changed += (_, _) => Ui.InvalidateSettings();
        Queue.Changed += (_, _) => PublishRuns();
        Queue.UploadProgressChanged += (_, _) => PublishRuns();
        Auth.TokenLinked += token =>
        {
            Ui.InvalidateSettings();
            _ = CheckTokenAsync();
        };

        // Shell (tray, balloons, close-to-tray, quit).
        Shell = new ShellHost(new ShellHostOptions
        {
            MainWindow = () => _mainWindow(),
            Language = () => config.Language,
            PrepareQuit = PrepareQuit,
            OpenUrl = OpenExternal,
            Log = RecordingLog.Write,
            TrayClassName = options.TrayClassName,
        });

        _pollServices = new PollLoopServices
        {
            Config = config,
            Clock = Clock,
            Connector = new PsobbGameConnector(connector),
            Frames = Frames,
            Catalog = Catalog,
            Recorder = Recorder,
            Queue = Queue,
            Registrar = Network,
            Submitter = Network,
            Uploader = Network,
            Ghost = Ghost,
            GhostFetch = async request =>
            {
                var r = await Api.FetchGhostSplitsAsync(request.Slugs[0], [.. request.Slugs.Skip(1)], request.Difficulty,
                    request.PartySize, request.Pb, cancellationToken: _shutdown.Token).ConfigureAwait(false);
                return r.Found ? r.Body : null;
            },
            PinSets = PinSets,
            TriggerLog = TriggerLog,
            // A developer copy's queue does not know the installed client's
            // recordings in the shared folder: never sweep or clean it.
            ManageRecordingsFolder = !(options.MultiInstance || options.Isolated),
            SetGameExe = exe => PinShare.GameExe = exe,
            RecordingSettings = RecordingSettings,
            MaybeStartGdigrabProbe = Gdigrab.MaybeStart,
            Deferred = Updater.Deferred,
            ApplyDeferredUpdate = ready => ApplyUpdateRestart(ready.ZipPath, ready.Tag),
            Toast = toast => Shell.Notify(toast.Title, toast.Text, NotifyKind.Info, toast.Url),
            Tick = OnTick,
            Log = RecordingLog.Write,
            MachineName = Submission.SafeMachineName(),
        };
        Poll = new PollLoop(_pollServices);
    }

    /// <summary>
    /// data\quest-triggers.sexp into the catalog, never fatal: anything
    /// (unreadable, malformed s-expressions, unexpected shapes) is logged and
    /// leaves the builtin set empty; the server's definitions still arrive with
    /// check-server. False when the load failed.
    /// </summary>
    internal static bool LoadBuiltinQuests(QuestCatalog catalog, string path, Action<string> log)
    {
        try
        {
            catalog.LoadBuiltin(path);
            return true;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            log($"quest-triggers.sexp could not be read: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // ---- services (internal: the IPC methods use them) ----

    public ConfigStore Config { get; }
    public HostOptions Options { get; }
    public UiState Ui { get; }
    public ShellHost Shell { get; }
    internal HttpTransport Transport { get; }
    internal ApiClient Api { get; }
    internal AuthService Auth { get; }
    internal SelfUpdater Updater { get; }
    internal IGameClock Clock { get; }
    internal QuestCatalog Catalog { get; }
    internal RunLogs RunLogs { get; }
    internal TriggerLog TriggerLog { get; }
    internal GameFrameProcessor Frames { get; }
    internal RunQueue Queue { get; }
    internal QueueNetwork Network { get; }
    internal HwEncoderProbe HwProbe { get; }
    internal GdigrabProbe Gdigrab { get; }
    internal Recorder Recorder { get; }
    internal GhostSession Ghost { get; }
    internal GameOverlay Overlay { get; }
    internal PinSharePermission Permission { get; }
    internal PinSetTracker PinSets { get; }
    internal PinShareSupervisor PinShare { get; }
    internal PollLoop Poll { get; private set; }
    internal CancellationToken ShutdownToken => _shutdown.Token;
    internal Language Language => Config.Language;

    /// <summary>The recordings folder (resolve-record-dir, with its one-time rename migration).</summary>
    internal string RecordDir() => RecordingFiles.ResolveRecordDir(Config.RecordDir);

    internal string FfmpegPath() => RecordingFiles.ResolveFfmpegPath(Config.FfmpegPath, Options.ExeDir);

    /// <summary>The main window and its title setter (the app's WinForms side).</summary>
    public void AttachWindow(Func<Form?> mainWindow, Action<string> setTitle)
    {
        _mainWindow = mainWindow;
        _setTitle = setTitle;
    }

    /// <summary>Connects the WebView2 IPC: events out, methods in.</summary>
    public void AttachIpc(IIpcRegistry registry, IUiSink sink)
    {
        Ui.Attach(sink);
        _sink = sink;
        new Ipc.AppMethods(this).Register(registry);
        new Ipc.SettingsMethods(this).Register(registry);
        new Ipc.AccountMethods(this).Register(registry);
        new Ipc.RunsMethods(this).Register(registry);
        new Ipc.RulesMethods(this).Register(registry);
        new Ipc.PinShareMethods(this).Register(registry);
        new Ipc.UpdateMethods(this).Register(registry);
    }

    private IUiSink? _sink;

    /// <summary>Host-initiated message box (ipc.md <c>notice</c>).</summary>
    internal void EmitNotice(Notice notice) => _sink?.Emit("notice", notice);

    /// <summary>
    /// Everything that does not need the UI, once the main window exists (core
    /// §2.1 steps 8-14): tray, Pin Share, session log line, HW encoder probe,
    /// the poll thread, then the server and token checks (their results reach
    /// the UI as state). Deviation: runs at window-shown rather than after the
    /// page said hello, so a WebView2 page that never loads cannot stop runs
    /// from being timed and queued; the hello only gates what needs the page
    /// (the startup marker and the startup-update report).
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        if (!Shell.Start()) RecordingLog.Write("tray: could not start (running without a tray icon)");
        PinShare.Start();
        _ = PermissionLoopAsync();
        RecordingLog.Write(RecordingLog.SessionLine(ClientVersion.Display, _machine, FfmpegPath()));
        if (Config.HwEncode) HwProbe.Start();
        if (Config.TriggerLog) Try("trigger log", () => TriggerLog.Start());
        Poll.Start();
        _ = CheckServerAsync();
        _ = CheckTokenAsync(onInvalid: ReloginWithFile);
        PromptForTokenSetup();
    }

    /// <summary>
    /// <c>app.hello</c>: the page runs and talks to us. The first time, write
    /// the bridge updater's startup marker and only then sweep the .old exe a
    /// rollback would have needed (PLAN.md decision 1), and queue the report of
    /// the pre-window update pass. The snapshot is replied (queued behind every
    /// event already emitted) under the runs lock and the state lock, in the
    /// same step as the lists start being published: no runs/rooms list or
    /// state patch can fall between the snapshot and the response.
    /// </summary>
    internal void Hello(Action<AppSnapshotDto> reply)
    {
        bool first;
        lock (_runsLock)
        {
            first = !_helloDone;
            _helloDone = true;
            Snapshot(handshake: true, reply);
        }
        if (first)
        {
            // A developer copy never answers for the installed client's update.
            if (!Options.Isolated && !Options.MultiInstance)
            {
                StartupMarker.Write();
                // Install moved the previous process's exe to "<its name>.old",
                // whatever it was called: sweep every such leftover.
                var installDir = Updater.InstallDir;
                _ = UpdateFiles.CleanupOldUpdateFilesAsync(installDir,
                    extraOldExeNames: UpdateFiles.OldExeNamesIn(installDir));
            }
            // After this response: the UI has its strings by then.
            _ = Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                ReportStartupUpdate();
            });
        }
    }

    /// <summary>
    /// The full <c>AppSnapshot</c>, handed to <paramref name="deliver"/> (which
    /// posts the IPC response) while the runs lock and the state lock are held,
    /// so it leaves in order with the events. <paramref name="handshake"/>: the
    /// UI adopts the state from it (hello), so patches restart from here; a
    /// language switch only takes the strings and keeps its patched state.
    /// </summary>
    internal void Snapshot(bool handshake, Action<AppSnapshotDto> deliver)
    {
        var language = Language;
        lock (_runsLock)
        {
            var runs = BuildRuns();
            _lastRunsJson = IpcJson.Serialize(runs);
            var rooms = SafeBuildRooms() ?? [];
            _lastRoomsJson = IpcJson.Serialize(rooms);
            Ui.Deliver(handshake, state => deliver(new AppSnapshotDto(
                ClientVersion.Display,
                Config.DebugMode,
                language.Code(),
                [.. Languages.All.Select(l => new LanguageDto(l.Code(), l.Label()))],
                Strings.Default.TemplatesFor(language),
                state,
                runs,
                rooms)));
        }
    }

    // ---- settings ----

    private SettingsDto BuildSettings()
    {
        var c = Config;
        var corner = OverlayPlacement.ParseCorner(c.OverlayCorner).KeywordName();
        return new SettingsDto
        {
            Language = c.Language.Code(),
            ServerUrl = c.ServerUrl,
            ApiToken = c.ApiToken,
            TrackingOnly = c.TrackingOnly,
            TrackingPrivate = c.TrackingPrivate,
            RecordAudio = c.RecordAudio,
            RecordMaxTotalGb = c.RecordMaxTotalGb ?? 0,
            AutoPublish = c.AutoPublish,
            RecordDir = SafeRecordDir(),
            GhostRace = c.GhostRace,
            GhostOverlay = c.GhostOverlay,
            GhostMarker = c.GhostMarker,
            OverlayCorner = corner,
            PinshareEnabled = c.PinshareEnabled,
            PinshareChannel = c.PinshareChannel,
            AutoUpdate = c.AutoUpdate,
            CloseToTray = c.CloseToTray,
            Autostart = _autostartEnabled,
            StartMinimized = c.StartMinimized,
            RankToast = c.RankToast,
            TriggerLog = c.TriggerLog,
        };
    }

    private string SafeRecordDir()
    {
        try
        {
            return RecordDir();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Config.RecordDir;
        }
    }

    /// <summary>Autostart checkbox (R17): write, then read the registry back.</summary>
    internal bool SetAutostart(bool enabled)
    {
        _autostart.SetEnabled(enabled);
        _autostartEnabled = _autostart.IsEnabled();
        Ui.InvalidateSettings();
        return _autostartEnabled;
    }

    /// <summary>save-config!, logged instead of thrown (the settings stay in memory).</summary>
    internal void SaveConfig()
    {
        try
        {
            Config.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RecordingLog.Write("config.sexp save failed: " + e.Message);
        }
    }

    // ---- server / token / login ----

    /// <summary>
    /// check-server (gui.lisp:1412): GET /api/quests, merge the server's quest
    /// definitions, cross-check the builtin trigger slugs.
    /// </summary>
    internal async Task CheckServerAsync()
    {
        try
        {
            var quests = await Api.FetchQuestsAsync(_shutdown.Token).ConfigureAwait(false);
            var element = JsonSerializer.SerializeToElement(quests);
            var serverDefs = Catalog.SetServerQuests(element);
            var slugs = quests.Select(q => q?["slug"] is JsonValue v && v.TryGetValue<string>(out var slug) ? slug : null)
                .OfType<string>().ToList();
            var unknown = QuestCatalog.UnknownSlugs(slugs, Catalog.Builtin);
            Ui.SetServer(Line.Ok(Msg.Of("server-ok", quests.Count, serverDefs, unknown.Count > 0 ? unknown.Count : null)));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Ui.SetServer(Line.Error(StatusMsgs.ServerError(e)));
        }
    }

    /// <summary>
    /// check-token (gui.lisp:1486): the token line, the Pin Share verdict, the
    /// moderator role and auto-publish mirror, the guest merge and the queue
    /// flush. <paramref name="onInvalid"/> runs on a definite 401 only.
    /// Checks finish in any order: a result applies only while its check is
    /// the latest and its token still the configured one (<see cref="TokenCheckGate"/>);
    /// a superseded check applies nothing and returns null.
    /// </summary>
    internal async Task<TokenCheckResult?> CheckTokenAsync(Action? onInvalid = null)
    {
        var ticket = _tokenChecks.Begin(Config.ApiToken);
        bool Current() => ticket.IsCurrent(Config.ApiToken);
        if (Auth.IsUnlinked)
        {
            // Unlinked is a supported state, not an error (R20).
            Permission.Set(false);
            Ui.SetToken(Line.Neutral(Msg.Of("token-unlinked")));
            return new TokenCheckResult(TokenCheckKind.Unlinked);
        }
        Ui.SetToken(Line.Busy(Msg.Of("token-checking")));
        var result = await Auth.CheckTokenAsync(user =>
        {
            if (!Current()) return;
            // The rollout verdict first, before anything fallible.
            Permission.Set(user.PinShareAllowed);
            ApplyModerator(user.IsModerator);
            ApplyAutoPublish(user.AutoPublish);
        }, _shutdown.Token).ConfigureAwait(false);
        if (!Current())
        {
            RecordingLog.Write($"token check: a superseded {result.Kind} result was ignored");
            return null;
        }
        switch (result.Kind)
        {
            case TokenCheckKind.Ok:
                Ui.SetToken(Line.Ok(Msg.Of("token-ok", result.User?.Username ?? "")));
                // A verified token flushes the local backlog on the next pass.
                Poll.RequestRetry();
                break;
            case TokenCheckKind.Unauthorized:
                Permission.Set(false);
                Ui.SetToken(Line.Error(Msg.Of("token-invalid")));
                onInvalid?.Invoke();
                break;
            case TokenCheckKind.Error:
                Ui.SetToken(Line.Error(StatusMsgs.TokenError(result.Error)));
                break;
        }
        return result;
    }

    /// <summary>apply-moderator-role: cache the role; the Rooms tab follows the state flag (no rebuild).</summary>
    private void ApplyModerator(bool moderator)
    {
        if (Config.Moderator != moderator)
        {
            Config.Moderator = moderator;
            SaveConfig();
        }
        Ui.SetModerator(moderator);
        PublishRooms();
    }

    /// <summary>apply-auto-publish: the server is the truth; mirror it into the cache.</summary>
    private void ApplyAutoPublish(bool autoPublish)
    {
        if (Config.AutoPublish == autoPublish) return;
        Config.AutoPublish = autoPublish;
        SaveConfig();
    }

    /// <summary>check-token's on-invalid at startup: a revoked token heals itself when login.txt is there.</summary>
    private void ReloginWithFile()
    {
        if (Credentials.Present(Options.ExeDir)) StartFileLogin();
    }

    /// <summary>prompt-for-token-setup (gui.lisp:758): no token + login.txt = file login. Never the browser.</summary>
    private void PromptForTokenSetup()
    {
        if (Auth.IsUnlinked && Credentials.Present(Options.ExeDir)) StartFileLogin();
    }

    /// <summary>run-file-login-flow (gui.lisp:836); success hands over to TokenLinked → check-token.</summary>
    internal void StartFileLogin() => _ = Task.Run(async () =>
    {
        var credentials = Credentials.Read(Credentials.PathIn(Options.ExeDir));
        if (credentials is null)
        {
            Ui.SetToken(Line.Error(Msg.Of("file-login-bad-file")));
            return;
        }
        Ui.SetToken(Line.Busy(Msg.Of("file-login-checking")));
        var result = await Auth.LoginWithCredentialsAsync(credentials.Username, credentials.Password, _shutdown.Token)
            .ConfigureAwait(false);
        switch (result.Outcome)
        {
            case FileLoginOutcome.BadFile:
                Ui.SetToken(Line.Error(Msg.Of("file-login-bad-file")));
                break;
            case FileLoginOutcome.Invalid:
                Ui.SetToken(Line.Error(Msg.Of("file-login-invalid")));
                break;
            case FileLoginOutcome.Failed:
                Ui.SetToken(Line.Error(Msg.Of("file-login-failed", result.Error)));
                break;
        }
    });

    /// <summary>run-pairing-flow (gui.lisp:783) on a worker; progress on the token line.</summary>
    internal void StartPairing()
    {
        if (Auth.IsPairing || Ui.Pairing) return;
        Ui.SetPairing(true);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await Auth.RunPairingAsync(OpenExternal,
                    () => Ui.SetToken(Line.Busy(Msg.Of("pairing-waiting"))), _shutdown.Token).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case PairingOutcome.Expired:
                        Ui.SetToken(Line.Neutral(Msg.Of("pairing-expired")));
                        break;
                    case PairingOutcome.FailedToStart:
                        Ui.SetToken(Line.Error(Msg.Of("pairing-failed", result.Error)));
                        break;
                }
            }
            finally
            {
                Ui.SetPairing(false);
            }
        });
    }

    /// <summary>pinshare-permission-loop: re-ask /api/me every 30 minutes while linked.</summary>
    private async Task PermissionLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PinSharePermission.RefreshInterval, _shutdown.Token).ConfigureAwait(false);
                if (await Auth.RefreshPinShareAllowedAsync(_shutdown.Token).ConfigureAwait(false) is { } allowed)
                    Permission.Set(allowed);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                RecordingLog.Write("pin share permission refresh failed: " + e.Message);
            }
        }
    }

    // ---- updates ----

    private string VersionText => ClientVersion.Display;

    internal void SetVersionNote(Msg? note, bool error = false) =>
        Ui.SetVersion(new Line(Msg.Of("version-status", VersionText, note), error ? Tone.Error : Tone.Neutral));

    /// <summary>report-startup-update (gui.lisp:1752).</summary>
    private void ReportStartupUpdate()
    {
        switch (_startupNote)
        {
            case StartupUpdateNote.UpToDate:
                SetVersionNote(Msg.Of("update-up-to-date"));
                break;
            case StartupUpdateNote.Rejected:
                SetVersionNote(Msg.Of("update-rejected"), error: true);
                break;
            case StartupUpdateNote.NotWritable:
                OfferManualDownload(emit: true);
                break;
            case StartupUpdateNote.DownloadFailed:
                SetVersionNote(Msg.Of("update-download-failed"), error: true);
                EmitNotice(Notice.Fail("update-download-failed-dialog"));
                break;
        }
    }

    /// <summary>offer-manual-download (gui.lisp:1643): not writable → the release page, on Yes.</summary>
    internal Notice OfferManualDownload(bool emit)
    {
        SetVersionNote(Msg.Of("update-not-writable"), error: true);
        var notice = new Notice(Msg.Of("update-not-writable-confirm"), ConfirmUrl: Updater.ReleasePageUrl);
        if (emit) EmitNotice(notice);
        return notice;
    }

    /// <summary>
    /// apply-update-restart (gui.lisp:1692): stop the poll loop (the recorder
    /// winds down), swap the exe and start the new build, then exit. On a
    /// failed hand-over everything was rolled back: re-take the single-instance
    /// guard and carry on with a fresh poll loop. Runs on its own thread, so it
    /// may be called from the poll thread (a deferred update) or an IPC worker.
    /// </summary>
    /// <remarks>
    /// Single-flight: a manual check and a parked update handed back by the
    /// poll loop can both arrive; a second hand-over while one runs would
    /// start two new processes, and the loser's exit would leave no client.
    /// </remarks>
    internal void ApplyUpdateRestart(string zip, string tag)
    {
        if (Interlocked.CompareExchange(ref _applyingUpdate, 1, 0) != 0)
        {
            RecordingLog.Write($"update {tag}: a hand-over is already in progress; ignored");
            return;
        }
        SetVersionNote(Msg.Of("update-restarting", tag));
        var thread = new Thread(() =>
        {
            Poll.Stop(TimeSpan.FromSeconds(10));
            var started = UpdateLauncher.ApplyAndRestart(zip, SingleInstance.ReleaseProcessClaim, RecordingLog.Write);
            if (started)
            {
                Shell.Quit(); // the flag stays set: this process is leaving
                return;
            }
            if (!Options.MultiInstance) SingleInstance.ClaimForProcess();
            SetVersionNote(Msg.Of("update-download-failed"), error: true);
            // The old loop's unwind closed the trigger log; reopen it like Start did.
            if (Config.TriggerLog) Try("trigger log", () => TriggerLog.Start());
            Poll = new PollLoop(_pollServices);
            Poll.Start();
            // Retry requests made while the old loop was stopping were dropped
            // with it, and its cancelled pass left entries queued: one pass on
            // the new loop (quiet when nothing is unsent).
            Poll.RequestRetry();
            Volatile.Write(ref _applyingUpdate, 0);
        })
        { Name = "eta-client-update-apply", IsBackground = true };
        thread.Start();
    }

    // ---- the status tick (update-game-status, gui.lisp:1773) ----

    private void OnTick(PollTick tick)
    {
        var detector = Frames.Detector;
        var recording = Recorder.State == RecorderState.Recording;
        var inQuest = detector.State == DetectorState.InQuest;

        var baseLine = tick.Rejection is { } rejection
            ? Line.Error(Msg.Of("game-signature-refused", StatusMsgs.Signature(rejection)))
            : tick.ReadFailing
                ? Line.Error(Msg.Of("game-read-failed"))
                : tick.Attached
                    ? Line.Ok(Msg.Of("game-attached"))
                    : Line.Busy(Msg.Of("game-searching"));
        var game = Recorder.LastError is { } error
            ? Line.Error(Msg.Of("game-status-with-error", baseLine.Msg, error))
            : baseLine;

        JsonObject quest;
        string title;
        if (inQuest)
        {
            // Formatted once: the pane and the title must agree (R21).
            var clock = RunDisplay.FormatSplitClock(detector.ElapsedMs ?? 0);
            var race = Ghost.Race;
            var ghost = race?.Ghost;
            quest = UiState.QuestActive(detector.ActiveDef?.Slug ?? "", Math.Max(0, detector.ActiveCount - 1), clock, recording,
                ghost is null ? null : GhostFormat.RunTime(ghost.TimeMs),
                ghost is not null && race!.DeltaMs is { } delta ? GhostFormat.Delta(delta, ghost.Precision) : null);
            // The taskbar truncates from the right, so the time goes first.
            title = $"{clock}{Ghost.TitleSuffix()}{(recording ? " [REC]" : "")} - {WindowTitle}";
        }
        else
        {
            quest = tick.Snapshot?.QuestName is { } name ? UiState.QuestWaiting(name) : UiState.QuestNone();
            title = WindowTitle;
        }
        SetTitle(title);

        Ui.Batch(ui =>
        {
            ui.SetGame(game);
            ui.SetQuest(quest);
            ui.SetPinshare(StatusMsgs.PinShare(PinShare.Status));
            ui.SetPinSet(StatusMsgs.PinSet(PinSets.Current, PinSets.QuestSlugs));
        });
        Try("overlay", () => UpdateOverlay(inQuest, recording));
        PublishRooms();
    }

    private void SetTitle(string title)
    {
        if (title == _lastTitle) return;
        _lastTitle = title;
        _setTitle(title);
    }

    /// <summary>
    /// update-ghost-overlay (overlay-win32.lisp:1254): persist a finished
    /// Ctrl+drag first, then show the timer/ghost panel during a quest or hide it.
    /// </summary>
    private void UpdateOverlay(bool inQuest, bool recording)
    {
        if (Overlay.TakeDraggedPosition() is { } dragged)
        {
            Config.OverlayCorner = "CUSTOM";
            Config.Set(ConfigKeys.OverlayPosition, OverlayPlacement.ToSexp(dragged));
            SaveConfig();
        }
        if (!(Config.GhostOverlay && inQuest))
        {
            Overlay.Hide();
            return;
        }
        var detector = Frames.Detector;
        long? elapsed = detector.Telemetry is { } t ? LispMath.ElapsedMs(Clock.Now, t.StartTime, Clock.TicksPerSecond) : null;
        var data = elapsed is { } e ? Ghost.OverlayData(e, Config.GhostMarker) : null;
        Overlay.Show(OverlayContent.ForQuest(detector.ElapsedMs, recording, Ghost.Race, data,
            OverlayPlacement.ParseCorner(Config.OverlayCorner),
            OverlayPlacement.ParseCustom(Config.Get(ConfigKeys.OverlayPosition)),
            Language));
    }

    private void RefreshPinShareLines() => Ui.Batch(ui =>
    {
        ui.SetPinshare(StatusMsgs.PinShare(PinShare.Status));
        ui.SetPinSet(StatusMsgs.PinSet(PinSets.Current, PinSets.QuestSlugs));
    });

    // ---- runs and rooms lists ----

    /// <summary>The Runs list (gui.lisp:117 column-function), newest first.</summary>
    internal List<RunRowDto> BuildRuns()
    {
        var language = Language;
        var hasToken = Config.SubmissionToken.Length > 0;
        var videoUpload = Config.VideoUpload;
        return Queue.Entries.Select(entry =>
        {
            var data = entry.Data;
            var status = entry.Status;
            var error = !entry.Is(RunKeys.VideoAttached) && status is RunStatus.Rejected or RunStatus.Failed;
            return new RunRowDto(
                entry.Id.ToString(),
                entry.QuestName ?? entry.QuestSlug ?? "",
                entry.TimeMs ?? 0,
                entry.Get("PARTY-SIZE").AsLong ?? 0,
                entry.Is("PB"),
                StatusMsgs.RunVideo(data, Queue.UploadProgressPercent(data)),
                [Msg.Raw(RunDisplay.RunStatusLabel(data, language, hasToken, videoUpload))],
                error,
                entry.Url,
                entry.VideoPath is not null);
        }).ToList();
    }

    /// <summary>refresh-runs-list: send the list only when it changed (keeps selection, R2).</summary>
    internal void PublishRuns(bool force = false)
    {
        if (_sink is null) return;
        // Build, compare and emit under one lock: built outside, an older list
        // could be emitted after a newer one (Queue.Changed fires on any thread).
        // The hello snapshot is replied under the same lock (see Hello).
        lock (_runsLock)
        {
            if (!_helloDone) return;
            var rows = BuildRuns();
            var json = IpcJson.Serialize(rows);
            if (!force && json == _lastRunsJson) return;
            _lastRunsJson = json;
            _sink.Emit("runs", rows);
        }
    }

    private readonly object _runsLock = new();

    /// <summary>run-room-rows as the Rooms tab shows them (moderators only; others get none).</summary>
    internal List<RoomRowDto> BuildRooms()
    {
        if (!Ui.Moderator) return [];
        return RunLogs.RoomRows().Select((row, i) => RoomDto(row, i)).ToList();
    }

    internal static RoomRowDto RoomDto(RoomRow row, int index) => new(
        string.Create(CultureInfo.InvariantCulture, $"{index}:{row.Area}:{row.Kind}:{row.Name}:{row.Trigger.Label}"),
        row.Area,
        row.Kind == RoomRowKind.Clear ? "clear" : "enemy",
        row.Name,
        StatusMsgs.TriggerJson(row.Trigger));

    /// <summary>refresh-rooms-list: rebuild only when rooms-list-signature moved.</summary>
    internal void PublishRooms()
    {
        if (_sink is null) return;
        var kills = RunLogs.Kills;
        var signature = (kills.Count, RunLogs.Switches.Count, kills.Count > 0 ? kills[^1].Id : (int?)null, Ui.Moderator);
        lock (_runsLock)
        {
            if (!_helloDone || _roomsSignature == signature) return;
            // A failed build leaves the signature alone, so the next tick retries.
            if (SafeBuildRooms() is not { } rows) return;
            _roomsSignature = signature;
            var json = IpcJson.Serialize(rows);
            if (json == _lastRoomsJson) return;
            _lastRoomsJson = json;
            _sink.Emit("rooms", rows);
        }
    }

    /// <summary><see cref="BuildRooms"/>, logged instead of thrown (its callers include the token check's onVerified); null on failure.</summary>
    private List<RoomRowDto>? SafeBuildRooms()
    {
        try
        {
            return BuildRooms();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            RecordingLog.Write("rooms list failed: " + e.Message);
            return null;
        }
    }

    // ---- shell helpers ----

    /// <summary>A tray balloon in the current language (notify-user).</summary>
    internal void Notify(Msg title, Msg text, NotifyKind kind, string? url = null) =>
        Shell.Notify(title.Render(Language), text.Render(Language), kind, url);

    /// <summary>open-in-browser: http(s) only, never throws.</summary>
    public static void OpenExternal(string url)
    {
        if (!Urls.IsValidHttpUrl(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RecordingLog.Write("open url failed: " + e.Message);
        }
    }

    // ---- quit ----

    /// <summary>
    /// quit-app's middle (main.lisp:419), run by <see cref="ShellHost.Quit"/>
    /// on whichever thread quits: stop the poll loop (≤10 s; the recorder shuts
    /// down on it), then the overlay, the relay and the workers. The shell then
    /// removes the tray icon and calls ExitProcess(0).
    /// </summary>
    private void PrepareQuit()
    {
        _shutdown.Cancel();
        if (!Poll.Stop(TimeSpan.FromSeconds(10))) RecordingLog.Write("quit: the poll loop did not stop within 10 s");
        Try("overlay", Overlay.Dispose);
        Try("pin share", () => PinShare.Stop(TimeSpan.FromSeconds(2)));
        Try("trigger log", TriggerLog.Close);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        Poll.Stop(TimeSpan.FromSeconds(10));
        Overlay.Dispose();
        PinShare.Dispose();
        Shell.Dispose();
        TriggerLog.Dispose();
    }

    internal static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            RecordingLog.Write($"{what} failed: {e.Message}");
        }
    }
}
