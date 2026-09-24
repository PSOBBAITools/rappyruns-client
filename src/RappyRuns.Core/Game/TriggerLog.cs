using System.Globalization;
using System.Text;

namespace RappyRuns.Core.Game;

/// <summary>
/// Trigger discovery log (trigger-log.lisp): while enabled, every quest
/// register / floor-switch change and monster kill between consecutive frames
/// of one loaded quest is appended to trigger-log.txt. The path and the line
/// format are a <b>contract</b> (spec core §16.1): moderators follow
/// instructions (quest-triggers.sexp comments) that name this file and read
/// these lines. UTF-8, append-only. The Lisp never rotated it (469 MB seen on a
/// dev machine; the toggle survives restarts), so past <see cref="MaxBytes"/>
/// the file moves aside to <see cref="OldPath"/> and a fresh one starts. The UI
/// thread (toggle) and the poll thread (diffs) both write, so every file access
/// holds a lock.
/// </summary>
public sealed class TriggerLog(string path, IGameClock clock, long maxBytes = TriggerLog.MaxBytes) : IDisposable
{
    /// <summary>Rotation threshold. 8 MiB is hours of dense register traffic, far more than one segment needs.</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    private readonly Lock _gate = new();
    private StreamWriter? _stream;

    /// <summary>%APPDATA%\ephinea-ta-client\trigger-log.txt (the config directory; home when APPDATA is unset).</summary>
    public static string DefaultPath()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var dir = string.IsNullOrEmpty(appData) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : appData;
        return System.IO.Path.Combine(dir, "ephinea-ta-client", "trigger-log.txt");
    }

    public string Path { get; } = path;

    /// <summary>The rotated generation: trigger-log.txt becomes trigger-log.old.txt (one generation, replaced).</summary>
    public string OldPath => System.IO.Path.ChangeExtension(Path, ".old.txt");

    // trigger-log.lisp:34: opened on first use, kept open until closed. A file
    // an earlier session left oversized is rotated before it is reopened.
    private StreamWriter Stream()
    {
        if (_stream is null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            var existing = new FileInfo(Path);
            if (existing.Exists && existing.Length > maxBytes) MoveAside();
            var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _stream = new StreamWriter(file, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        }
        return _stream;
    }

    /// <summary>
    /// After a flush: once the open file has grown past the limit, close it,
    /// move it aside and start the fresh file with a line saying where the
    /// earlier lines went, so a moderator reading it mid-segment is not left
    /// with a log that begins in the middle of nothing.
    /// </summary>
    private void RotateIfFull()
    {
        if (_stream is null || _stream.BaseStream.Length <= maxBytes) return;
        _stream.Dispose();
        _stream = null;
        if (!MoveAside()) return;
        var stream = Stream();
        stream.Write($"=== trigger log rotated {TimeOfDay()}; earlier lines are in {System.IO.Path.GetFileName(OldPath)} ===\n");
        stream.Flush();
    }

    /// <summary>
    /// Rename the file to <see cref="OldPath"/>, replacing it. False when
    /// Windows refuses (the old file is open without delete sharing, say):
    /// the log then keeps appending to the big file rather than stop.
    /// </summary>
    private bool MoveAside()
    {
        try
        {
            File.Move(Path, OldPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>trigger-log.lisp:60 time-of-day: local HH:MM:SS.</summary>
    private string TimeOfDay() => clock.LocalNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// trigger-log.lisp:49 start-trigger-log: open (creating) the file and write
    /// the session header at once, so it exists the moment logging is enabled.
    /// </summary>
    public string Start()
    {
        lock (_gate)
        {
            var stream = Stream();
            // ~& - a fresh line: the stream is only ever left at a line start here.
            stream.Write($"=== trigger logging started {TimeOfDay()} ===\n");
            stream.Write("Play the segment; each register / floor-switch change is listed below.\n");
            stream.Flush();
            RotateIfFull();
        }
        return Path;
    }

    /// <summary>trigger-log.lisp:43 close-trigger-log (logging turned off, or shutdown).</summary>
    public void Close()
    {
        lock (_gate)
        {
            try
            {
                _stream?.Dispose();
            }
            catch (IOException)
            {
                // ignore-errors, like the Lisp.
            }
            _stream = null;
        }
    }

    public void Dispose() => Close();

    /// <summary>
    /// trigger-log.lisp:310 log-trigger-changes: append the diffs between two
    /// consecutive snapshots of the same loaded quest (both named, same quest
    /// pointer). Returns the number of lines written, or null when the frames
    /// are not comparable. Flushes when anything was written.
    /// </summary>
    public int? LogChanges(Snapshot? previous, Snapshot? snapshot)
    {
        var lines = ChangeLines(previous, snapshot, TimeOfDay());
        if (lines is null) return null;
        lock (_gate)
        {
            var stream = Stream();
            foreach (var line in lines) stream.Write(line + "\n");
            if (lines.Count > 0)
            {
                stream.Flush();
                RotateIfFull();
            }
        }
        return lines.Count;
    }

    /// <summary>
    /// The lines <see cref="LogChanges"/> writes, without the file (pure):
    /// registers in id order, then floor switches (byte, then bit), then kills.
    /// <c>"&lt;quest&gt;"</c> is printed like Lisp ~s (quoted, " and \ escaped).
    /// </summary>
    public static List<string>? ChangeLines(Snapshot? previous, Snapshot? snapshot, string stamp)
    {
        if (previous?.QuestName is null || snapshot?.QuestName is null || previous.QuestPtr != snapshot.QuestPtr) return null;
        var quest = LispPrin1String(snapshot.QuestName);
        var lines = new List<string>();
        if (previous.Registers is { } old && snapshot.Registers is { } now)
        {
            for (var id = 0; id < PsobbLayout.RegisterCount; id++)
            {
                var oldValue = MemoryDecode.U16(old, 4 * id);
                var newValue = MemoryDecode.U16(now, 4 * id);
                if (oldValue != newValue)
                    lines.Add(FormattableString.Invariant($"{stamp} {quest} register {id}: {oldValue} -> {newValue}"));
            }
        }
        foreach (var (floor, sw, oldOn, newOn) in RunLogs.FloorSwitchDiffs(previous, snapshot))
            lines.Add(FormattableString.Invariant($"{stamp} {quest} floor {floor} switch {sw}: {(oldOn ? "on" : "off")} -> {(newOn ? "on" : "off")}"));
        foreach (var monster in RunLogs.NewlyKilledMonsters(previous, snapshot))
            lines.Add(FormattableString.Invariant($"{stamp} {quest} monster {monster.Id} killed ({monster.Name ?? "?"}, unitxt {monster.Unitxt ?? 0})"));
        return lines;
    }

    /// <summary>A string as prin1 / ~s writes it: double-quoted, with " and \ backslash-escaped.</summary>
    public static string LispPrin1String(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
