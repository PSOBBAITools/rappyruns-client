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

    // The stream position past which Writer closes the stream and checks the file for rotation.
    private long _checkAt;

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

    // trigger-log.lisp:34: opened on first use, kept open until closed (or
    // until Writer closes it to check for rotation).
    private StreamWriter Stream()
    {
        if (_stream is null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _stream = new StreamWriter(file, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        }
        return _stream;
    }

    /// <summary>
    /// The stream for the next lines, rotating first when the file is past
    /// the limit, so the lines about to be written (a session header included)
    /// land together in the fresh file. One path does it: once the open
    /// stream's position (every write flushes, so it tracks the file) passes
    /// <c>_checkAt</c>, the stream closes; opening then looks at the
    /// file on disk, moves it aside when it is over the limit, and starts the
    /// fresh file with a line saying where the earlier lines went. When
    /// Windows refuses the rename, the log keeps appending to the big file and
    /// tries again after another limit's worth of growth, not on every write.
    /// </summary>
    private StreamWriter Writer()
    {
        if (_stream is not null && _stream.BaseStream.Position > _checkAt) CloseStream();
        if (_stream is not null) return _stream;
        var existing = new FileInfo(Path);
        var rotated = existing.Exists && existing.Length > maxBytes && MoveAside();
        var stream = Stream();
        var size = stream.BaseStream.Position;
        _checkAt = size <= maxBytes ? maxBytes : size + maxBytes;
        if (rotated)
        {
            stream.Write($"=== trigger log rotated {TimeOfDay()}; earlier lines are in {System.IO.Path.GetFileName(OldPath)} ===\n");
            stream.Flush();
        }
        return stream;
    }

    /// <summary>Rename the file to <see cref="OldPath"/>, replacing it. False when Windows refuses (the old file open elsewhere without delete sharing, say).</summary>
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
            var stream = Writer();
            // ~& - a fresh line: the stream is only ever left at a line start here.
            stream.Write($"=== trigger logging started {TimeOfDay()} ===\n");
            stream.Write("Play the segment; each register / floor-switch change is listed below.\n");
            stream.Flush();
        }
        return Path;
    }

    /// <summary>trigger-log.lisp:43 close-trigger-log (logging turned off, or shutdown).</summary>
    public void Close()
    {
        lock (_gate) CloseStream();
    }

    private void CloseStream()
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

    public void Dispose() => Close();

    /// <summary>
    /// trigger-log.lisp:310 log-trigger-changes: append the diffs between two
    /// consecutive snapshots of the same loaded quest (both named, same quest
    /// pointer). Returns the number of lines written, or null when the frames
    /// are not comparable. A frame with no changes does not touch the file (the
    /// Lisp opened it anyway), so it cannot reopen or rotate a log the UI just
    /// closed.
    /// </summary>
    public int? LogChanges(Snapshot? previous, Snapshot? snapshot)
    {
        var lines = ChangeLines(previous, snapshot, TimeOfDay());
        if (lines is null) return null;
        if (lines.Count == 0) return 0;
        lock (_gate)
        {
            var stream = Writer();
            foreach (var line in lines) stream.Write(line + "\n");
            stream.Flush();
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
