using RappyRuns.Core.I18n;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Config;

/// <summary>Raised after config values change. <see cref="Key"/> is null when everything may have changed (a reload).</summary>
public sealed class ConfigChangedEventArgs(string? key) : EventArgs
{
    public string? Key { get; } = key;
}

/// <summary>
/// The client settings in <c>%APPDATA%\ephinea-ta-client\config.sexp</c>, a
/// port of config.lisp (spec core §3.1-3.4). The file stays in the Lisp
/// client's format - a keyword plist printed by <see cref="SexpWriter"/> - so a
/// downgrade to the Lisp client keeps the token and every setting, and keys
/// this client does not know (<c>:overlay-capturable</c>, <c>:ghost-video</c>,
/// whatever a newer or older client wrote) survive a save untouched.
/// <para>
/// Thread-safe: the poll loop, upload worker and UI all read settings. Values
/// are immutable <see cref="SexpNode"/>s, so a read never sees a torn value.
/// </para>
/// </summary>
public sealed class ConfigStore
{
    /// <summary>The config folder name under %APPDATA% (shared with the Lisp client).</summary>
    public const string FolderName = "ephinea-ta-client";

    /// <summary>The releases repository the updater uses unless <c>:update-repo</c> overrides it (updater.lisp:14).</summary>
    public const string DefaultUpdateRepo = "PSOBBAITools/rappyruns-client";

    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly string[] _args;
    private Plist _config;

    /// <param name="directory">The folder holding config.sexp; production passes <see cref="DefaultDirectory"/>, tests a temp folder.</param>
    /// <param name="commandLineArgs">The process arguments (without the exe), for <c>--debug</c> and <c>--minimized</c>.</param>
    public ConfigStore(string directory, IEnumerable<string>? commandLineArgs = null)
    {
        Directory = directory;
        _args = commandLineArgs?.ToArray() ?? [];
        _config = Migrate(ConfigKeys.DefaultPlist());
    }

    /// <summary>A store for <paramref name="directory"/> with config.sexp already loaded.</summary>
    public static ConfigStore Open(string directory, IEnumerable<string>? commandLineArgs = null)
    {
        var store = new ConfigStore(directory, commandLineArgs);
        store.Load();
        return store;
    }

    /// <summary>Fired after <see cref="Set"/> or <see cref="Load"/>; handlers run on the thread that made the change.</summary>
    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public string Directory { get; }

    public string FilePath => Path.Combine(Directory, "config.sexp");

    /// <summary>The queue file lives next to the config (store.lisp:280 queue-path).</summary>
    public string QueuePath => Path.Combine(Directory, "queue.sexp");

