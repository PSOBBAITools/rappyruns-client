using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static RappyRuns.Win.Shell.NativeMethods;

namespace RappyRuns.Win.Shell;

/// <summary>Balloon icon (dwInfoFlags), as <c>tray-notify</c>'s :none/:info/:warning.</summary>
public enum NotifyKind
{
    None,
    Info,
    Warning,
}

/// <summary>What <see cref="TrayIcon"/> needs from its owner. All callbacks run on the tray thread.</summary>
public sealed class TrayIconOptions
{
    /// <summary>Window class; the contract value unless a test needs its own (see <see cref="SingleInstance.TrayClassName"/>).</summary>
    public string ClassName { get; init; } = SingleInstance.TrayClassName;

    /// <summary>Window title (contract, tray-win32.lisp:533).</summary>
    public string WindowName { get; init; } = SingleInstance.TrayWindowName;

    /// <summary>False creates only the hidden window (tests: no icon flickering in the user's tray).</summary>
    public bool ShowIcon { get; init; } = true;

    /// <summary>Exe whose embedded icon (index 0) is shown; IDI_APPLICATION when it has none.</summary>
    public string? IconSource { get; init; } = Environment.ProcessPath;

    /// <summary>Tooltip text, read whenever the icon is (re)added or refreshed.</summary>
    public Func<string> Tooltip { get; init; } = () => "Rappy Runs Client";

    /// <summary>Menu labels (Show, Quit), read each time the menu pops so the current language is used.</summary>
    public Func<(string Show, string Quit)> MenuLabels { get; init; } = () => ("Show", "Quit");

    /// <summary>Left double-click, menu Show, the 0x8002 request, or a click on a URL-less balloon.</summary>
    public Action ShowRequested { get; init; } = () => { };

    /// <summary>Menu Quit.</summary>
    public Action QuitRequested { get; init; } = () => { };

    /// <summary>Click on a balloon that carried a URL.</summary>
    public Action<string> UrlClicked { get; init; } = _ => { };

    /// <summary>Where swallowed errors go (the Lisp code's ignore-errors dropped them silently).</summary>
    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// The notification-area icon and the hidden window that owns it - a port of
/// client/src/tray-win32.lisp.
/// </summary>
/// <remarks>
/// <para>
/// Threading: like the Lisp client (<c>start-tray!</c>, tray-win32.lisp:554)
/// the window lives on its own STA thread named <c>eta-client-tray</c> with a
/// raw GetMessage loop. A WinForms NotifyIcon on the UI thread was rejected
/// because the window must be a plain top-level window of class
/// <c>RappyRunsTrayWindow</c> that FindWindow can see (the single-instance
/// contract, core §2.4) - WinForms names its classes <c>WindowsForms10.*</c> -
/// and because the "show yourself" request and the Quit menu must keep
/// working while the UI thread is busy or blocked. Everything that touches
/// the main window therefore arrives here as a callback on the tray thread,
/// and the owner marshals it (see <see cref="ShellHost"/>).
/// </para>
/// <para>
/// Balloons use Shell_NotifyIcon NIF_INFO exactly as the Lisp client does
/// (<c>tray-notify</c>, tray-win32.lisp:399); Windows 10/11 renders these as
/// toasts attributed to the app, and a click comes back as
/// NIN_BALLOONUSERCLICK. No AUMID/Start-menu shortcut is required.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Our Shell_NotifyIcon callback, WM_APP+1 (tray-win32.lisp:276).</summary>
    public const uint CallbackMessage = WM_APP + 1;

    private const uint IconId = 1;
    private const int MenuShow = 1;
    private const int MenuQuit = 2;

    // One window procedure for the whole process; it finds the instance by
    // HWND. Messages sent during CreateWindowEx arrive before the HWND is
    // known and simply go to DefWindowProc.
    private static readonly ConcurrentDictionary<nint, TrayIcon> s_windows = new();
    private static readonly ConcurrentDictionary<string, bool> s_registeredClasses = new(StringComparer.OrdinalIgnoreCase);
    private static uint s_taskbarCreated;

    private readonly TrayIconOptions _options;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private nint _hwnd;
    private nint _icon;
    private bool _ownsIcon;
    private volatile string? _balloonUrl;

    public TrayIcon(TrayIconOptions options) => _options = options;

    /// <summary>The hidden owner window, or 0 while the tray is down.</summary>
    public nint WindowHandle => _hwnd;

    /// <summary>True while the tray thread is running its message loop.</summary>
    public bool IsRunning => _hwnd != 0;

