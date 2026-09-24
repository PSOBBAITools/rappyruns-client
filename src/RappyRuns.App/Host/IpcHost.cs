using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace RappyRuns.App.Host;

/// <summary>
/// JSON messages between the host and the web UI (ui/src/lib/ipc.ts is the
/// other end; keep the two in step).
/// <code>
/// UI -> host   {"kind":"request","id":7,"method":"app.hello","params":{...}}
/// host -> UI   {"kind":"response","id":7,"ok":true,"result":...}
///              {"kind":"response","id":7,"ok":false,"error":"..."}
/// host -> UI   {"kind":"event","name":"state","data":...}
/// </code>
/// Handlers run on the UI thread (WebMessageReceived is raised there) and may
/// await; <see cref="Emit"/> can be called from any thread.
/// </summary>
internal sealed class IpcHost
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public delegate Task<object?> Handler(JsonElement parameters);

    private readonly CoreWebView2 _webView;
    private readonly Control _uiThread;
    private readonly Func<string, bool> _trustedSource;
    private readonly Dictionary<string, Handler> _handlers = new(StringComparer.Ordinal);

    public IpcHost(CoreWebView2 webView, Control uiThread, Func<string, bool> trustedSource)
    {
        _webView = webView;
        _uiThread = uiThread;
        _trustedSource = trustedSource;
        _webView.WebMessageReceived += OnMessage;
    }

    public void Register(string method, Handler handler) => _handlers.Add(method, handler);

    public void Register(string method, Func<JsonElement, object?> handler) =>
        _handlers.Add(method, p => Task.FromResult(handler(p)));

    /// <summary>Pushes an event to the UI. Safe from any thread; dropped once the window is gone.</summary>
    public void Emit(string name, object? data) =>
        Post(new { kind = "event", name, data });

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only our own page may drive the host - never a site the view was
        // somehow navigated to.
        if (!_trustedSource(e.Source)) return;

        Request? request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(e.WebMessageAsJson, Json);
        }
        catch (JsonException)
        {
            return;
        }
        if (request is null || request.Kind != "request" || request.Method is null) return;

        if (!_handlers.TryGetValue(request.Method, out var handler))
        {
            Post(new { kind = "response", id = request.Id, ok = false, error = $"unknown method {request.Method}" });
            return;
        }

        try
        {
            var result = await handler(request.Params);
            Post(new { kind = "response", id = request.Id, ok = true, result });
        }
        catch (Exception ex)
        {
            Post(new { kind = "response", id = request.Id, ok = false, error = ex.Message });
        }
    }

    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message, Json);
        if (_uiThread.IsDisposed) return;
        if (_uiThread.InvokeRequired)
        {
            try
            {
                _uiThread.BeginInvoke(() => PostOnUiThread(json));
            }
            catch (InvalidOperationException)
            {
                // Window handle already destroyed while shutting down.
            }
        }
        else
        {
            PostOnUiThread(json);
        }
    }

    private void PostOnUiThread(string json)
    {
        if (_uiThread.IsDisposed) return;
        _webView.PostWebMessageAsJson(json);
    }

    private sealed record Request(string? Kind, long Id, string? Method, JsonElement Params);
}
