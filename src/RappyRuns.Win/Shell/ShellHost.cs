using System.Diagnostics;
using RappyRuns.Core.I18n;

namespace RappyRuns.Win.Shell;

/// <summary>The shell operations the rest of the app calls.</summary>
public interface IShellHost
{
    /// <summary>Un-hide, restore and raise the main window. Any thread.</summary>
    void ShowMainWindow();

    /// <summary>Quit the whole app (core §2.6). Any thread; does not return.</summary>
    void Quit();

    /// <summary>The UI language changed: refresh the tray tooltip (menu labels are read per popup).</summary>
    void OnLanguageChanged();

    /// <summary>Balloon/toast from any thread (<c>notify-user</c>, recording.lisp:1009). Silent before the tray is up.</summary>
    void Notify(string title, string text, NotifyKind kind = NotifyKind.Warning, string? url = null);
}

/// <summary>What the × button should do (<c>client-confirm-destroy</c>, gui.lisp:544).</summary>
public enum CloseDecision
{
    /// <summary>Let the window close (a quit is already under way).</summary>
    Allow,

    /// <summary>Cancel the close and hide the window; the app keeps running in the tray.</summary>
    HideToTray,

    /// <summary>Cancel the close and run the full quit path.</summary>
    Quit,
}

/// <summary>Hooks the app gives <see cref="ShellHost"/>.</summary>
public sealed class ShellHostOptions
{
    /// <summary>The current main window, read at call time (null before it exists).</summary>
    public required Func<Form?> MainWindow { get; init; }

    /// <summary>The current UI language, read at call time.</summary>
    public required Func<Language> Language { get; init; }

    /// <summary>
    /// Quit step 2 (core §2.6): set the app's stop flags and join the poll
    /// thread for up to 10 s so the recorder unwinds. Called on whichever
    /// thread quits (often the tray thread); must not wait on the UI thread
    /// with Invoke.
    /// </summary>
    public Action PrepareQuit { get; init; } = () => { };

    /// <summary>Opens a balloon's URL; defaults to the shell's browser.</summary>
    public Action<string>? OpenUrl { get; init; }

    /// <summary>Diagnostics sink for the errors the Lisp code swallowed.</summary>
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>The unconditional terminator; ExitProcess by default. Tests replace it.</summary>
    public Action<int>? Exit { get; init; }

    /// <summary>Tray options override (tests: private class name, no icon).</summary>
    public string TrayClassName { get; init; } = SingleInstance.TrayClassName;

    /// <summary>False keeps the hidden window but shows no icon (tests).</summary>
    public bool ShowTrayIcon { get; init; } = true;
}

/// <summary>
/// The resident-app shell: tray icon, balloons, the "show yourself" request
/// from a second instance, close-to-tray and the quit path. Wraps
/// <see cref="TrayIcon"/> and marshals its tray-thread callbacks onto the
/// main window's UI thread.
/// </summary>
public sealed class ShellHost : IShellHost, IDisposable
{
    private readonly ShellHostOptions _options;
    private readonly TrayIcon _tray;
    private int _quitting;

    public ShellHost(ShellHostOptions options)
    {
        _options = options;
        _tray = new TrayIcon(new TrayIconOptions
        {
            ClassName = options.TrayClassName,
            ShowIcon = options.ShowTrayIcon,
            Tooltip = () => Tr("tray-tooltip"),
            MenuLabels = () => (Tr("tray-show"), Tr("tray-quit")),
            ShowRequested = ShowMainWindow,
            QuitRequested = Quit,
            UrlClicked = OpenUrl,
            Log = options.Log,
        });
    }

    /// <summary>True once <see cref="Quit"/> has begun (<c>*really-quitting*</c>).</summary>
    public bool IsQuitting => Volatile.Read(ref _quitting) != 0;

    /// <summary>The tray, for diagnostics and tests.</summary>
    public TrayIcon Tray => _tray;

    /// <summary>
    /// Bring up the tray thread and its window (core §2.1 step 12, after the
    /// main window exists). From here on a second instance can reach us.
    /// Never throws; false means the app runs without a tray.
    /// </summary>
    public bool Start() => _tray.Start();

