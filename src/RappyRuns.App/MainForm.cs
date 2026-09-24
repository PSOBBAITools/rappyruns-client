using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RappyRuns.App.Host;
using RappyRuns.Core.Media;
using RappyRuns.Host;

namespace RappyRuns.App;

/// <summary>
/// The main window: a bare WinForms frame around WebView2. Everything visible
/// is the Svelte UI; the frame only hosts it, relays messages and follows the
/// shell rules (close-to-tray, start minimized, the live window title).
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ClientHost _host;
    private readonly bool _developer;
    private readonly bool _startMinimized;
    private readonly WebView2 _webView;
    // RAPPYRUNS_UI_DEV_URL (e.g. http://localhost:5173/) points the window at
    // the Vite dev server for hot reload instead of the embedded build.
    private readonly Uri? _devUrl;

    public MainForm(ClientHost host, bool startMinimized)
    {
        _host = host;
        _startMinimized = startMinimized;
        _devUrl = Uri.TryCreate(Environment.GetEnvironmentVariable("RAPPYRUNS_UI_DEV_URL"), UriKind.Absolute, out var dev) ? dev : null;
        _developer = host.Config.DebugMode || _devUrl is not null;

        Text = ClientHost.WindowTitle;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 680);
        MinimumSize = new Size(640, 420);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Transparent,
            CreationProperties = new CoreWebView2CreationProperties
            {
                // Next to the config, not next to the exe: the install folder
                // may be read-only and the updater would carry the cache along.
                UserDataFolder = host.Options.WebViewDataDir,
            },
        };
        Controls.Add(_webView);
        host.AttachWindow(() => IsDisposed ? null : this, SetTitle);
    }

    /// <summary>set-window-title: the caller dedupes; any thread, never blocks.</summary>
    private void SetTitle(string title)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() => Text = title);
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The window is realized: tray, relay, poll thread, server/token checks.
        _host.Start();
        // Launch straight to the tray (autostart --minimized, or the setting):
        // shown once, then hidden, after the tray exists (ui-shell §4.1 step 14).
        if (_startMinimized) Hide();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await _webView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            RecordingLog.Write("WebView2 failed to start: " + ex);
            MessageBox.Show(this,
                "The window could not be created (WebView2 failed to start).\n画面を作成できませんでした (WebView2 の起動に失敗)。\n\n" + ex.Message,
                ClientHost.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var core = _webView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = _developer;
        core.Settings.AreDefaultContextMenusEnabled = _developer;
        core.Settings.AreBrowserAcceleratorKeysEnabled = _developer;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;

        core.AddWebResourceRequestedFilter(UiAssets.Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServeAsset;
        core.NavigationStarting += KeepNavigationInApp;
        core.NewWindowRequested += OpenNewWindowsExternally;
        core.ProcessFailed += (_, args) => RecordingLog.Write($"WebView2 process failed: {args.ProcessFailedKind}");

        var ipc = new IpcHost(core, this, IsAppSource);
        _host.AttachIpc(ipc, ipc);

        core.Navigate(_devUrl?.ToString() ?? UiAssets.Origin + "/index.html");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // client-confirm-destroy: close-to-tray hides; otherwise a real quit
        // (which ends the process through ExitProcess).
        _host.Shell.HandleFormClosing(this, e, _host.Config.CloseToTray);
        base.OnFormClosing(e);
    }

    private bool IsAppSource(string source) =>
        IsAppUri(Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri : null);

    private bool IsAppUri(Uri? uri) =>
        uri is not null &&
        (string.Equals(uri.GetLeftPart(UriPartial.Authority), UiAssets.Origin, StringComparison.OrdinalIgnoreCase) ||
         (_devUrl is not null && string.Equals(uri.GetLeftPart(UriPartial.Authority), _devUrl.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)));

    private void ServeAsset(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var env = _webView.CoreWebView2.Environment;
        var stream = UiAssets.Open(uri.AbsolutePath);
        e.Response = stream is null
            ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
            : env.CreateWebResourceResponse(stream, 200, "OK",
                $"Content-Type: {UiAssets.ContentType(uri.AbsolutePath)}\r\nCache-Control: no-store");
    }

    // The window only ever shows the app. Links to anything else (the site,
    // GitHub, Discord) open in the default browser.
    private void KeepNavigationInApp(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAppUri(Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri : null)) return;
        e.Cancel = true;
        ClientHost.OpenExternal(e.Uri);
    }

    private void OpenNewWindowsExternally(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        ClientHost.OpenExternal(e.Uri);
    }
}
