using System.Globalization;
using System.Text;

namespace RappyRuns.Core.Media;

/// <summary>
/// The capture diagnostics log, <c>%TEMP%\ephinea-ta-recording.log</c>
/// (media spec §1.12, recording.lisp:349-452). Every capture decision is
/// written here and its tail ships to the server with each uploaded video, so
/// the line formats are kept verbatim: other modules (attach, overlay, Pin
/// Share) write through <see cref="Write"/> too, as they did through the Lisp
/// <c>recording-log</c>.
/// </summary>
public static class RecordingLog
{
    /// <summary>+recording-log-max-bytes+: rotate past 1 MiB into <c>.old</c>.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>+diagnostics-tail-chars+.</summary>
    public const int DiagnosticsTailChars = 65536;

    private static readonly object Gate = new();

    /// <summary>
    /// Redirect the log (null = the real %TEMP% path): tests, and the app's
    /// isolated dev mode (RAPPYRUNS_CONFIG_DIR) so a side-by-side dev client
    /// never writes into the installed client's log.
    /// </summary>
    public static string? PathOverride { get; set; }

    /// <summary>
    /// <c>recording-log-path</c>: resolved from Windows at CALL time. v0.41.0
    /// shipped the build machine's temp path baked in and no field log was ever
    /// written; <see cref="System.IO.Path.GetTempPath"/> asks at run time.
    /// </summary>
    public static string Path => PathOverride ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ephinea-ta-recording.log");

    /// <summary>The rotated generation: same name, type "old".</summary>
    public static string OldPath => System.IO.Path.ChangeExtension(Path, ".old");

    /// <summary>
    /// <c>recording-log</c>: append <c>MM-DD HH:MM:SS message</c> (local time).
    /// Never throws. Lines end in CRLF and any newline inside the message
    /// becomes CRLF, because the LispWorks file stream writes Windows line
    /// endings (verified on a field log) - the diagnostics reader normalizes
    /// them back (<see cref="FileTail"/>).
    /// </summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Rotate();
                var now = DateTime.Now;
                var line = now.ToString("MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) +
                           message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) +
                           "\r\n";
                File.AppendAllText(Path, line, new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // Diagnostics must never break the caller.
        }
    }

    /// <summary><c>rotate-recording-log</c>: move an oversized log aside, overwriting the previous .old.</summary>
    private static void Rotate()
    {
        var info = new FileInfo(Path);
        if (info.Exists && info.Length > MaxBytes)
            File.Move(Path, OldPath, overwrite: true);
    }

    /// <summary>
    /// <c>file-tail</c> (recording.lisp:410): the last <paramref name="maxChars"/>
    /// characters of a UTF-8 text file, or null when missing, unreadable or empty. CRLF is
    /// read back as a single newline, as the LispWorks stream does.
    /// </summary>
    public static string? FileTail(string path, int maxChars)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            // An empty file yields NIL in the Lisp (nothing was ever kept):
            // an empty gdigrab probe stderr therefore reads as :failed.
            if (text.Length == 0) return null;
            return text.Length > maxChars ? text[^maxChars..] : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The startup "session:" line (<c>log-session-info</c>, ffmpeg-win32.lisp:786)
    /// that identifies the client build and hardware class in any log tail.
    /// </summary>
    public static string SessionLine(string clientVersion, MachineInfo machine, string? ffmpegPath) =>
        string.Create(CultureInfo.InvariantCulture,
            $"session: client {clientVersion}, {Lisp.Opt(machine.SoftwareType)} {Lisp.Opt(machine.SoftwareVersion)}, ram-gb {Lisp.Opt(machine.RamGb)}, cores {machine.LogicalProcessors}, ffmpeg {Lisp.Opt(ffmpegPath)}");

    /// <summary>
    /// <c>diagnostics-report</c> (recording.lisp:430): the body sent to
    /// <c>POST /api/runs/:id/diagnostics</c>. The server may parse it, so the
    /// shape - including Lisp's T/NIL - is kept exactly. Never throws.
    /// </summary>
    public static string DiagnosticsReport(string clientVersion, MachineInfo machine, HwEncoderStatus hw)
    {
        string? path = null;
        bool exists = false;
        string? tail = null;
        try
        {
            path = Path;
            exists = File.Exists(path);
            tail = FileTail(path, DiagnosticsTailChars);
        }
        catch (Exception)
        {
            // Every field degrades to NIL text.
        }
        var cores = machine.LogicalProcessors.ToString(CultureInfo.InvariantCulture);
        return $"client {clientVersion}\nos {Lisp.Opt(machine.SoftwareType)} {Lisp.Opt(machine.SoftwareVersion)}\n" +
               $"ram-gb {Lisp.Opt(machine.RamGb)} cores {cores}\n" +
               $"hw-encoder {Lisp.Opt(hw.Encoder)} gpu-chain {Lisp.Bool(hw.GpuChain)} low-memory {Lisp.Bool(machine.LowMemory)}\n" +
               $"--- recording log tail ({Lisp.Opt(path)}, exists {Lisp.Bool(path is not null && exists)}) ---\n" +
               (tail ?? "(no recording log)");
    }
}
