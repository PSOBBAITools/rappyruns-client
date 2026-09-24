using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RappyRuns.App.Host;

namespace RappyRuns.App;

/// <summary>
/// The main window: a bare WinForms frame around WebView2. Everything visible
/// is the Svelte UI; the frame only hosts it and relays messages.
/// </summary>
internal sealed class MainForm : Form
{
    private const string Title = "Rappy Runs Client";

    private readonly CommandLine _options;
    private readonly WebView2 _webView;
    // RAPPYRUNS_UI_DEV_URL (e.g. http://localhost:5173/) points the window at
    // the Vite dev server for hot reload instead of the embedded build.
    private readonly Uri? _devUrl;

    public MainForm(CommandLine options)
    {
        _options = options;
        _devUrl = Uri.TryCreate(Environment.GetEnvironmentVariable("RAPPYRUNS_UI_DEV_URL"), UriKind.Absolute, out var dev) ? dev : null;

        Text = Title;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 680);
        MinimumSize = new Size(640, 420);
        if (options.Minimized) WindowState = FormWindowState.Minimized;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Transparent,
            CreationProperties = new CoreWebView2CreationProperties
            {
                // Next to the config, not next to the exe: the install folder
                // may be read-only and the updater would carry the cache along.
                UserDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ephinea-ta-client", "WebView2"),
            },
        };
        Controls.Add(_webView);
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
            MessageBox.Show(this,
                "The window could not be created (WebView2 failed to start).\n画面を作成できませんでした (WebView2 の起動に失敗)。\n\n" + ex.Message,
                Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _webView.CoreWebView2;
        var developer = _options.Debug || _devUrl is not null;
        core.Settings.AreDevToolsEnabled = developer;
        core.Settings.AreDefaultContextMenusEnabled = developer;
        core.Settings.AreBrowserAcceleratorKeysEnabled = developer;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;

        core.AddWebResourceRequestedFilter(UiAssets.Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServeAsset;
        core.NavigationStarting += KeepNavigationInApp;
        core.NewWindowRequested += OpenNewWindowsExternally;

        var ipc = new IpcHost(core, this, IsAppSource);
        _ = new AppService(ipc, _options);

        core.Navigate(_devUrl?.ToString() ?? UiAssets.Origin + "/index.html");
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
        OpenExternally(e.Uri);
    }

    private void OpenNewWindowsExternally(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    internal static void OpenExternally(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
