using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using RappyRuns.Core.Media;
using RappyRuns.Host;

namespace RappyRuns.App.Host;

/// <summary>
/// JSON messages between the host and the web UI (ui/src/lib/ipc.ts is the
/// other end; keep the two in step, docs/ipc.md is the catalog).
/// <code>
/// UI -> host   {"kind":"request","id":7,"method":"app.hello","params":{...}}
/// host -> UI   {"kind":"response","id":7,"ok":true,"result":...}
///              {"kind":"response","id":7,"ok":false,"error":"..."}
/// host -> UI   {"kind":"event","name":"state","data":...}
/// </code>
/// Handlers run on the UI thread (WebMessageReceived is raised there) and may
/// await; <see cref="Emit"/> can be called from any thread and never blocks
/// (BeginInvoke: the poll thread must never wait on the UI thread).
/// </summary>
internal sealed class IpcHost : IIpcRegistry, IUiSink
{
    private readonly CoreWebView2 _webView;
    private readonly Control _uiThread;
    private readonly Func<string, bool> _trustedSource;
    private readonly Dictionary<string, IpcHandler> _handlers = new(StringComparer.Ordinal);

    public IpcHost(CoreWebView2 webView, Control uiThread, Func<string, bool> trustedSource)
    {
        _webView = webView;
        _uiThread = uiThread;
        _trustedSource = trustedSource;
        _webView.WebMessageReceived += OnMessage;
    }

    public void Register(string method, IpcHandler handler) => _handlers.Add(method, handler);

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
            request = JsonSerializer.Deserialize<Request>(e.WebMessageAsJson, IpcJson.Options);
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
            RecordingLog.Write($"ipc {request.Method} failed: {ex}");
            Post(new { kind = "response", id = request.Id, ok = false, error = ex.Message });
        }
    }

    private void Post(object message)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(message, IpcJson.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException)
        {
            RecordingLog.Write("ipc: could not serialize a message: " + ex.Message);
            return;
        }
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
        try
        {
            _webView.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The WebView2 process went away (crash, shutdown).
        }
    }

    private sealed record Request(string? Kind, long Id, string? Method, JsonElement Params);
}
