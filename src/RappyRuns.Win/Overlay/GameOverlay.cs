using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RappyRuns.Core.Ghost;
using static RappyRuns.Win.Overlay.NativeMethods;

namespace RappyRuns.Win.Overlay;

/// <summary>
/// The in-game overlay (overlay-win32.lisp): timer + ghost gap + room-split
/// rows + in-world ghost marker, drawn over the PSOBB window while a quest
/// runs. PSOBB itself is never touched - this is an ordinary topmost layered
/// window that happens to sit over the game (the read-only-access policy).
/// </summary>
/// <remarks>
/// <para>The window spans the game's whole client area while the in-world
/// marker is live, else just the corner panel. Everything painted in the key
/// color (magenta) is fully transparent via LWA_COLORKEY, so the game shows
/// through except where the panel and marker are drawn - those blend at the
/// fixed LWA_ALPHA. WS_EX_TRANSPARENT makes the window click-through (lifted
/// while Ctrl is held over the foreground game, for Ctrl+drag) and
/// WS_EX_NOACTIVATE keeps focus on the game.</para>
/// <para>Threading: the window lives on its own thread with its own message
/// loop, started lazily by the first <see cref="Show"/>. The poll loop only
/// swaps in an immutable <see cref="OverlayContent"/> (at 4 Hz) and the live
/// camera is pulled from the supplied source at paint time; a timer on the
/// overlay thread follows the game window and repaints - 30 Hz while the
/// marker is live (it must pan with the camera), 10 Hz for the plain pill.
/// The overlay thread never reads the config.</para>
/// <para>DPI: the overlay thread runs DPI-unaware (PLAN.md decision 3), so
/// client-rect coordinates and the fixed 20/16 px fonts match the Lisp client.</para>
/// </remarks>
public sealed class GameOverlay : IDisposable
{
    private const string ClassName = "RappyRunsOverlay";
    private const nuint TimerId = 1;

    /// <summary>10 Hz while no ghost course is live: the compact pill's clock does not need more.</summary>
    private const uint TimerMs = 100;

    /// <summary>30 Hz while the marker is live: a camera turn at 10 Hz visibly drags it behind the scenery.</summary>
    private const uint MarkerTimerMs = 33;

    /// <summary>
    /// How often the tick re-asserts HWND_TOPMOST (overlay-win32.lisp:521).
    /// WS_EX_TOPMOST is set once at creation and the follow-the-game
    /// SetWindowPos runs with SWP_NOZORDER, so nothing else ever puts the
    /// window back on top after Windows drops it out of the topmost band -
    /// which happens in the field (another topmost app, a display-mode switch,
    /// the game re-asserting its own z-order). Caught live on 2026-08-21 with
    /// the overlay twelve windows below the game.
    /// </summary>
    private const long TopmostIntervalMs = 1000;

    // COLORREF is 0x00BBGGRR (overlay-win32.lisp:421-438).
    /// <summary>Magenta color key: never used by a visible element; GDI solid fills reproduce it exactly.</summary>
    private const uint KeyColor = 0xFF00FF;
    private const uint BgColor = 0x201410;
    private const uint TextColor = 0xF0F0F0;
    private const uint AheadColor = 0x78DC50;
    private const uint BehindColor = 0x6E6EFF;
    private const uint GhostColor = 0x00A8FF;

    private static readonly ConcurrentDictionary<nint, GameOverlay> Overlays = new();
    private static readonly Lock ClassLock = new();
    private static bool _classRegistered;

    private readonly Func<CameraState?> _liveCamera;
    private readonly Func<nint> _findGameWindow;
    private readonly Action<string>? _log;
    private readonly bool _excludeFromCapture;
    private readonly Lock _threadLock = new();

    // --- Shared with the poll thread (single reference swaps) ---
    private volatile OverlayContent? _content;
    private volatile bool _wanted;
    private volatile bool _disposed;
    private volatile PositionBox? _draggedPos;
    private volatile PositionBox? _dropPos;
    private Thread? _thread;
    private nint _hwnd;

