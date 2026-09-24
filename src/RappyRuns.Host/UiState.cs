using System.Text.Json.Nodes;
using RappyRuns.Core;

namespace RappyRuns.Host;

/// <summary>
/// The host's copy of <c>HostState</c> (ipc.md) and the <c>state</c> event
/// protocol: after the UI's <c>app.hello</c> took a full snapshot, every
/// <see cref="Publish"/> sends only the top-level keys whose JSON changed
/// (the diffState reference in ui/src/lib/state.ts); nothing changed means no
/// event (ui-shell R1). Setters may be called from any thread.
/// </summary>
public sealed class UiState
{
    private readonly object _lock = new();
    private readonly Func<SettingsDto> _buildSettings;
    private readonly Dictionary<string, string> _sent = new(StringComparer.Ordinal);
    private IUiSink? _sink;
    private bool _live;
    private SettingsDto? _settings;

    private bool _moderator;
    private bool _pinshareAllowed;
    private Line _game = Line.Busy(Msg.Of("game-searching"));
    private Line _server = Line.Neutral(Msg.Of("server-not-checked"));
    private Line _token = Line.Neutral(Msg.Of("token-not-checked"));
    private JsonObject _quest = QuestNone();
    private Line _pinshare = Line.Neutral(Msg.Of("pinshare-status-off"));
    private Line _pinSet = Line.Neutral(Msg.Of("pinshare-pin-set-none-idle"));
    private Line _version = Line.Neutral(Msg.Of("version-status", ClientVersion.Display, null));
    private bool _pairing;
    private bool _updating;

    /// <param name="buildSettings">Builds the Settings group from config (called only after <see cref="InvalidateSettings"/>).</param>
    public UiState(Func<SettingsDto> buildSettings, bool moderator, bool pinshareAllowed)
    {
        _buildSettings = buildSettings;
        _moderator = moderator;
        _pinshareAllowed = pinshareAllowed;
    }

    /// <summary>Connects the event transport (the IPC host, once WebView2 is up).</summary>
    public void Attach(IUiSink sink)
    {
        lock (_lock) _sink = sink;
    }

    public bool Moderator
    {
        get { lock (_lock) return _moderator; }
    }

    public bool PinshareAllowed
    {
        get { lock (_lock) return _pinshareAllowed; }
    }

    public bool Pairing
    {
        get { lock (_lock) return _pairing; }
    }

    public bool Updating
    {
        get { lock (_lock) return _updating; }
    }

    public Line Game
    {
        get { lock (_lock) return _game; }
    }

    public Line Server
    {
        get { lock (_lock) return _server; }
    }

    public Line Token
    {
        get { lock (_lock) return _token; }
    }

    public Line Version
    {
        get { lock (_lock) return _version; }
    }

    public Line Pinshare
    {
        get { lock (_lock) return _pinshare; }
    }

    public JsonObject Quest
    {
        get { lock (_lock) return (JsonObject)_quest.DeepClone(); }
    }

    public void SetModerator(bool value) => Set(() => _moderator = value);

    public void SetPinshareAllowed(bool value) => Set(() => _pinshareAllowed = value);

    public void SetGame(Line value) => Set(() => _game = value);

    public void SetServer(Line value) => Set(() => _server = value);

    public void SetToken(Line value) => Set(() => _token = value);

    public void SetQuest(JsonObject value) => Set(() => _quest = value);

    public void SetPinshare(Line value) => Set(() => _pinshare = value);

    public void SetPinSet(Line value) => Set(() => _pinSet = value);

    public void SetVersion(Line value) => Set(() => _version = value);

    public void SetPairing(bool value) => Set(() => _pairing = value);

    public void SetUpdating(bool value) => Set(() => _updating = value);

    /// <summary>Several fields at once, one event.</summary>
    public void Batch(Action<UiState> changes)
    {
        lock (_lock)
        {
            _batch++;
            try
            {
                changes(this);
            }
            finally
            {
                _batch--;
            }
            if (_batch == 0) PublishLocked();
        }
    }

    private int _batch;

    /// <summary>A setting changed (config, registry): rebuild Settings on the next publish, and publish.</summary>
    public void InvalidateSettings() => Set(() => _settings = null);

