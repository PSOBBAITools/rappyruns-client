using System.Runtime.InteropServices;

namespace RappyRuns.Win.Shell;

/// <summary>
/// The single-instance guard (spec core §2.4, a <b>contract</b>). During the
/// migration the Lisp client and this one may both be installed and started
/// by the same logon Run entry or by hand, so the mutex name, the tray window
/// class and the "show yourself" message are byte-for-byte those of
/// client/src/tray-win32.lisp:300-594: whichever client starts first, the
/// second one raises it and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Named mutex shared with the Lisp client (tray-win32.lisp:320).</summary>
    public const string MutexName = "RappyRunsClient-single-instance";

    /// <summary>Class of the hidden tray-owner window the second instance looks for (tray-win32.lisp:318).</summary>
    public const string TrayClassName = "RappyRunsTrayWindow";

    /// <summary>Title of that window (tray-win32.lisp:533).</summary>
    public const string TrayWindowName = "RappyRunsTray";

    /// <summary>WM_APP+2: "un-hide your main window" (tray-win32.lisp:283).</summary>
    public const uint ShowRequestMessage = NativeMethods.WM_APP + 2;

    // The claim of the running process, kept here so it can never be
    // collected (closing the handle would release the instance lock -
    // tray-win32.lisp:324 keeps *singleton-mutex* for the same reason).
    private static SingleInstance? ProcessClaim { get; set; }

    private nint _handle;

    private SingleInstance(nint handle, bool alreadyRunning)
    {
        _handle = handle;
        AlreadyRunning = alreadyRunning;
    }

    /// <summary>True when another instance (Lisp or C#) already held the mutex.</summary>
    public bool AlreadyRunning { get; }

    /// <summary>
    /// Create (or open) the named mutex and report whether it already
    /// existed. Mirrors <c>already-running-p</c> (tray-win32.lisp:575):
    /// CreateMutexW(NULL, FALSE, name), then GetLastError == 183.
    /// </summary>
    /// <remarks>
    /// Deviation: ERROR_ACCESS_DENIED also counts as "running". It means the
    /// mutex exists but was created by a process we cannot open (an elevated
    /// copy); the Lisp code would have carried on and started a second
    /// resident client, which is exactly what the guard is for.
    /// </remarks>
    public static SingleInstance Claim(string name = MutexName)
    {
        var handle = NativeMethods.CreateMutexW(0, false, name);
        // Read immediately: nothing else may run between the call and this.
        var error = Marshal.GetLastPInvokeError();
        var running = error == NativeMethods.ERROR_ALREADY_EXISTS ||
                      (handle == 0 && error == NativeMethods.ERROR_ACCESS_DENIED);
        return new SingleInstance(handle, running);
    }

    /// <summary>
    /// <see cref="Claim"/> for the real mutex, holding the handle for the life
    /// of the process. Call first thing in Main (spec core §2.1 step 1).
    /// </summary>
    public static bool ClaimForProcess()
    {
        var claim = Claim();
        ProcessClaim = claim;
        return claim.AlreadyRunning;
    }

    /// <summary>
    /// Ask the running instance to show its window: FindWindowW(class) then
    /// PostMessageW(0x8002) (<c>signal-existing-instance</c>,
    /// tray-win32.lisp:585). Best-effort - the other instance may still be
    /// starting and not own its tray window yet. Returns true when posted.
    /// </summary>
    /// <remarks>
    /// Addition over Lisp: the first instance is granted the right to take the
    /// foreground (AllowSetForegroundWindow). This process was just launched by
    /// the user and so holds that right; without passing it on, Windows only
    /// flashes the other window's taskbar button instead of raising it.
    /// </remarks>
    public static bool SignalExisting(string className = TrayClassName)
    {
        var hwnd = NativeMethods.FindWindowW(className, null);
        if (hwnd == 0) return false;
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid != 0)
            NativeMethods.AllowSetForegroundWindow(pid);
        return NativeMethods.PostMessageW(hwnd, ShowRequestMessage, 0, 0);
    }

    /// <summary>Release the mutex handle (tests only; the process claim is never released).</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) NativeMethods.CloseHandle(handle);
    }
}