    // --- Overlay thread only ---
    private nint _font, _smallFont, _keyBrush, _bgBrush, _ghostBrush;
    private nint _memDc, _memBitmap;
    private (int W, int H)? _memSize, _memFailedSize;
    private bool _visible;
    private (int W, int H)? _size;
    private bool _full;
    private (int X, int Y, int W, int H)? _placement;
    private uint _timerCurrentMs;
    private long? _topmostAt;
    // Change-only diagnostics (S08): the last visibility state, (mode, size)
    // and topmost result written to the log, so a steady state logs nothing.
    private string? _loggedState;
    private (bool Full, int W, int H)? _loggedShape;
    private bool? _loggedTopmost;
    private bool _inputEnabled;
    private DragState? _drag;
    private nint _gameHwnd;
    private (int Left, int Top, int Right, int Bottom)? _gameRect;

    private sealed record PositionBox(OverlayCustomPosition Value);

    private sealed class DragState(int grabDx, int grabDy)
    {
        public int GrabDx { get; } = grabDx;
        public int GrabDy { get; } = grabDy;
        public OverlayCustomPosition? Pos { get; set; }
    }

    /// <param name="liveCamera">The freshest game camera (e.g. <c>() =&gt; session.LiveCamera</c>),
    /// read at every marker repaint - the 4 Hz content cadence visibly lags a camera turn.</param>
    /// <param name="findGameWindow">The game window to follow, 0 when none. Defaults to
    /// <see cref="FindPsobbWindow"/>; the smoke test points it at a stand-in.</param>
    /// <param name="log">The recording log (the topmost re-assert failure goes there).</param>
    /// <param name="excludeFromCapture">Diagnostics only: false leaves the window capturable so
    /// a screenshot can show what it paints. The client always excludes it.</param>
    public GameOverlay(Func<CameraState?> liveCamera, Func<nint>? findGameWindow = null, Action<string>? log = null,
        bool excludeFromCapture = true)
    {
        _liveCamera = liveCamera;
        _findGameWindow = findGameWindow ?? FindPsobbWindow;
        _log = log;
        _excludeFromCapture = excludeFromCapture;
    }

    /// <summary>The overlay window, or 0 before its thread created it (diagnostics).</summary>
    public nint WindowHandle => Volatile.Read(ref _hwnd);

    /// <summary>
    /// The PSOBB main window (find-psobb-window, win32.lisp:222): the Ephinea
    /// title first, then the stock one. 0 when neither exists.
    /// </summary>
    public static nint FindPsobbWindow()
    {
        var hwnd = FindWindow(null, "Ephinea: Phantasy Star Online Blue Burst");
        return hwnd != 0 ? hwnd : FindWindow(null, "PHANTASY STAR ONLINE Blue Burst");
    }