    /// <summary>
    /// Start the tray thread and wait (up to 5 s) until its window exists, so a
    /// second instance can find it from then on. Idempotent; never throws - a
    /// tray failure must not take the app down (tray-win32.lisp:554).
    /// </summary>
    public bool Start()
    {
        try
        {
            if (_thread is { IsAlive: true }) return _ready.Wait(TimeSpan.FromSeconds(5)) && _hwnd != 0;
            _ready.Reset();
            var thread = new Thread(ThreadMain) { Name = "eta-client-tray", IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            _thread = thread;
            thread.Start();
            return _ready.Wait(TimeSpan.FromSeconds(5)) && _hwnd != 0;
        }
        catch (Exception ex)
        {
            _options.Log($"tray start failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Balloon notification from any thread (tray-win32.lisp:399). Title is cut
    /// to 63 chars, text to 255. <paramref name="url"/> becomes the click
    /// target; null resets it so a click raises the main window instead.
    /// Silent when the tray is not up.
    /// </summary>
    public unsafe void Notify(string title, string text, NotifyKind kind = NotifyKind.Warning, string? url = null)
    {
        var hwnd = _hwnd;
        if (hwnd == 0) return;
        _balloonUrl = url;
        try
        {
            var nid = NewData(hwnd);
            nid.UFlags = NIF_INFO;
            nid.UTimeoutOrVersion = 0;
            nid.DwInfoFlags = kind switch
            {
                NotifyKind.None => NIIF_NONE,
                NotifyKind.Info => NIIF_INFO,
                _ => NIIF_WARNING,
            };
            CopyString(title, nid.SzInfoTitle, 64);
            CopyString(text, nid.SzInfo, 256);
            Shell_NotifyIconW(NIM_MODIFY, &nid);
        }
        catch (Exception ex)
        {
            _options.Log($"tray notify failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-read the tooltip (language switch). The Lisp client left the old
    /// language's tooltip until restart; this refreshes it in place.
    /// </summary>
    public unsafe void RefreshTooltip()
    {
        var hwnd = _hwnd;
        if (hwnd == 0 || !_options.ShowIcon) return;
        try
        {
            var nid = NewData(hwnd);
            nid.UFlags = NIF_TIP;
            CopyString(_options.Tooltip(), nid.SzTip, 128);
            Shell_NotifyIconW(NIM_MODIFY, &nid);
        }
        catch (Exception ex)
        {
            _options.Log($"tray tooltip failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Drop the icon synchronously from any thread (quit path, before
    /// ExitProcess, so no ghost icon lingers - <c>tray-remove-icon-now</c>).
    /// </summary>
    public void RemoveIconNow()
    {
        var hwnd = _hwnd;
        if (hwnd != 0) RemoveIcon(hwnd);
    }

    /// <summary>Close the window (icon removed on WM_DESTROY) and end the thread.</summary>
    public void Dispose()
    {
        var hwnd = _hwnd;
        if (hwnd != 0) PostMessageW(hwnd, WM_CLOSE, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(5));
    }

    // --- Tray thread ----------------------------------------------------------

    private unsafe void ThreadMain()
    {
        try
        {
            if (s_taskbarCreated == 0) s_taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            var instance = GetModuleHandleW(null);
            if (!RegisterClass(_options.ClassName, instance)) return;

            var hwnd = CreateWindowExW(0, _options.ClassName, _options.WindowName, 0,
                0, 0, 0, 0, 0, 0, instance, 0);
            if (hwnd == 0)
            {
                _options.Log($"tray window creation failed ({Marshal.GetLastPInvokeError()})");
                return;
            }
            s_windows[hwnd] = this;
            _hwnd = hwnd;
            if (_options.ShowIcon) AddIcon(hwnd);
            _ready.Set();

            MSG msg;
            // GetMessage returns 0 on WM_QUIT, -1 on error.
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        catch (Exception ex)
        {
            _options.Log($"tray thread error: {ex.Message}");
        }
        finally
        {
            if (_hwnd != 0) s_windows.TryRemove(_hwnd, out _);
            _hwnd = 0;
            if (_ownsIcon && _icon != 0) DestroyIcon(_icon);
            _icon = 0;
            _ready.Set();
        }
    }

    private unsafe bool RegisterClass(string className, nint instance)
    {
        if (s_registeredClasses.ContainsKey(className)) return true;
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                CbSize = (uint)sizeof(WNDCLASSEXW),
                LpfnWndProc = &WndProc,
                HInstance = instance,
                LpszClassName = name,
            };
            if (RegisterClassExW(&wc) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                {
                    _options.Log($"tray class registration failed ({error})");
                    return false;
                }
            }
        }
        s_registeredClasses[className] = true;
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // No exception may escape into user32 (it would tear the process down);
        // this is the Lisp code's ignore-errors around every branch.
        try
        {
            if (s_windows.TryGetValue(hwnd, out var tray) && tray.Handle(hwnd, msg, lParam))
                return 0;
        }
        catch (Exception ex)
        {
            if (s_windows.TryGetValue(hwnd, out var t)) t._options.Log($"tray event failed: {ex.Message}");
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>RappyTrayWndProc (tray-win32.lisp:466). True = handled.</summary>
    private bool Handle(nint hwnd, uint msg, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            // Pre-v4 notify icon (no NIM_SETVERSION): lParam's low word is the event.
            var ev = (uint)(lParam & 0xFFFF);
            if (ev == WM_LBUTTONDBLCLK) Safe(_options.ShowRequested);
            else if (ev == NIN_BALLOONUSERCLICK) BalloonClicked();
            else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) PopupMenu(hwnd);
            return true;
        }
        if (msg == SingleInstance.ShowRequestMessage)
        {
            Safe(_options.ShowRequested);
            return true;
        }
        if (s_taskbarCreated != 0 && msg == s_taskbarCreated)
        {
            // Explorer restarted: the icon is gone, add it again.
            if (_options.ShowIcon) AddIcon(hwnd);
            return true;
        }
        if (msg == WM_DESTROY)
        {
            // Dispose posts WM_CLOSE; DefWindowProc turns it into DestroyWindow.
            if (_options.ShowIcon) RemoveIcon(hwnd);
            PostQuitMessage(0);
            return true;
        }
        return false;
    }

    /// <summary>A toast with a URL opens it; a plain warning raises the window (tray-win32.lisp:354).</summary>
    private void BalloonClicked()
    {
        var url = _balloonUrl;
        if (url is not null) Safe(() => _options.UrlClicked(url));
        else Safe(_options.ShowRequested);
    }

    /// <summary>
    /// Show / Quit menu (tray-win32.lisp:436). SetForegroundWindow before and
    /// WM_NULL after TrackPopupMenu is the documented way to make a tray menu
    /// close when the user clicks elsewhere. Labels are localized; C# strings
    /// go to AppendMenuW as UTF-16, so the Japanese labels that once silently
    /// killed the Lisp menu (560918f, ui-shell R6) are a non-issue here.
    /// </summary>
    private void PopupMenu(nint hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        int cmd;
        try
        {
            var (show, quit) = _options.MenuLabels();
            AppendMenuW(menu, MF_STRING, MenuShow, show);
            AppendMenuW(menu, MF_STRING, MenuQuit, quit);
            SetForegroundWindow(hwnd);
            if (!GetCursorPos(out var pt)) pt = default;
            cmd = TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, hwnd, 0);
            PostMessageW(hwnd, WM_NULL, 0, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
        if (cmd == MenuShow) Safe(_options.ShowRequested);
        else if (cmd == MenuQuit) Safe(_options.QuitRequested);
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { _options.Log($"tray callback failed: {ex.Message}"); }
    }

    // --- Shell_NotifyIcon -----------------------------------------------------

    private static unsafe NOTIFYICONDATAW NewData(nint hwnd) => new()
    {
        CbSize = (uint)sizeof(NOTIFYICONDATAW),
        HWnd = hwnd,
        UID = IconId,
    };

    private unsafe void AddIcon(nint hwnd)
    {
        if (_icon == 0) LoadIcon();
        var nid = NewData(hwnd);
        nid.UFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.UCallbackMessage = CallbackMessage;
        nid.HIcon = _icon;
        CopyString(_options.Tooltip(), nid.SzTip, 128);
        if (!Shell_NotifyIconW(NIM_ADD, &nid))
            _options.Log("tray icon could not be added (Explorer not ready?)");
    }

    private static unsafe void RemoveIcon(nint hwnd)
    {
        var nid = NewData(hwnd);
        Shell_NotifyIconW(NIM_DELETE, &nid);
    }

    /// <summary>
    /// The exe's own icon, else IDI_APPLICATION (<c>tray-load-icon</c>). The
    /// path is passed as UTF-16, so a Japanese install folder works (R6).
    /// </summary>
    private void LoadIcon()
    {
        var source = _options.IconSource;
        if (!string.IsNullOrEmpty(source))
        {
            // ExtractIcon returns 1 for "not an exe/ico" and 0 for "no icon".
            var icon = ExtractIconW(GetModuleHandleW(null), source, 0);
            if (icon > 1)
            {
                _icon = icon;
                _ownsIcon = true;
                return;
            }
        }
        _icon = LoadIconW(0, IDI_APPLICATION);
        _ownsIcon = false;
    }

    /// <summary>
    /// Copy <paramref name="value"/> into a fixed WCHAR[<paramref name="capacity"/>],
    /// truncated and NUL-terminated (<c>copy-tray-wstring</c>). Unlike the Lisp
    /// copy it never splits a surrogate pair.
    /// </summary>
    internal static unsafe void CopyString(string value, char* buffer, int capacity)
    {
        var text = Truncate(value, capacity - 1);
        for (var i = 0; i < text.Length; i++) buffer[i] = text[i];
        buffer[text.Length] = '\0';
    }

    /// <summary>At most <paramref name="max"/> UTF-16 units, without a dangling high surrogate.</summary>
    internal static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        var n = max;
        if (n > 0 && char.IsHighSurrogate(value[n - 1])) n--;
        return value[..n];
    }
}