    /// <summary>The Settings group as the UI sees it now.</summary>
    public SettingsDto Settings
    {
        get
        {
            lock (_lock) return _settings ??= _buildSettings();
        }
    }

    /// <summary>
    /// The full state for <c>app.hello</c>; from here on changes are sent as
    /// patches against it.
    /// </summary>
    public JsonObject Snapshot()
    {
        JsonObject? result = null;
        Deliver(handshake: true, state => result = state);
        return result!;
    }

    /// <summary>
    /// The full state handed to <paramref name="deliver"/> while the patch
    /// lock is held (<paramref name="handshake"/>: <see cref="Snapshot()"/>,
    /// else <see cref="Current"/>). The host posts its IPC response from
    /// there, so no patch can be emitted between the state it describes and
    /// the response leaving: patches made before arrive before it, later ones after.
    /// </summary>
    public void Deliver(bool handshake, Action<JsonObject> deliver)
    {
        lock (_lock)
        {
            JsonObject state;
            if (handshake)
            {
                state = BuildLocked();
                _sent.Clear();
                foreach (var (key, value) in state) _sent[key] = value?.ToJsonString() ?? "null";
                _live = true;
            }
            else
            {
                PublishLocked();
                state = BuildLocked();
            }
            deliver(state);
        }
    }

    /// <summary>
    /// The full state without resetting the patch baseline (a language switch:
    /// the UI keeps its patched copy, so pending changes are published first).
    /// </summary>
    public JsonObject Current()
    {
        JsonObject? result = null;
        Deliver(handshake: false, state => result = state);
        return result!;
    }

    /// <summary>Sends the changed keys, if any (the 4 Hz tick and every known change).</summary>
    public void Publish()
    {
        lock (_lock) PublishLocked();
    }

    private void Set(Action change)
    {
        lock (_lock)
        {
            change();
            if (_batch == 0) PublishLocked();
        }
    }

    private void PublishLocked()
    {
        if (!_live || _sink is null) return;
        var state = BuildLocked();
        var patch = new JsonObject();
        foreach (var (key, value) in state)
        {
            var json = value?.ToJsonString() ?? "null";
            if (_sent.TryGetValue(key, out var old) && old == json) continue;
            _sent[key] = json;
            patch[key] = value?.DeepClone();
        }
        // Emitted under the lock so patches leave in the order they were made.
        if (patch.Count > 0) _sink.Emit("state", patch);
    }

    private JsonObject BuildLocked() => new()
    {
        ["moderator"] = _moderator,
        ["pinshareAllowed"] = _pinshareAllowed,
        ["game"] = IpcJson.ToNode(_game),
        ["server"] = IpcJson.ToNode(_server),
        ["token"] = IpcJson.ToNode(_token),
        ["quest"] = _quest.DeepClone(),
        ["pinshare"] = IpcJson.ToNode(_pinshare),
        ["pinSet"] = IpcJson.ToNode(_pinSet),
        ["version"] = IpcJson.ToNode(_version),
        ["pairing"] = _pairing,
        ["updating"] = _updating,
        ["settings"] = IpcJson.ToNode(_settings ??= _buildSettings()),
    };

    /// <summary>QuestStatus <c>{kind:'none'}</c> (:no-active-quest).</summary>
    public static JsonObject QuestNone() => new() { ["kind"] = "none" };

    /// <summary>QuestStatus <c>{kind:'waiting', name}</c> (:quest-waiting).</summary>
    public static JsonObject QuestWaiting(string name) => new() { ["kind"] = "waiting", ["name"] = name };

    /// <summary>QuestStatus <c>{kind:'active', ...}</c>: the running quest line, clock formatted once (R21).</summary>
    public static JsonObject QuestActive(string slug, int others, string elapsed, bool recording, string? ghostTarget, string? ghostGap) => new()
    {
        ["kind"] = "active",
        ["slug"] = slug,
        ["others"] = others,
        ["elapsed"] = elapsed,
        ["recording"] = recording,
        ["ghost"] = ghostTarget is null ? null : new JsonObject { ["target"] = ghostTarget, ["gap"] = ghostGap },
    };
}
