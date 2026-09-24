using System.Runtime.InteropServices;
using RappyRuns.Core.I18n;
using RappyRuns.Win.Shell;

namespace RappyRuns.Tests.Shell;

// Tests never touch the real mutex or window class: a running client (Lisp or
// C#) on the dev machine would otherwise be raised by them. Each test uses its
// own names and runs the tray without an icon.
public class SingleInstanceTests
{
    [Fact]
    public void ContractValuesMatchTheLispClient()
    {
        // tray-win32.lisp:276-320
        Assert.Equal("RappyRunsClient-single-instance", SingleInstance.MutexName);
        Assert.Equal("RappyRunsTrayWindow", SingleInstance.TrayClassName);
        Assert.Equal("RappyRunsTray", SingleInstance.TrayWindowName);
        Assert.Equal(0x8002u, SingleInstance.ShowRequestMessage);
        Assert.Equal(0x8001u, TrayIcon.CallbackMessage);
    }

    [Fact]
    public void SecondClaimOfTheMutexReportsRunning()
    {
        var name = "RappyRunsTests-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.Claim(name);
        Assert.False(first.AlreadyRunning);
        using (var second = SingleInstance.Claim(name))
            Assert.True(second.AlreadyRunning);
        // Still held by the first claim.
        using (var third = SingleInstance.Claim(name))
            Assert.True(third.AlreadyRunning);
    }

    [Fact]
    public void MutexIsReleasedWhenTheHolderGoesAway()
    {
        var name = "RappyRunsTests-" + Guid.NewGuid().ToString("N");
        SingleInstance.Claim(name).Dispose();
        using var again = SingleInstance.Claim(name);
        Assert.False(again.AlreadyRunning);
    }

    [Fact]
    public void SignalWithoutAWindowIsANoOp() =>
        Assert.False(SingleInstance.SignalExisting("RappyRunsTests-none-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void SecondInstanceHandshakeReachesTheTrayWindow()
    {
        var className = "RappyRunsTests-" + Guid.NewGuid().ToString("N");
        using var shown = new ManualResetEventSlim();
        string? threadName = null;
        using var tray = new TrayIcon(new TrayIconOptions
        {
            ClassName = className,
            ShowIcon = false,
            ShowRequested = () =>
            {
                threadName = Thread.CurrentThread.Name;
                shown.Set();
            },
        });
        Assert.True(tray.Start());

        // What a second process does after finding the mutex taken.
        Assert.True(SingleInstance.SignalExisting(className));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "0x8002 did not reach the tray window");
        Assert.Equal("eta-client-tray", threadName);

        // A top-level window with the contract title, not message-only.
        Assert.Equal(tray.WindowHandle, NativeMethods.FindWindowW(className, SingleInstance.TrayWindowName));
    }
}

public class TrayIconTests
{
    private static TrayIcon Start(TrayIconOptions options)
    {
        var tray = new TrayIcon(options);
        Assert.True(tray.Start());
        return tray;
    }

    [Fact]
    public void DoubleClickShowsSingleClickDoesNothing()
    {
        var shows = 0;
        using var shown = new ManualResetEventSlim();
        using var tray = Start(new TrayIconOptions
        {
            ClassName = "RappyRunsTests-" + Guid.NewGuid().ToString("N"),
            ShowIcon = false,
            ShowRequested = () => { Interlocked.Increment(ref shows); shown.Set(); },
        });
        // WM_LBUTTONUP (0x202) first: must be ignored, as in RappyTrayWndProc.
        NativeMethods.PostMessageW(tray.WindowHandle, TrayIcon.CallbackMessage, 0, 0x202);
        NativeMethods.PostMessageW(tray.WindowHandle, TrayIcon.CallbackMessage, 0, (nint)NativeMethods.WM_LBUTTONDBLCLK);
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, shows);
    }