    /// <summary>
    /// <c>%APPDATA%\ephinea-ta-client</c>, or the home directory's
    /// ephinea-ta-client when APPDATA is unset (config.lisp:57 config-dir).
    /// </summary>
    public static string DefaultDirectory()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, FolderName);
    }

    /// <summary>
    /// load-config!: read config.sexp and migrate it. A missing file (or one
    /// that is not a keyword plist) means a fresh copy of the defaults. A file
    /// that exists but cannot be read as a plist is copied aside to
    /// config.sexp.bad first, so a corrupt file never silently eats the token
    /// for good (spec core §22 #20; the Lisp client just fell back).
    /// </summary>
    public void Load()
    {
        var node = SexpReader.TryReadFile(FilePath);
        var plist = node is null || node.IsNil ? null : Plist.From(node);
        if (plist is null && File.Exists(FilePath) && !(node?.IsNil ?? false))
        {
            try
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        lock (_lock) _config = Migrate(plist ?? ConfigKeys.DefaultPlist());
        Changed?.Invoke(this, new ConfigChangedEventArgs(null));
    }

    /// <summary>
    /// save-config!: write the whole plist back (atomically, unlike the Lisp
    /// client). Only keys present in memory are written - after a first run
    /// that is every default except the forced keys, as in Lisp.
    /// Throws on I/O failure, like the Lisp write did.
    /// </summary>
    public void Save()
    {
        // Serialized (the atomic write shares one temp file) and each save
        // snapshots under the lock, so the file is never older than memory.
        lock (_saveLock)
        {
            SexpNode form;
            lock (_lock) form = _config.ToSexp();
            SexpWriter.WriteFile(FilePath, form);
        }
    }

    /// <summary>
    /// migrate-config (config.lisp:77): scrub the dropped
    /// <c>:token-prompt-shown</c> key and every <see cref="ConfigKeys.Forced"/>
    /// key, so those behaviors always fall back to their fixed default.
    /// Mutates and returns <paramref name="config"/>.
    /// </summary>
    public static Plist Migrate(Plist config)
    {
        config.Remove(ConfigKeys.TokenPromptShown);
        foreach (var key in ConfigKeys.Forced) config.Remove(key);
        return config;
    }

    /// <summary>
    /// config-value: the value in the file when the key is present - even when
    /// that value is NIL - else the default (NIL for keys without one).
    /// </summary>
    public SexpNode Get(string key)
    {
        lock (_lock) return _config.Get(key) ?? ConfigKeys.DefaultFor(key);
    }

    /// <summary>(setf config-value): set in memory (a new key goes to the front). Call <see cref="Save"/> to persist.</summary>
    public void Set(string key, SexpNode value)
    {
        lock (_lock) _config.Set(key, value);
        Changed?.Invoke(this, new ConfigChangedEventArgs(key.ToUpperInvariant()));
    }

    /// <summary>True when the in-memory plist has <paramref name="key"/> (so <see cref="Get"/> does not fall back to the default).</summary>
    public bool Contains(string key)
    {
        lock (_lock) return _config.Contains(key);
    }

    /// <summary>A copy of the whole in-memory plist, for diagnostics and tests.</summary>
    public Plist Snapshot()
    {
        lock (_lock) return _config.Clone();
    }

    /// <summary>Lisp truthiness: anything but NIL is true.</summary>
    public bool GetBool(string key) => Get(key).IsTrue;

    public void SetBool(string key, bool value) => Set(key, SexpNode.Bool(value));

    /// <summary>The string value, or "" when the value is not a string (NIL included).</summary>
    public string GetString(string key) => Get(key).AsString ?? "";

    public void SetString(string key, string value) => Set(key, SexpNode.Str(value));

    /// <summary>The keyword name (upper case, no colon), or null when the value is not a keyword.</summary>
    public string? GetKeyword(string key) => Get(key).KeywordName;

    public void SetKeyword(string key, string name) => Set(key, SexpNode.Kw(name));

    /// <summary>The value as a number (integer or float), or null.</summary>
    public double? GetNumber(string key) => Get(key).AsNumber;

    // ---- typed accessors for every key in spec core §3.2 ----

    public string ServerUrl { get => GetString(ConfigKeys.ServerUrl); set => SetString(ConfigKeys.ServerUrl, value); }

    public string ApiToken { get => GetString(ConfigKeys.ApiToken); set => SetString(ConfigKeys.ApiToken, value); }

    public string AnonToken { get => GetString(ConfigKeys.AnonToken); set => SetString(ConfigKeys.AnonToken, value); }

    /// <summary>valid-language (i18n.lisp:16): :ja is Japanese, anything else English.</summary>
    public Language Language
    {
        get => GetKeyword(ConfigKeys.Language) == "JA" ? Language.Ja : Language.En;
        set => SetKeyword(ConfigKeys.Language, value == Language.Ja ? "JA" : "EN");
    }

    /// <summary>Forced (hidden): always true.</summary>
    public bool AutoSubmit => GetBool(ConfigKeys.AutoSubmit);

    /// <summary>Forced (hidden): always true - aborted runs are submitted (private on the server).</summary>
    public bool SubmitAborted => GetBool(ConfigKeys.SubmitAborted);

    /// <summary>Forced (hidden): always false.</summary>
    public bool CompletionSound => GetBool(ConfigKeys.CompletionSound);

    /// <summary>Forced (hidden): always true.</summary>
    public bool RecordEnabled => GetBool(ConfigKeys.RecordEnabled);

    /// <summary>Forced (hidden): always true - saved recordings upload automatically.</summary>
    public bool VideoUpload => GetBool(ConfigKeys.VideoUpload);

    public bool AutoUpdate { get => GetBool(ConfigKeys.AutoUpdate); set => SetBool(ConfigKeys.AutoUpdate, value); }

    public bool TriggerLog { get => GetBool(ConfigKeys.TriggerLog); set => SetBool(ConfigKeys.TriggerLog, value); }

    public bool TrackingOnly { get => GetBool(ConfigKeys.TrackingOnly); set => SetBool(ConfigKeys.TrackingOnly, value); }

    public bool TrackingPrivate { get => GetBool(ConfigKeys.TrackingPrivate); set => SetBool(ConfigKeys.TrackingPrivate, value); }

    public bool RecordAudio { get => GetBool(ConfigKeys.RecordAudio); set => SetBool(ConfigKeys.RecordAudio, value); }

    /// <summary>The recordings-folder cap in GB as stored (integer or float); null when unset/not a number.</summary>
    public double? RecordMaxTotalGb => GetNumber(ConfigKeys.RecordMaxTotalGb);

    /// <summary>Stores the cap: whole numbers as integers (like the default 20), fractions as single floats.</summary>
    public void SetRecordMaxTotalGb(double? gb) =>
        Set(ConfigKeys.RecordMaxTotalGb, gb switch
        {
            null => SexpNode.Nil,
            { } v when v == Math.Floor(v) && Math.Abs(v) < long.MaxValue => SexpNode.Int((long)v),
            { } v => new SFloat((float)v),
        });

    /// <summary>
    /// record-max-total-bytes (recording.lisp:1427): the cap in bytes,
    /// <c>round(gb * 1024^3)</c> with banker's rounding, or null when unset,
    /// zero or negative (unlimited). A single-float setting is multiplied in
    /// single precision, as in Lisp.
    /// </summary>
    public long? RecordMaxTotalBytes => RecordMaxTotalBytesOf(Get(ConfigKeys.RecordMaxTotalGb));

    /// <summary>The pure part of <see cref="RecordMaxTotalBytes"/>.</summary>
    public static long? RecordMaxTotalBytesOf(SexpNode value) => value switch
    {
        SInteger { Value: > 0 } i => i.Value * 1024 * 1024 * 1024,
        SFloat { Value: > 0, IsDouble: false } f => (long)MathF.Round((float)f.Value * 1073741824f, MidpointRounding.ToEven),
        SFloat { Value: > 0 } d => (long)Math.Round(d.Value * 1073741824d, MidpointRounding.ToEven),
        _ => null,
    };

    public bool AutoPublish { get => GetBool(ConfigKeys.AutoPublish); set => SetBool(ConfigKeys.AutoPublish, value); }

    public bool HwEncode { get => GetBool(ConfigKeys.HwEncode); set => SetBool(ConfigKeys.HwEncode, value); }

    public string FfmpegPath { get => GetString(ConfigKeys.FfmpegPath); set => SetString(ConfigKeys.FfmpegPath, value); }

    public string RecordDir { get => GetString(ConfigKeys.RecordDir); set => SetString(ConfigKeys.RecordDir, value); }

    public bool Moderator { get => GetBool(ConfigKeys.Moderator); set => SetBool(ConfigKeys.Moderator, value); }

    public bool CloseToTray { get => GetBool(ConfigKeys.CloseToTray); set => SetBool(ConfigKeys.CloseToTray, value); }

    public bool RankToast { get => GetBool(ConfigKeys.RankToast); set => SetBool(ConfigKeys.RankToast, value); }

    public bool GhostRace { get => GetBool(ConfigKeys.GhostRace); set => SetBool(ConfigKeys.GhostRace, value); }

    public bool GhostOverlay { get => GetBool(ConfigKeys.GhostOverlay); set => SetBool(ConfigKeys.GhostOverlay, value); }

    public bool GhostMarker { get => GetBool(ConfigKeys.GhostMarker); set => SetBool(ConfigKeys.GhostMarker, value); }

    /// <summary>
    /// The overlay anchor keyword name ("TOP-RIGHT", ..., "CUSTOM"), as stored.
    /// Not validated: an unknown anchor is placed top-right by the overlay
    /// code, as in ghost.lisp. "TOP-RIGHT" when the value is not a keyword.
    /// </summary>
    public string OverlayCorner
    {
        get => GetKeyword(ConfigKeys.OverlayCorner) ?? "TOP-RIGHT";
        set => SetKeyword(ConfigKeys.OverlayCorner, value);
    }

    /// <summary>
    /// The Ctrl+dragged panel spot (x-frac, y-frac), or null when unset or not
    /// a two-number list (ghost.lisp overlay-panel-origin's guard).
    /// Written back as two single floats, as the Lisp overlay does.
    /// </summary>
    public (float X, float Y)? OverlayPosition
    {
        get
        {
            var node = Get(ConfigKeys.OverlayPosition);
            if (node is SList { Tail: null, Items.Count: >= 2 } list
                && list.Items[0].AsNumber is { } x && list.Items[1].AsNumber is { } y)
                return ((float)x, (float)y);
            return null;
        }
        set => Set(ConfigKeys.OverlayPosition, value is { } p
            ? SexpNode.List(new SFloat(p.X), new SFloat(p.Y))
            : SexpNode.Nil);
    }

    public bool PinshareAllowed { get => GetBool(ConfigKeys.PinshareAllowed); set => SetBool(ConfigKeys.PinshareAllowed, value); }

    public bool PinshareEnabled { get => GetBool(ConfigKeys.PinshareEnabled); set => SetBool(ConfigKeys.PinshareEnabled, value); }

    public string PinshareChannel { get => GetString(ConfigKeys.PinshareChannel); set => SetString(ConfigKeys.PinshareChannel, value); }

    public string PinshareServer { get => GetString(ConfigKeys.PinshareServer); set => SetString(ConfigKeys.PinshareServer, value); }

    /// <summary>The saved <c>:start-minimized</c> setting alone (see <see cref="StartupMinimized"/> for the launch decision).</summary>
    public bool StartMinimized { get => GetBool(ConfigKeys.StartMinimized); set => SetBool(ConfigKeys.StartMinimized, value); }

    /// <summary>The saved <c>:debug</c> setting alone (see <see cref="DebugMode"/>).</summary>
    public bool DebugSetting { get => GetBool(ConfigKeys.Debug); set => SetBool(ConfigKeys.Debug, value); }

    /// <summary>
    /// debug-mode-p (config.lisp:97): developer settings (the Server URL
    /// field) show when <c>:debug</c> is set or the client was launched with
    /// <c>--debug</c> (compared case-insensitively, whole argument).
    /// </summary>
    public bool DebugMode => DebugSetting || HasArg("--debug");

    /// <summary>
    /// startup-minimized-p (config.lisp:108): launch to the tray when
    /// <c>:start-minimized</c> is on or <c>--minimized</c> was passed (the
    /// autostart entry passes it).
    /// </summary>
    public bool StartupMinimized => StartMinimized || HasArg("--minimized");

    /// <summary>
    /// resolve-update-repo (updater.lisp:32): a non-empty string
    /// <c>:update-repo</c> override, else <see cref="DefaultUpdateRepo"/>.
    /// </summary>
    public string ResolveUpdateRepo() =>
        Get(ConfigKeys.UpdateRepo).AsString is { Length: > 0 } repo ? repo : DefaultUpdateRepo;

    /// <summary>
    /// submission-token (api-client.lisp:302): the linked account's token when
    /// set, else the anonymous guest token, else "". Both normalized.
    /// </summary>
    public string SubmissionToken
    {
        get
        {
            var real = NormalizeToken(ApiToken);
            return real.Length > 0 ? real : NormalizeToken(AnonToken);
        }
    }

    /// <summary>unlinked-p (store.lisp:54): no site account linked. A guest token does not count.</summary>
    public bool IsUnlinked => NormalizeToken(ApiToken).Length == 0;

    /// <summary>api-url (api-client.lisp:211) against the configured server.</summary>
    public string ApiUrl(string path) => ApiUrl(ServerUrl, path);

    /// <summary>api-url: the server URL with trailing slashes trimmed, then <paramref name="path"/>.</summary>
    public static string ApiUrl(string serverUrl, string path) => serverUrl.TrimEnd('/') + path;

    /// <summary>
    /// How the settings form stores a typed Server URL (gui.lisp:601):
    /// trailing slashes and spaces trimmed (<c>string-right-trim "/ "</c>).
    /// </summary>
    public static string TrimServerUrl(string url) => url.TrimEnd('/', ' ');

    /// <summary>
    /// normalize-token (api-client.lisp:223): a pasted token trimmed of the
    /// spaces, tabs and line breaks a browser copy drags along; null becomes "".
    /// </summary>
    public static string NormalizeToken(string? token) => (token ?? "").Trim(' ', '\t', '\r', '\n');

    private bool HasArg(string flag) =>
        _args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}