    /// <summary>
    /// Want the overlay visible with <paramref name="content"/> (overlay-show!,
    /// overlay-win32.lisp:772). Any thread; starts the overlay thread lazily and
    /// never throws into the poll loop. Clears the just-dropped drag position
    /// only when no unconsumed drag publish exists: the caller consumed
    /// <see cref="TakeDraggedPosition"/> before building the content, so it
    /// normally carries the dragged spot already - but a drag that finished
    /// during this tick must keep its override until the next tick persists it,
    /// or the panel would snap back for ~250 ms.
    /// </summary>
    public void Show(OverlayContent content)
    {
        if (_disposed) return;
        _content = content;
        _wanted = true;
        if (_draggedPos is null) _dropPos = null;
        try
        {
            lock (_threadLock)
            {
                if (_thread is { IsAlive: true }) return;
                _thread = new Thread(ThreadMain) { IsBackground = true, Name = "eta-client-overlay" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }
        catch (Exception)
        {
            // Best-effort: a failure here must never touch the poll loop.
        }
    }

    /// <summary>
    /// Want the overlay gone (overlay-hide!); the overlay thread's next tick
    /// hides it. The thread itself stays up - an idle timer costs nothing and
    /// the next quest reuses it.
    /// </summary>
    public void Hide()
    {
        _wanted = false;
        if (_content is { } content) _content = content with { Ghost = null };
    }

    /// <summary>
    /// The (x-frac, y-frac) a finished Ctrl+drag left for persistence, consumed
    /// by this call; null when there is none. Call it at the start of the 4 Hz
    /// update, BEFORE building the next <see cref="OverlayContent"/>: save it as
    /// <c>:overlay-corner :custom</c> + <c>:overlay-position</c>
    /// (<see cref="OverlayPlacement.ToSexp"/>), refresh the corner selector, and
    /// build the content from the saved values - then the overlay can drop its
    /// local override without a snap-back (update-ghost-overlay,
    /// overlay-win32.lisp:810-819).
    /// </summary>
    public OverlayCustomPosition? TakeDraggedPosition() => Interlocked.Exchange(ref _draggedPos, null)?.Value;

    /// <summary>Destroys the window and ends the overlay thread (the Lisp client just died with the process).</summary>
    public void Dispose()
    {
        _disposed = true;
        _wanted = false;
        Thread? thread;
        lock (_threadLock) thread = _thread;
        var hwnd = WindowHandle;
        if (hwnd != 0) PostMessage(hwnd, WM_CLOSE, 0, 0);
        thread?.Join(TimeSpan.FromSeconds(2));
    }

    // ------------------------------------------------------------------
    // Overlay thread
    // ------------------------------------------------------------------

    private OverlayContent? Content => _content;

    private int CurrentHeight(OverlayContent? content) => content?.PanelHeight ?? OverlayLayout.CompactHeight;

    private unsafe void ThreadMain()
    {
        nint hwnd = 0;
        try
        {
            // Before any window exists on this thread: coordinates and fonts
            // as the DPI-unaware Lisp client saw them.
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_UNAWARE);
            RegisterClass();
            hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                ClassName, ClassName, WS_POPUP,
                0, 0, OverlayLayout.Width, OverlayLayout.GhostHeight,
                0, 0, GetModuleHandle(null), 0);
            if (hwnd == 0) return;
            Overlays[hwnd] = this;
            Volatile.Write(ref _hwnd, hwnd);

            ReleaseBackbuffer();
            _visible = false;
            _size = null;
            _full = false;
            _placement = null;
            _topmostAt = null;
            _loggedState = null;
            _loggedShape = null;
            _loggedTopmost = null;
            _log?.Invoke("overlay: window created");
            // Drag state must not survive a thread restart: the fresh window
            // starts WS_EX_TRANSPARENT, so a stale drag could never receive its
            // button-up and would pin the panel forever.
            _drag = null;
            _dropPos = null;
            _inputEnabled = false;
            _timerCurrentMs = TimerMs;

            // Alpha blends the visible elements; the color key punches the
            // rest of the client-area-sized window fully out.
            SetLayeredWindowAttributes(hwnd, KeyColor, OverlayLayout.Alpha, LWA_ALPHA | LWA_COLORKEY);
            // Keep the overlay out of screen captures: the fullscreen
            // recording path duplicates the whole monitor (ddagrab) and the
            // submitted footage must show the game, not our panel.
            if (_excludeFromCapture) SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            _font = CreateFont(20, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
            _smallFont = CreateFont(16, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
            _keyBrush = CreateSolidBrush(KeyColor);
            _bgBrush = CreateSolidBrush(BgColor);
            _ghostBrush = CreateSolidBrush(GhostColor);
            SetTimer(hwnd, TimerId, TimerMs, 0);

            MSG msg;
            while (GetMessage(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessage(&msg);
            }
        }
        catch (Exception e)
        {
            _log?.Invoke($"overlay thread error: {e.Message}");
        }
        finally
        {
            // Fonts, brushes and the back buffer are released whatever ended the loop.
            ReleaseBackbuffer();
            foreach (var obj in new[] { _font, _smallFont, _keyBrush, _bgBrush, _ghostBrush })
                if (obj != 0) DeleteObject(obj);
            _font = _smallFont = _keyBrush = _bgBrush = _ghostBrush = 0;
            if (hwnd != 0) Overlays.TryRemove(hwnd, out _);
            Volatile.Write(ref _hwnd, 0);
            _visible = false;
        }
    }

    private static unsafe void RegisterClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    CbSize = (uint)sizeof(WNDCLASSEXW),
                    WndProc = &WndProc,
                    Instance = GetModuleHandle(null),
                    ClassName = name,
                };
                RegisterClassEx(&wc);
            }
            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (!Overlays.TryGetValue(hwnd, out var self)) return DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            return self.Handle(hwnd, msg, wParam, lParam);
        }
        catch (Exception)
        {
            // ignore-errors: an exception must never unwind into user32.
            return msg == WM_NCHITTEST ? HTTRANSPARENT : 0;
        }
    }

    private nint Handle(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                Swallow(() => TimerTick(hwnd));
                return 0;
            case WM_PAINT:
                Swallow(() => Paint(hwnd));
                return 0;
            case WM_ERASEBKGND:
                // Regions exposed by a grow (pill -> full client area, game
                // resize) are otherwise undefined - usually black, which the
                // key does not match, so DWM could flash them opaquely over the
                // game until the next WM_PAINT. The key color is invisible.
                if (_keyBrush != 0)
                {
                    var (w, h) = _size ?? (OverlayLayout.Width, OverlayLayout.GhostHeight);
                    Swallow(() => FillRectangle(wParam, 0, 0, w, h, _keyBrush));
                }
                return 1;
            case WM_NCHITTEST:
                // Only reached while Ctrl has lifted WS_EX_TRANSPARENT.
                // Everything outside the panel - the marker too - reports
                // HTTRANSPARENT so the click continues to the game.
                {
                    var (x, y) = LParamPoint(lParam);
                    bool hit;
                    try
                    {
                        hit = _drag is not null || PanelHit(x, y);
                    }
                    catch (Exception)
                    {
                        hit = false;
                    }
                    return hit ? HTCLIENT : HTTRANSPARENT;
                }
            case WM_LBUTTONDOWN:
                {
                    bool grabbed;
                    try
                    {
                        grabbed = DragBegin(hwnd);
                    }
                    catch (Exception)
                    {
                        grabbed = false;
                    }
                    return grabbed ? 0 : DefWindowProc(hwnd, msg, wParam, lParam);
                }
            case WM_MOUSEMOVE:
                if (_drag is not null) Swallow(() => DragMove(hwnd));
                return 0;
            case WM_LBUTTONUP:
            case WM_CAPTURECHANGED:
                if (_drag is not null) Swallow(DragEnd);
                return 0;
            case WM_DESTROY:
                KillTimer(hwnd, TimerId);
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static void Swallow(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // ignore-errors, as every Lisp wndproc branch.
        }
    }

    /// <summary>
    /// Follow the game window and repaint while wanted, hide otherwise - also
    /// when the game window is gone mid-quest or minimized to a degenerate
    /// client rect (overlay-timer-tick, overlay-win32.lisp:1007). A transient
    /// rect-query failure keeps the overlay up at its last placement rather
    /// than blinking it off. Re-arms the timer between the pill rate and the
    /// marker rate, and keeps re-claiming the topmost band.
    /// </summary>
    private unsafe void TimerTick(nint hwnd)
    {
        var content = Content;
        var wantedMs = content?.MarkerWanted == true ? MarkerTimerMs : TimerMs;
        if (wantedMs != _timerCurrentMs)
        {
            _timerCurrentMs = wantedMs;
            SetTimer(hwnd, TimerId, wantedMs, 0);
        }
        var wanted = _wanted;
        var game = wanted ? _findGameWindow() : 0;
        var fit = game != 0 ? Position(hwnd, game, content) : Fit.Failed;
        _gameHwnd = game;
        if (game == 0 || fit == Fit.Degenerate)
        {
            Conceal(hwnd);
            LogState(!wanted ? "hidden: not wanted (no quest running)"
                : game == 0 ? "hidden: game window not found"
                : "hidden: game client area is empty (minimized?)");
        }
        else if (fit == Fit.Fit || _placement is not null)
        {
            if (!_visible)
            {
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                _visible = true;
            }
            LogState(fit == Fit.Fit ? "shown" : "shown: game client rect unreadable, kept at the last placement");
            // Ctrl arms the drag - but only while the game itself is the
            // foreground window, so Ctrl+clicks aimed at another app that
            // overlaps the topmost panel are never eaten. The same test gates
            // the topmost re-assert.
            var foreground = GetForegroundWindow() == game;
            var arm = GetAsyncKeyState(VK_CONTROL) < 0 && foreground;
            if (arm && !_inputEnabled) Swallow(() => ApplyInput(hwnd, true));
            else if (!arm && _inputEnabled && _drag is null) Swallow(() => ApplyInput(hwnd, false));
            Swallow(() => KeepTopmost(hwnd, foreground));
            InvalidateRect(hwnd, null, false);
        }
        else
        {
            LogState("hidden: game client rect unreadable before the first placement");
        }
    }

    /// <summary>
    /// Log a visibility state once, when it differs from the last one logged
    /// (the tick runs at 10-30 Hz; a steady state must cost nothing). A shown
    /// state carries the placement, so "shown but nowhere near the game" can
    /// be told from "never shown".
    /// </summary>
    private void LogState(string state)
    {
        if (state == _loggedState) return;
        _loggedState = state;
        if (_visible && _placement is { } shown) _loggedShape = (_full, shown.W, shown.H);
        _log?.Invoke(_visible && _placement is { } p
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"overlay: {state} at {p.X},{p.Y} {p.W}x{p.H} ({(_full ? "full client area" : "panel")}), game hwnd {_gameHwnd:X}")
            : $"overlay: {state}");
    }

    private enum Fit
    {
        Failed,
        Fit,
        Degenerate,
    }

    /// <summary>
    /// Fit against the game's client area (overlay-position,
    /// overlay-win32.lisp:575): the whole area while the marker is live (the
    /// color key keeps all but panel and marker transparent), just the panel
    /// otherwise. Skips SetWindowPos when the placement has not moved.
    /// </summary>
    private Fit Position(nint hwnd, nint game, OverlayContent? content)
    {
        if (ClientScreenRect(game) is not { } rect) return Fit.Failed;
        var clientW = rect.Right - rect.Left;
        var clientH = rect.Bottom - rect.Top;
        if (clientW <= 0 || clientH <= 0) return Fit.Degenerate;
        var (panelX, panelY) = PanelPosition(content, clientW, clientH);
        _gameRect = rect;
        var full = content?.MarkerWanted == true;
        var placement = full
            ? (rect.Left, rect.Top, clientW, clientH)
            : (rect.Left + panelX, rect.Top + panelY, OverlayLayout.Width, CurrentHeight(content));
        _size = (placement.Item3, placement.Item4);
        _full = full;
        if (_placement != placement)
        {
            _placement = placement;
            SetWindowPos(hwnd, 0, placement.Item1, placement.Item2, placement.Item3, placement.Item4, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        // Log a mode or size change while shown - not every move: a drag or a
        // moving game window would log each tick.
        var shape = (full, placement.Item3, placement.Item4);
        if (_visible && _loggedShape is { } logged && logged != shape)
        {
            _loggedShape = shape;
            _log?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"overlay: placement now {placement.Item1},{placement.Item2} {placement.Item3}x{placement.Item4} ({(full ? "full client area" : "panel")})"));
        }
        return Fit.Fit;
    }

    /// <summary>The client area's screen rect (window-client-screen-rect, ffmpeg-win32.lisp:554), or null.</summary>
    private static (int Left, int Top, int Right, int Bottom)? ClientScreenRect(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var rect)) return null;
        var point = new POINT();
        if (!ClientToScreen(hwnd, ref point)) return null;
        return (point.X, point.Y, point.X + rect.Right, point.Y + rect.Bottom);
    }

    /// <summary>
    /// The panel origin within the client area: an in-progress drag first,
    /// then a just-dropped position awaiting persistence, then the configured
    /// anchor / custom spot (overlay-panel-position).
    /// </summary>
    private (int X, int Y) PanelPosition(OverlayContent? content, int areaW, int areaH)
    {
        var live = _drag?.Pos ?? _dropPos?.Value;
        return OverlayPlacement.PanelOrigin(
            live is not null ? OverlayCorner.Custom : content?.Corner ?? OverlayCorner.TopRight,
            live ?? content?.Custom,
            areaW, areaH, OverlayLayout.Width, CurrentHeight(content), OverlayLayout.MarginX, OverlayLayout.MarginY);
    }

    private unsafe void Conceal(nint hwnd)
    {
        if (_drag is not null) Swallow(DragEnd);
        if (_inputEnabled) Swallow(() => ApplyInput(hwnd, false));
        if (_visible)
        {
            ShowWindow(hwnd, SW_HIDE);
            _visible = false;
        }
        // The back buffer can be a client-area-sized bitmap (tens of MB at
        // 4K): do not pin it between quests. Also clears the failed-size memo.
        Swallow(ReleaseBackbuffer);
        // Whatever put the game in front of us may have taken the topmost band
        // with it, so the next show re-asserts at once.
        _topmostAt = null;
        // The next show logs its placement and topmost result afresh.
        _loggedShape = null;
        _loggedTopmost = null;
    }

    /// <summary>
    /// Back to the head of the topmost band, at most once a second and only
    /// while the game holds the foreground - an overlay hoisted over an app the
    /// player deliberately brought up would be worse than one behind the game
    /// (overlay-keep-topmost, overlay-win32.lisp:977). The timestamp is
    /// committed before the call, so a persistent failure costs one attempt a
    /// second. The result is logged when it changes (and once per show), since
    /// "the overlay is behind the game" looks exactly like the bug this guards
    /// against.
    /// </summary>
    private void KeepTopmost(nint hwnd, bool gameForeground)
    {
        if (!gameForeground) return;
        var now = Stopwatch.GetTimestamp();
        if (_topmostAt is { } at && Stopwatch.GetElapsedTime(at, now).TotalMilliseconds < TopmostIntervalMs) return;
        _topmostAt = now;
        var ok = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (ok == _loggedTopmost) return;
        _loggedTopmost = ok;
        _log?.Invoke(ok
            ? "overlay: HWND_TOPMOST re-asserted"
            : "overlay: HWND_TOPMOST re-assert failed - the panel may be sitting behind the game");
    }

    // --- Painting ---

    /// <summary>
    /// WM_PAINT: compose the frame into the back buffer and blit it in one
    /// BitBlt - painting straight to the window would flash the key-color
    /// ground through the panel every frame (PR #281's flicker). EndPaint runs
    /// even when drawing throws, or the invalid region never clears and
    /// WM_PAINT storms; the blit runs even after a failed compose, so a
    /// persistent draw error degrades to direct drawing instead of freezing
    /// at the last good frame (overlay-paint, overlay-win32.lisp:820).
    /// </summary>
    private unsafe void Paint(nint hwnd)
    {
        PAINTSTRUCT ps;
        var hdc = BeginPaint(hwnd, &ps);
        try
        {
            if (hdc == 0) return;
            var content = Content;
            var (width, height) = _size ?? (OverlayLayout.Width, CurrentHeight(content));
            var memDc = Backbuffer(hdc, width, height);
            try
            {
                DrawFrame(memDc != 0 ? memDc : hdc, width, height, content);
            }
            finally
            {
                if (memDc != 0) BitBlt(hdc, 0, 0, width, height, memDc, 0, 0, SRCCOPY);
            }
        }
        finally
        {
            EndPaint(hwnd, &ps);
        }
    }

    /// <summary>
    /// The cached memory DC for a width x height bitmap, recreated only on a
    /// size change. The working buffer is kept until its replacement exists -
    /// creation fails exactly when GDI is short - and a failed size is
    /// remembered so the 30 Hz paint does not retry it every frame. 0 on
    /// failure: the caller paints straight to the window.
    /// </summary>
    private nint Backbuffer(nint hdc, int width, int height)
    {
        var size = (width, height);
        if (_memDc != 0 && _memSize == size) return _memDc;
        if (_memFailedSize == size) return 0;
        var memDc = CreateCompatibleDC(hdc);
        nint bitmap = 0;
        if (memDc != 0)
        {
            bitmap = CreateCompatibleBitmap(hdc, width, height);
            if (bitmap == 0) DeleteDC(memDc);
        }
        if (bitmap == 0)
        {
            _memFailedSize = size;
            return 0;
        }
        SelectObject(memDc, bitmap);
        ReleaseBackbuffer();
        _memDc = memDc;
        _memBitmap = bitmap;
        _memSize = size;
        return memDc;
    }

    private void ReleaseBackbuffer()
    {
        // DeleteDC deselects our bitmap, so no stock-bitmap restore is needed.
        if (_memDc != 0) DeleteDC(_memDc);
        if (_memBitmap != 0) DeleteObject(_memBitmap);
        _memDc = _memBitmap = 0;
        _memSize = _memFailedSize = null;
    }

    /// <summary>
    /// Compose one frame. The back buffer is a long-lived DC, so the viewport
    /// origin and selected font must not leak across frames: a drawing error
    /// that left the origin shifted would misplace the next frame's key fill.
    /// </summary>
    private void DrawFrame(nint hdc, int width, int height, OverlayContent? content)
    {
        var saved = SaveDC(hdc);
        try
        {
            DrawFrameCore(hdc, width, height, content);
        }
        finally
        {
            if (saved > 0) RestoreDC(hdc, -1);
        }
    }

    private unsafe void DrawFrameCore(nint hdc, int width, int height, OverlayContent? content)
    {
        var full = _full;
        var (panelX, panelY) = full ? PanelPosition(content, width, height) : (0, 0);
        var data = content?.Ghost;
        // Only the full-window mode draws the marker, so skip the
        // interpolation otherwise.
        var ghostPos = data is not null && full ? data.GhostPositionAt(data.EffectiveElapsed(Stopwatch.GetTimestamp())) : null;

        if (_keyBrush != 0) FillRectangle(hdc, 0, 0, width, height, _keyBrush);
        // The panel paints in its own coordinates via a shifted viewport
        // origin, exactly as when the window WAS the panel.
        SetViewportOrgEx(hdc, panelX, panelY, null);
        if (_bgBrush != 0) FillRectangle(hdc, 0, 0, OverlayLayout.Width, CurrentHeight(content), _bgBrush);
        if (_font != 0) SelectObject(hdc, _font);
        SetBkMode(hdc, BK_TRANSPARENT);
        SetTextColor(hdc, TextColor);
        var line1 = content?.Line1 ?? "";
        TextOut(hdc, 12, 6, line1, line1.Length);
        if (content?.Line2 is { } line2)
        {
            SetTextColor(hdc, content.DeltaState switch
            {
                GhostDeltaState.Ahead => AheadColor,
                GhostDeltaState.Behind => BehindColor,
                _ => TextColor,
            });
            TextOut(hdc, 12, 32, line2, line2.Length);
        }
        if (content is not null && data is not null) DrawSplits(hdc, content, data);
        SetViewportOrgEx(hdc, 0, 0, null);
        if (full && data is not null) DrawMarker(hdc, width, height, data, ghostPos);
    }

    /// <summary>
    /// The room-split rows under the header: the newest matched rooms, oldest
    /// of them first so the list reads downward like the run - label, enter
    /// clock, cumulative gap colored like the header (overlay-draw-splits,
    /// overlay-win32.lisp:647).
    /// </summary>
    private void DrawSplits(nint hdc, OverlayContent content, GhostOverlayData data)
    {
        if (data.Splits.Count == 0 || _smallFont == 0) return;
        SelectObject(hdc, _smallFont);
        var y = OverlayLayout.SplitTop;
        for (var i = data.Splits.Count - 1; i >= 0; i--, y += OverlayLayout.SplitRowHeight)
        {
            var split = data.Splits[i];
            var (room, clock, delta) = content.SplitRow(split, data.Precision);
            SetTextColor(hdc, TextColor);
            TextOut(hdc, 12, y, room, room.Length);
            TextOut(hdc, 116, y, clock, clock.Length);
            SetTextColor(hdc, split.Delta < 0 ? AheadColor : BehindColor);
            TextOut(hdc, 180, y, delta, delta.Length);
        }
    }

    /// <summary>
    /// The in-world marker: an orange dot at the ghost's feet with a name pill
    /// above it (overlay-draw-ghost-marker, overlay-win32.lisp:670). The pill is
    /// solid so ClearType never blends text against the color key.
    /// </summary>
    private void DrawMarker(nint hdc, int width, int height, GhostOverlayData data, GhostPosition? ghostPos)
    {
        if (data.MarkerPoint(_liveCamera(), width, height, ghostPos) is not { } p) return;
        if (_ghostBrush != 0)
        {
            var oldBrush = SelectObject(hdc, _ghostBrush);
            var oldPen = SelectObject(hdc, GetStockObject(NULL_PEN));
            Ellipse(hdc, p.X - 6, p.Y - 6, p.X + 6 + 1, p.Y + 6 + 1);
            SelectObject(hdc, oldPen);
            SelectObject(hdc, oldBrush);
        }
        var label = data.MarkerLabel;
        if (_smallFont != 0) SelectObject(hdc, _smallFont);
        if (!GetTextExtentPoint32(hdc, label, label.Length, out var size)) return;
        var pillBottom = p.Y - 10;
        var pillTop = pillBottom - size.Cy - 4;
        var pillLeft = p.X - size.Cx / 2 - 6;          // (floor tw 2), tw >= 0
        var pillRight = p.X + (size.Cx + 1) / 2 + 6;   // (ceiling tw 2)
        FillRectangle(hdc, pillLeft, pillTop, pillRight, pillBottom, _bgBrush);
        SetTextColor(hdc, GhostColor);
        TextOut(hdc, pillLeft + 6, pillTop + 2, label, label.Length);
    }

    private static unsafe void FillRectangle(nint hdc, int left, int top, int right, int bottom, nint brush)
    {
        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        FillRect(hdc, &rect, brush);
    }

    // --- Ctrl+drag ---

    /// <summary>
    /// Lift / restore WS_EX_TRANSPARENT (overlay-apply-input). Lifted, the
    /// opaque panel receives mouse input while the color-keyed rest of the
    /// window stays click-through; restored, every pixel passes clicks on.
    /// </summary>
    private void ApplyInput(nint hwnd, bool enabled)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var wanted = enabled ? style & ~(nint)WS_EX_TRANSPARENT : style | (nint)WS_EX_TRANSPARENT;
        if (style != wanted)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, wanted);
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        _inputEnabled = enabled;
    }

    /// <summary>The screen point in a WM_NCHITTEST lParam: two signed 16-bit halves (negative left of / above the primary monitor).</summary>
    internal static (int X, int Y) LParamPoint(nint lParam) =>
        ((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

    private (int X, int Y)? PanelScreenOrigin()
    {
        if (_gameRect is not { } rect) return null;
        var (px, py) = PanelPosition(Content, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return (rect.Left + px, rect.Top + py);
    }

    /// <summary>Is the screen point on the panel? Shared by the grab and WM_NCHITTEST, so what is grabbable and what swallows a click are the same pixels.</summary>
    private bool PanelHit(int x, int y)
    {
        if (PanelScreenOrigin() is not { } o) return false;
        return o.X <= x && x <= o.X + OverlayLayout.Width && o.Y <= y && y <= o.Y + CurrentHeight(Content);
    }

    private bool DragBegin(nint hwnd)
    {
        if (!GetCursorPos(out var cursor) || !PanelHit(cursor.X, cursor.Y)) return false;
        if (PanelScreenOrigin() is not { } o) return false;
        _drag = new DragState(cursor.X - o.X, cursor.Y - o.Y);
        SetCapture(hwnd);
        return true;
    }

    /// <summary>
    /// Follow the cursor: the panel origin clamps inside the client area and is
    /// stored as slack fractions, then the window refits (overlay-drag-move).
    /// </summary>
    private unsafe void DragMove(nint hwnd)
    {
        if (_drag is not { } drag || _gameRect is not { } rect) return;
        if (!GetCursorPos(out var cursor)) return;
        var slackX = Math.Max(0, rect.Right - rect.Left - OverlayLayout.Width);
        var slackY = Math.Max(0, rect.Bottom - rect.Top - CurrentHeight(Content));
        var px = Math.Min(slackX, Math.Max(0, cursor.X - drag.GrabDx - rect.Left));
        var py = Math.Min(slackY, Math.Max(0, cursor.Y - drag.GrabDy - rect.Top));
        drag.Pos = new OverlayCustomPosition(
            slackX == 0 ? 0.0f : (float)px / slackX,
            slackY == 0 ? 0.0f : (float)py / slackY);
        if (_gameHwnd != 0) Position(hwnd, _gameHwnd, Content);
        InvalidateRect(hwnd, null, false);
    }

    /// <summary>
    /// Finish the drag: publish the drop for persistence and keep it live
    /// meanwhile. The publish is written BEFORE the local override - Show only
    /// clears the override when no unconsumed publish exists, and this order
    /// means it can never observe the override without the publish. Also runs
    /// on the WM_CAPTURECHANGED our own ReleaseCapture raises; the drag is
    /// already cleared by then, so that is a no-op (overlay-drag-end).
    /// </summary>
    private void DragEnd()
    {
        if (_drag is not { } drag) return;
        _drag = null;
        if (drag.Pos is { } pos)
        {
            _draggedPos = new PositionBox(pos);
            _dropPos = new PositionBox(pos);
        }
        ReleaseCapture();
    }
}
