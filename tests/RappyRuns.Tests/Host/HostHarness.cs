using System.Net;
using System.Text;
using System.Text.Json;
using RappyRuns.Core.Api;
using RappyRuns.Core.Config;
using RappyRuns.Core.Game;
using RappyRuns.Core.Media;
using RappyRuns.Core.Update;
using RappyRuns.Host;
using RappyRuns.Win.Media;
using RappyRuns.Win.PinShare;

namespace RappyRuns.Tests.Host;

/// <summary>An <see cref="IHostServices"/> that touches nothing on the machine (S32).</summary>
internal sealed class FakeHostServices : IHostServices
{
    public List<string> Lines { get; } = [];

    public FakeAutostart AutostartSetting { get; } = new();

    public MachineInfo Machine { get; } = new(16L << 30, 8, "Windows", "10.0");

    public void Log(string line)
    {
        lock (Lines) Lines.Add(line);
    }

    public IAutostartSetting Autostart(string valueName) => AutostartSetting;

    public IGameConnector Connector() => new NoGame();

    public ICaptureBackend CaptureBackend(Func<bool> wgcDisabled, GdigrabProbe gdigrab) => new Media.MockBackend();

    public bool ShowTrayIcon => false;

    /// <summary>Exit codes the quit sequence asked for (the test runner keeps running).</summary>
    public List<int> Exits { get; } = [];

    public void Exit(int code)
    {
        lock (Exits) Exits.Add(code);
    }

    public Task<RelaySocket> PinShareConnect(string url, CancellationToken cancellationToken) =>
        Task.FromException<RelaySocket>(new IOException("no relay server in tests"));

    public AddonInstaller PinShareInstaller() => new([], Log);

    internal sealed class FakeAutostart : IAutostartSetting
    {
        public bool Enabled { get; set; }

        public bool IsEnabled() => Enabled;

        public bool SetEnabled(bool enable)
        {
            Enabled = enable;
            return true;
        }
    }

    private sealed class NoGame : IGameConnector
    {
        public IProcessMemoryReader? TryAttach() => null;

        public PsobbRejection? Rejection => null;
    }
}

/// <summary>
/// An HTTP handler whose answers the test releases one by one: each request
/// waits on its own <see cref="TaskCompletionSource"/>, so a test picks the
/// order in which concurrent calls complete.
/// </summary>
internal sealed class GatedHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(string Url, string? Auth, TaskCompletionSource<(int Status, string Body)> Answer)> _pending = [];
    private readonly Func<string, string?, (int Status, string Body)?> _immediate;

    /// <param name="immediate">Answers a request at once (url, Authorization) or returns null to hold it.</param>
    public GatedHandler(Func<string, string?, (int Status, string Body)?>? immediate = null) =>
        _immediate = immediate ?? ((_, _) => null);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        var auth = request.Headers.Authorization?.ToString();
        var answer = _immediate(url, auth);
        if (answer is null)
        {
            var tcs = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pending.Add((url, auth, tcs));
                Monitor.PulseAll(_gate);
            }
            try
            {
                answer = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Abandoned: Take must never hand out its answer handle.
                lock (_gate) _pending.RemoveAll(p => p.Answer == tcs);
                throw;
            }
        }
        var (status, body) = answer.Value;
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
            RequestMessage = request,
        };
    }

    /// <summary>Waits until a held request matches, then removes and returns its answer handle.</summary>
    public TaskCompletionSource<(int Status, string Body)> Take(Func<string, string?, bool> match, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_gate)
        {
            while (true)
            {
                var i = _pending.FindIndex(p => match(p.Url, p.Auth));
                if (i >= 0)
                {
                    var found = _pending[i];
                    _pending.RemoveAt(i);
                    return found.Answer;
                }
                var left = deadline - Environment.TickCount64;
                if (left <= 0) throw new TimeoutException("no matching request arrived");
                Monitor.Wait(_gate, (int)left);
            }
        }
    }
}

/// <summary>An <see cref="IUiSink"/> that records every event in emission order.</summary>
internal sealed class RecordingSink : IUiSink
{
    private readonly List<(string Name, string Json)> _events = [];

    public void Emit(string name, object? data)
    {
        var json = data is null ? "null" : JsonSerializer.Serialize(data, data.GetType(), IpcJson.Options);
        lock (_events) _events.Add((name, json));
    }

    /// <summary>A replied IPC response, recorded in the same stream as the events.</summary>
    public void Reply(string method, object? data) => Emit("reply:" + method, data);

    public List<(string Name, string Json)> Events
    {
        get { lock (_events) return [.. _events]; }
    }
}

/// <summary>A <see cref="ClientHost"/> in a temp folder, built from fakes, with its IPC recorded.</summary>
internal sealed class HostHarness : IDisposable
{
    private readonly TempDir _dir = new("rr-host");
    private readonly Dictionary<string, IpcHandler> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IpcOrderedHandler> _ordered = new(StringComparer.Ordinal);

    public HostHarness(GatedHandler? http = null, Action<ConfigStore>? configure = null)
    {
        Http = http ?? new GatedHandler((_, _) => (404, ""));
        Config = ConfigStore.Open(_dir.Path);
        Config.ServerUrl = "https://s.example";
        configure?.Invoke(Config);
        var transport = _transport = new HttpTransport(Http);
        Host = new ClientHost(Config, new HostOptions { ConfigDir = _dir.Path, Isolated = true, ExeDir = _dir.Path },
            transport, new SelfUpdater(transport, () => "", "1.0.0", _dir.Path), services: Services);
        Host.AttachIpc(new Registry(this), Sink);
    }

    public string Dir => _dir.Path;

    public GatedHandler Http { get; }

    public ConfigStore Config { get; }

    public FakeHostServices Services { get; } = new();

    public RecordingSink Sink { get; } = new();

    public ClientHost Host { get; }

    /// <summary>Calls an ordered IPC method (app.hello, app.setLanguage); its reply joins the event stream.</summary>
    public void CallOrdered(string method, JsonElement parameters = default) =>
        _ordered[method](parameters, reply => Sink.Reply(method, reply));

    /// <summary>Calls a plain IPC method.</summary>
    public Task<object?> Call(string method, JsonElement parameters = default) => _methods[method](parameters);

    public void Dispose()
    {
        Host.Dispose();
        _transport.Dispose(); // and the handler with it
        _dir.Dispose();
    }

    private readonly HttpTransport _transport;

    private sealed class Registry(HostHarness harness) : IIpcRegistry
    {
        public void Register(string method, IpcHandler handler) => harness._methods[method] = handler;

        public void RegisterOrdered(string method, IpcOrderedHandler handler) => harness._ordered[method] = handler;
    }
}