    /// <inheritdoc />
    public void ShowMainWindow()
    {
        var form = _options.MainWindow();
        if (form is null || form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            // BeginInvoke, never Invoke: the tray thread must not block on a
            // UI thread that may itself be waiting (e.g. in PrepareQuit).
            form.BeginInvoke(() => Restore(form));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window went away between the check and the post.
        }
    }

    /// <summary>
    /// UI thread: show, un-minimize and activate (<c>tray-show-main-window</c>:
    /// display state :normal + raise).
    /// </summary>
    public static void Restore(Form form)
    {
        if (form.IsDisposed) return;
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        NativeMethods.SetForegroundWindow(form.Handle);
    }

    /// <summary>
    /// <c>quit-app</c> (main.lisp:419): flag, stop the poll loop (≤10 s, in
    /// <see cref="ShellHostOptions.PrepareQuit"/>), drop the tray icon, then
    /// ExitProcess(0). Not Application.Exit: like LW:QUIT from the tray thread
    /// (5872e4c, ui-shell R5), a cooperative shutdown can leave the process
    /// alive; config and queue are saved on every change so nothing is lost.
    /// Re-entrant calls (Quit clicked twice) return immediately.
    /// </summary>
    public void Quit()
    {
        if (Interlocked.Exchange(ref _quitting, 1) != 0) return;
        try { _options.PrepareQuit(); }
        catch (Exception ex) { _options.Log($"quit: prepare failed: {ex.Message}"); }
        try { _tray.RemoveIconNow(); }
        catch (Exception ex) { _options.Log($"quit: tray removal failed: {ex.Message}"); }
        (_options.Exit ?? ExitProcess)(0);
    }

    /// <summary>The default <see cref="ShellHostOptions.Exit"/>: ExitProcess, no unwinding.</summary>
    public static void ExitProcess(int code) => NativeMethods.ExitProcess((uint)code);

    /// <inheritdoc />
    public void OnLanguageChanged() => _tray.RefreshTooltip();

    /// <inheritdoc />
    public void Notify(string title, string text, NotifyKind kind = NotifyKind.Warning, string? url = null) =>
        _tray.Notify(title, text, kind, url);

    /// <summary>
    /// The × button rule (<c>client-confirm-destroy</c>, gui.lisp:544): a quit
    /// in progress closes; close-to-tray hides; otherwise it is a real quit.
    /// </summary>
    /// <remarks>
    /// Addition: a Windows logoff/shutdown or Task Manager close is never
    /// vetoed into the tray (that would block logoff); it takes the quit path
    /// so the recorder still unwinds. CAPI handled this below the Lisp code.
    /// </remarks>
    public static CloseDecision DecideClose(bool reallyQuitting, bool closeToTray, CloseReason reason)
    {
        if (reallyQuitting) return CloseDecision.Allow;
        if (reason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return CloseDecision.Quit;
        return closeToTray ? CloseDecision.HideToTray : CloseDecision.Quit;
    }

    /// <summary>
    /// Convenience for MainForm.OnFormClosing: applies <see cref="DecideClose"/>.
    /// </summary>
    public void HandleFormClosing(Form form, FormClosingEventArgs e, bool closeToTray)
    {
        switch (DecideClose(IsQuitting, closeToTray, e.CloseReason))
        {
            case CloseDecision.Allow:
                return;
            case CloseDecision.HideToTray:
                e.Cancel = true;
                form.Hide();
                return;
            case CloseDecision.Quit:
                // Quit ends the process; if it returns (tests), let the close proceed.
                Quit();
                return;
        }
    }

    /// <summary>Stops the tray thread (removes the icon). Not part of the quit path.</summary>
    public void Dispose() => _tray.Dispose();

    private string Tr(string key) => Strings.Default.Tr(_options.Language(), key);

    private void OpenUrl(string url)
    {
        if (_options.OpenUrl is { } open)
        {
            open(url);
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            ShowMainWindow();
            return;
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}