    [Fact]
    public void BalloonClickOpensTheLatestUrlOrShowsTheWindow()
    {
        string? opened = null;
        using var clicked = new AutoResetEvent(false);
        var shows = 0;
        using var tray = Start(new TrayIconOptions
        {
            ClassName = "RappyRunsTests-" + Guid.NewGuid().ToString("N"),
            ShowIcon = false,
            UrlClicked = url => { opened = url; clicked.Set(); },
            ShowRequested = () => { Interlocked.Increment(ref shows); clicked.Set(); },
        });

        tray.Notify("t", "x", NotifyKind.Info, "https://example.test/runs/1");
        NativeMethods.PostMessageW(tray.WindowHandle, TrayIcon.CallbackMessage, 0, (nint)NativeMethods.NIN_BALLOONUSERCLICK);
        Assert.True(clicked.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.Equal("https://example.test/runs/1", opened);
        Assert.Equal(0, shows);

        // A warning (no URL) resets the target: a click raises the window.
        tray.Notify("t", "x");
        NativeMethods.PostMessageW(tray.WindowHandle, TrayIcon.CallbackMessage, 0, (nint)NativeMethods.NIN_BALLOONUSERCLICK);
        Assert.True(clicked.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, shows);
    }

    [Fact]
    public void CallbackExceptionsDoNotKillTheTray()
    {
        var logged = new List<string>();
        var calls = 0;
        using var again = new ManualResetEventSlim();
        using var tray = Start(new TrayIconOptions
        {
            ClassName = "RappyRunsTests-" + Guid.NewGuid().ToString("N"),
            ShowIcon = false,
            Log = m => { lock (logged) logged.Add(m); },
            ShowRequested = () =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
                again.Set();
            },
        });
        NativeMethods.PostMessageW(tray.WindowHandle, SingleInstance.ShowRequestMessage, 0, 0);
        NativeMethods.PostMessageW(tray.WindowHandle, SingleInstance.ShowRequestMessage, 0, 0);
        Assert.True(again.Wait(TimeSpan.FromSeconds(5)));
        lock (logged) Assert.Contains(logged, m => m.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposeEndsTheThreadAndFreesTheClassName()
    {
        var className = "RappyRunsTests-" + Guid.NewGuid().ToString("N");
        var tray = Start(new TrayIconOptions { ClassName = className, ShowIcon = false });
        Assert.NotEqual(0, NativeMethods.FindWindowW(className, null));
        tray.Dispose();
        Assert.False(tray.IsRunning);
        Assert.Equal(0, NativeMethods.FindWindowW(className, null));
        // Restartable with the already-registered class.
        using var again = Start(new TrayIconOptions { ClassName = className, ShowIcon = false });
        Assert.True(again.IsRunning);
    }

    [Fact]
    public void NotifyBeforeStartIsSilent()
    {
        using var tray = new TrayIcon(new TrayIconOptions { ShowIcon = false });
        tray.Notify("t", "x", NotifyKind.Warning, "https://example.test");
        tray.RefreshTooltip();
        tray.RemoveIconNow();
        Assert.False(tray.IsRunning);
    }

    [Fact]
    public void NotifyIconDataHasTheFullVistaLayout()
    {
        // cbSize must be the whole NOTIFYICONDATAW (tray-win32.lisp:233).
        Assert.Equal(IntPtr.Size == 8 ? 976 : 956, Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>());
    }

    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("ab\U0001F600", 3, "ab")] // never keep half a surrogate pair
    public void TruncateKeepsWholeCharacters(string value, int max, string expected) =>
        Assert.Equal(expected, TrayIcon.Truncate(value, max));
}

public class ShellHostTests
{
    [Theory]
    [InlineData(true, true, CloseReason.UserClosing, CloseDecision.Allow)]
    [InlineData(true, false, CloseReason.UserClosing, CloseDecision.Allow)]
    [InlineData(false, true, CloseReason.UserClosing, CloseDecision.HideToTray)]
    [InlineData(false, false, CloseReason.UserClosing, CloseDecision.Quit)]
    [InlineData(false, true, CloseReason.WindowsShutDown, CloseDecision.Quit)]
    [InlineData(false, true, CloseReason.TaskManagerClosing, CloseDecision.Quit)]
    [InlineData(false, true, CloseReason.None, CloseDecision.HideToTray)]
    public void CloseRule(bool reallyQuitting, bool closeToTray, CloseReason reason, CloseDecision expected) =>
        Assert.Equal(expected, ShellHost.DecideClose(reallyQuitting, closeToTray, reason));

    [Fact]
    public void QuitPreparesThenExitsOnce()
    {
        var steps = new List<string>();
        using var shell = new ShellHost(new ShellHostOptions
        {
            MainWindow = () => null,
            Language = () => Language.En,
            TrayClassName = "RappyRunsTests-" + Guid.NewGuid().ToString("N"),
            ShowTrayIcon = false,
            PrepareQuit = () => steps.Add("prepare"),
            Exit = code => steps.Add($"exit {code}"),
        });
        Assert.False(shell.IsQuitting);
        shell.Quit();
        shell.Quit();
        Assert.True(shell.IsQuitting);
        Assert.Equal(["prepare", "exit 0"], steps);
        // Once quitting, the × button closes for real.
        Assert.Equal(CloseDecision.Allow, ShellHost.DecideClose(shell.IsQuitting, true, CloseReason.UserClosing));
    }

    [Fact]
    public void QuitStillExitsWhenPrepareThrows()
    {
        var exited = false;
        using var shell = new ShellHost(new ShellHostOptions
        {
            MainWindow = () => null,
            Language = () => Language.En,
            TrayClassName = "RappyRunsTests-" + Guid.NewGuid().ToString("N"),
            ShowTrayIcon = false,
            PrepareQuit = () => throw new InvalidOperationException("poll stuck"),
            Exit = _ => exited = true,
        });
        shell.Quit();
        Assert.True(exited);
    }

    [Fact]
    public void ShowWithoutAWindowIsANoOp()
    {
        var className = "RappyRunsTests-" + Guid.NewGuid().ToString("N");
        using var shell = new ShellHost(new ShellHostOptions
        {
            MainWindow = () => null,
            Language = () => Language.Ja,
            TrayClassName = className,
            ShowTrayIcon = false,
            Exit = _ => { },
        });
        Assert.True(shell.Start());
        shell.ShowMainWindow();
        shell.Notify("t", "x", NotifyKind.None);
        shell.OnLanguageChanged();
        // A 0x8002 before the window exists is dropped, like *interface* NIL.
        Assert.True(SingleInstance.SignalExisting(className));
        Assert.True(shell.Tray.IsRunning);
    }

    [Fact]
    public void TrayLabelsAreLocalized()
    {
        // ui-shell R6: the Japanese menu once vanished; the labels must come
        // through as real text in both languages.
        Assert.Equal("表示", Strings.Default.Tr(Language.Ja, "tray-show"));
        Assert.Equal("終了", Strings.Default.Tr(Language.Ja, "tray-quit"));
        Assert.Equal("Show", Strings.Default.Tr(Language.En, "tray-show"));
        Assert.Equal("Quit", Strings.Default.Tr(Language.En, "tray-quit"));
        Assert.Equal("Rappy Runs クライアント", Strings.Default.Tr(Language.Ja, "tray-tooltip"));
    }
}
