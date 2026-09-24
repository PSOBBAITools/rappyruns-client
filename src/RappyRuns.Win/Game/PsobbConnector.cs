using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RappyRuns.Core.Game;
using static RappyRuns.Win.Game.NativeMethods;

namespace RappyRuns.Win.Game;

/// <summary>
/// Finding and attaching to the PSOBB process (win32.lisp:222-635,
/// main.lisp:52/237 - spec core §11): window search by exact title, a
/// minimal-rights OpenProcess, the 2-window candidate choice and the
/// Authenticode gate. Called by the poll thread once a second while unattached.
/// </summary>
public sealed unsafe class PsobbConnector(Action<string>? log = null)
{
    private (int Pid, int Error)? _openProcessLastLog;

    /// <summary>The current Authenticode rejection (shown in the status line), or null.</summary>
    public PsobbRejection? Rejection { get; private set; }

    /// <summary>win32.lisp:222 find-psobb-window: FindWindowW for each known title (the cheap idle guard).</summary>
    public static nint FindPsobbWindow()
    {
        foreach (var name in PsobbAttach.WindowNames)
        {
            var hwnd = FindWindow(null, name);
            if (hwnd != 0) return hwnd;
        }
        return 0;
    }

    /// <summary>win32.lisp:228 window-title: GetWindowTextW (256 chars), null when empty.</summary>
    public static string? WindowTitle(nint hwnd)
    {
        const int maxCount = 256;
        var buffer = stackalloc char[maxCount];
        var length = GetWindowText(hwnd, buffer, maxCount);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    private static int? HwndPid(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid > 0 ? (int)pid : null;
    }

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint hwnd, nint lParam)
    {
        try
        {
            var found = (List<(nint, int)>)GCHandle.FromIntPtr(lParam).Target!;
            var title = WindowTitle(hwnd);
            if (title is not null && PsobbAttach.WindowNames.Contains(title, StringComparer.Ordinal) && HwndPid(hwnd) is { } pid)
                found.Add((hwnd, pid));
        }
        catch
        {
            // A callback must never throw across the native frame.
        }
        return 1; // keep enumerating
    }

    /// <summary>
    /// win32.lisp:267 find-psobb-windows: every top-level PSOBB window as
    /// (hwnd, pid). Enumerates only once FindWindow saw one (2-window play:
    /// shop + play); if enumeration fails, the FindWindow hit alone.
    /// </summary>
    public static List<(nint Hwnd, int Pid)> FindPsobbWindows()
    {
        var primary = FindPsobbWindow();
        if (primary == 0) return [];
        var found = new List<(nint, int)>();
        var handle = GCHandle.Alloc(found);
        try
        {
            if (EnumWindows(&EnumCallback, GCHandle.ToIntPtr(handle)) && found.Count > 0) return found;
        }
        finally
        {
            handle.Free();
        }
        return HwndPid(primary) is { } p ? [(primary, p)] : [];
    }

    /// <summary>win32.lisp:295 open-reader-for: OpenProcess(VM_READ | QUERY_INFORMATION), or null with a de-duplicated log line.</summary>
    public LiveReader? OpenReaderFor(nint hwnd, int pid)
    {
        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, (uint)pid);
        if (handle != 0)
        {
            _openProcessLastLog = null;
            return new LiveReader(handle, pid, WindowTitle(hwnd));
        }
        // GetLastError first, before anything else clobbers it.
        var err = Marshal.GetLastPInvokeError();
        if (_openProcessLastLog != (pid, err))
        {
            _openProcessLastLog = (pid, err);
            log?.Invoke(string.Format(CultureInfo.InvariantCulture, "OpenProcess failed: pid {0} err {1} ({2}){3}", pid, err,
                GameFrameProcessor.Win32ErrorLabel(err), err == 5 ? " - run the client as administrator" : ""));
        }
        return null;
    }

    /// <summary>win32.lisp:578 reader-signature-trusted-p: file-based Authenticode only - reads no process memory.</summary>
    public static bool SignatureTrusted(LiveReader reader)
    {
        var path = reader.ImagePath;
        var (status, signer) = path is not null ? Authenticode.Verify(path) : (SignatureStatus.Invalid, null);
        return PsobbTables.SignatureTrusted(status, signer);
    }

    /// <summary>
    /// win32.lisp:624 open-psobb-reader: attach to a running PSOBB, or null.
    /// With several windows a trusted instance running a quest wins; an
    /// untrusted candidate's memory is never read.
    /// </summary>
    public LiveReader? OpenPsobbReader()
    {
        var candidates = FindPsobbWindows();
        if (candidates.Count == 0) return null;
        if (candidates.Count > 1)
            log?.Invoke(string.Format(CultureInfo.InvariantCulture, "multiple PSOBB windows: {0} (pids {1})",
                candidates.Count, string.Join(", ", candidates.Select(c => c.Pid))));
        var readers = candidates.Select(c => OpenReaderFor(c.Hwnd, c.Pid)).OfType<LiveReader>().ToList();
        var trustedCount = 0;
        var chosen = PsobbAttach.Choose(readers, r =>
        {
            var ok = SignatureTrusted(r);
            if (ok) trustedCount++;
            return ok;
        }, r => r.QuestLoaded, r => r.Dispose());
        if (chosen is not null && readers.Count > 1)
            log?.Invoke(string.Format(CultureInfo.InvariantCulture, "selected PSOBB pid {0} of {1} windows ({2} trusted)",
                chosen.Pid, readers.Count, trustedCount));
        return chosen;
    }

    /// <summary>
    /// The attach half of main.lisp:237 poll-search-step: open a candidate and
    /// refuse anything but the signed official client (no attach = no
    /// detection, recording or submission). Keeps <see cref="Rejection"/> for
    /// the status line and clears it once the rejected game's window is gone.
    /// On success the caller sets the audio target pid (<see cref="LiveReader.Pid"/>),
    /// the Pin Share game exe (<see cref="LiveReader.ImagePath"/>) and calls
    /// <see cref="GameFrameProcessor.Attach"/>.
    /// </summary>
    public LiveReader? TryAttach()
    {
        var reader = OpenPsobbReader();
        if (reader is not null)
        {
            var rejection = PsobbAttach.TrustRejection(reader.Pid, Rejection, () => reader.ImagePath, Authenticode.Verify);
            Rejection = rejection;
            if (rejection is not null)
            {
                reader.Dispose();
                return null;
            }
            return reader;
        }
        // The rejected process went away: back to plain searching.
        if (Rejection is not null && FindPsobbWindow() == 0) Rejection = null;
        return null;
    }
}
