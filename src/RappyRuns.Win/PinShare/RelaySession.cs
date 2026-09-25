using System.Collections.Concurrent;
using System.Diagnostics;
using RappyRuns.Core.PinShare;

namespace RappyRuns.Win.PinShare;

/// <summary>
/// One relay session's loop (Lisp <c>pinshare-relay-loop</c>,
/// pinshare-win32.lisp:312): shuttles between the addon's exchange files
/// and the relay server until the wanted session changes. Ticks every
/// 50 ms and never blocks on the network: connecting and receiving happen
/// on a connection task that reports through a queue, so in.txt keeps its
/// once-a-second heartbeat even while a connect attempt hangs (the addon
/// calls a 5 s old heartbeat "relay not running").
/// </summary>
internal sealed class RelaySession
{
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);
    public const double MaxBackoffSeconds = 30;

    /// <summary>A connection that lasted at least this long resets the backoff when it drops; a shorter one counts as a failed attempt.</summary>
    public const double StableConnectionSeconds = 30;

    private abstract record Event;
    private sealed record Opened(RelaySocket Socket) : Event;
    private sealed record Failed(string Text) : Event;
    private sealed record Received(string Text) : Event;
    private sealed record Closed : Event;

    /// <summary>
    /// Shared between the session and its connection task so a session can
    /// end while a connect is still in flight without leaking the socket
    /// that connect is about to produce.
    /// </summary>
    private sealed class Link
    {
        public readonly object Lock = new();
        public RelaySocket? Socket;
        public bool Cancelled;

        public void Cancel()
        {
            RelaySocket? socket;
            lock (Lock)
            {
                Cancelled = true;
                socket = Socket;
                Socket = null;
            }
            socket?.Close();
        }
    }

    private readonly PinShareSupervisor _owner;
    private readonly PinShareRelay _relay;
    private readonly PinShareWanted _wanted;
    private readonly string _outPath;
    private readonly string _inPath;
    private readonly string _tmpPath;
    private readonly BlockingCollection<Event> _events = new();
    private readonly Link _link = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private RelaySocket? _socket;
    private bool _connecting;
    private double _retryAt;
    private double _backoff = 1;
    private double _openedAt;
    private double _lastWrite = double.NegativeInfinity;
    private bool _setDrawn;
    private PinSet? _drawnSet;

    public RelaySession(PinShareSupervisor owner, PinShareRelay relay, PinShareWanted wanted, string outPath, string inPath, string tmpPath)
    {
        _owner = owner;
        _relay = relay;
        _wanted = wanted;
        _outPath = outPath;
        _inPath = inPath;
        _tmpPath = tmpPath;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private bool LocalOnly => _wanted.Channel == "";

    public void Run()
    {
        try
        {
            while (_owner.SessionCurrent(_wanted))
            {
                if (_socket is null && !_connecting && !LocalOnly && Now >= _retryAt)
                {
                    _connecting = true;
                    Status("connecting", _wanted.ServerUrl, new PinShareStatus(PinShareStatusKind.Connecting));
                    StartConnection();
                }
                // The timed read is the tick; a server message wakes it early.
                if (_events.TryTake(out var first, Tick))
                {
                    Handle(first);
                    while (_events.TryTake(out var more)) Handle(more);
                }
                // What Save files: the server's latest lists, only while live.
                _owner.ChannelItems = _socket is not null ? new ChannelItems(_relay.Pins, _relay.Arrows) : null;
                // A set fetched (or dropped) mid-session redraws at once
                // rather than on the next heartbeat.
                var set = _owner.CurrentPinSet;
                if (!_setDrawn || !ReferenceEquals(set, _drawnSet))
                {
                    _setDrawn = true;
                    _drawnSet = set;
                    _relay.Dirty = true;
                }
                var messages = _relay.Consume(Outbox.Parse(ExchangeFiles.ReadText(_outPath)), _socket is not null);
                if (_socket is not null && messages.Count > 0) Send(messages);
                // Local mode's line follows the drawn set and the addon's version.
                if (LocalOnly) LocalStatus(set);
                // The addon's first command to this session flips the
                // "waiting for the addon" line; its name without a current
                // version turns it into "addon outdated", and a Reload back (S21).
                if (_socket is not null && _owner.Status.Kind is PinShareStatusKind.ConnectedNoAddon or PinShareStatusKind.Connected or PinShareStatusKind.AddonOutdated
                    && _relay.AddonSeen)
                    ConnectedStatus();
                if (_relay.Dirty || Now - _lastWrite >= 1)
                {
                    if (WriteInbox(set)) _lastWrite = Now;
                }
            }
        }
        finally
        {
            _owner.ChannelItems = null;
            _link.Cancel();
            _socket?.Close();
            // Leave in.txt without items: its heartbeat stays fresh for a few
            // seconds, and the addon would keep drawing the last list - the
            // previous quest's set, most visibly, after leaving it.
            try
            {
                _relay.ClearState();
                WriteInbox(null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private bool WriteInbox(PinSet? set)
    {
        var text = Inbox.Render(_relay, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), set);
        if (!ExchangeFiles.ReplaceInbox(_inPath, _tmpPath, text)) return false;
        _relay.Dirty = false;
        return true;
    }

    private void Status(string status, string message, PinShareStatus gui)
    {
        if (_relay.SetStatus(status, message)) _owner.Log($"pin share: {status} {message}");
        _owner.Status = gui;
    }

    private void ConnectedStatus() =>
        Status("connected", "", _relay.AddonOutdated
            ? new PinShareStatus(PinShareStatusKind.AddonOutdated)
            : _relay.AddonSeen
                ? new PinShareStatus(PinShareStatusKind.Connected, _relay.Channel, _relay.Members.Count)
                // Connected, yet the game has not loaded the script (fresh install: it needs a Reload).
                : new PinShareStatus(PinShareStatusKind.ConnectedNoAddon));

    private void LocalStatus(PinSet? set) =>
        Status("local", "", _relay.AddonOutdated
            ? new PinShareStatus(PinShareStatusKind.AddonOutdated)
            : new PinShareStatus(PinShareStatusKind.LocalOnly, set?.DisplayName));

    private void BackOff()
    {
        _retryAt = Now + _backoff;
        _backoff = Math.Min(2 * _backoff, MaxBackoffSeconds);
    }

    private void Send(List<string> messages)
    {
        try
        {
            foreach (var message in messages) _socket!.SendText(message);
        }
        catch (Exception)
        {
            // The receive side sees the same failure and posts Closed, which
            // does the state change.
            _socket!.Close();
        }
    }

    private void Handle(Event e)
    {
        switch (e)
        {
            case Opened opened:
                _socket = opened.Socket;
                _connecting = false;
                _openedAt = Now;
                _relay.ClearState();
                ConnectedStatus();
                Send(_relay.HelloMessages());
                break;
            case Failed failed:
            {
                _connecting = false;
                BackOff();
                var text = $"connect failed: {failed.Text}";
                Status("error", text, new PinShareStatus(PinShareStatusKind.Error, text));
                break;
            }
            case Received received:
            {
                var serverError = _relay.NoteMessage(received.Text);
                if (serverError is not null)
                {
                    // in.txt stays "connected" (it is); the Settings line
                    // shows the refusal until the next state snapshot, so a
                    // rejected command is not a silent no-op.
                    _owner.Log($"pin share: server says {serverError}");
                    _owner.Status = new PinShareStatus(PinShareStatusKind.Error, $"server: {serverError}");
                }
                else if (_socket is not null)
                {
                    ConnectedStatus();
                }
                break;
            }
            case Closed:
                if (_socket is not null)
                {
                    _socket.Close();
                    _socket = null;
                }
                lock (_link.Lock) _link.Socket = null;
                // Only a connection that held earns the quick retry. A server
                // that accepts and drops (crash loop, a proxy that upgrades
                // then resets) backs off like a failed connect - every
                // resident client hitting it once a second would keep it down.
                if (Now - _openedAt >= StableConnectionSeconds)
                {
                    _backoff = 1;
                    _retryAt = Now + 1;
                }
                else
                {
                    BackOff();
                }
                _relay.ClearState();
                Status("error", "disconnected from the server", new PinShareStatus(PinShareStatusKind.Error, "disconnected from the server"));
                break;
        }
    }

    /// <summary>
    /// Connects on a pool task and feeds the queue: exactly one Opened or
    /// Failed, then Received per server message, then one Closed - however
    /// the receive loop ends, or the session would sit on a dead socket
    /// calling itself connected and never reconnect.
    /// </summary>
    private void StartConnection()
    {
        var url = _wanted.ServerUrl;
        _ = Task.Run(async () =>
        {
            RelaySocket socket;
            try
            {
                socket = await _owner.Connect(url, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _events.Add(new Failed(e.Message));
                return;
            }
            bool adopted;
            lock (_link.Lock)
            {
                adopted = !_link.Cancelled;
                if (adopted) _link.Socket = socket;
            }
            if (!adopted)
            {
                // The session ended while we were connecting.
                socket.Close();
                return;
            }
            _events.Add(new Opened(socket));
            try
            {
                while (await socket.ReceiveAsync().ConfigureAwait(false) is { } message)
                    _events.Add(new Received(message));
            }
            catch (Exception)
            {
                // Fall through to Closed.
            }
            _events.Add(new Closed());
        });
    }
}
