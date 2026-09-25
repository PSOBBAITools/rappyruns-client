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

    /// <summary>
    /// A log file past this is left over from the Lisp client (which never
    /// rotated) or from renames refused for a long time: far over what
    /// <see cref="MaxBytes"/> rotation keeps. <see cref="CompactOversized"/> cuts it down.
    /// </summary>
    public const long OldCleanupBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Startup cleanup (C#, S42): trigger-log.txt and <see cref="OldPath"/>, each
    /// when over <paramref name="limit"/>, are cut to their newest lines (the
    /// rotation size's worth, from a line start), so a Lisp-era log of
    /// hundreds of MB stops taking the disk without losing the lines of the
    /// last session. Call it before <see cref="Start"/>: the live file is only
    /// touched while no stream is open. Returns one line per file handled, for
    /// the client log (what was cut, or why it could not be).
    /// </summary>
    public IReadOnlyList<string> CompactOversized(long limit = OldCleanupBytes)
    {
        var report = new List<string>();
        lock (_gate)
        {
            if (_stream is null) Compact(Path, limit, report);
            Compact(OldPath, limit, report);
        }
        return report;
    }

    private void Compact(string path, long limit, List<string> report)
    {
        var name = System.IO.Path.GetFileName(path);
        var tmp = path + ".tmp";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= limit) return;
            var size = info.Length;
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Start at a line: from one byte before the cut, skip to just
                // past the next newline (a cut on a line start keeps that line).
                // The marker and the kept tail together stay under the rotation limit.
                var marker = Encoding.UTF8.GetBytes($"=== trigger log cut {TimeOfDay()}; older lines were dropped ===\n");
                var cut = Math.Max(0, size - Math.Max(0, maxBytes - marker.Length));
                if (cut > 0)
                {
                    source.Seek(cut - 1, SeekOrigin.Begin);
                    int b;
                    while ((b = source.ReadByte()) >= 0 && b != '\n')
                    {
                    }
                    // No newline in the whole tail: keep the raw tail rather than nothing.
                    if (b < 0) source.Seek(cut, SeekOrigin.Begin);
                }
                // Say so at the top, as a rotation does, so the file's start is not taken for its history.
                target.Write(marker);
                source.CopyTo(target);
            }
            File.Move(tmp, path, overwrite: true);
            report.Add(FormattableString.Invariant(
                $"trigger log: cut {name} from {size / (1024 * 1024)} MiB to its newest {new FileInfo(path).Length / 1024} KiB"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            report.Add($"trigger log: could not cut {name}: {e.Message}");
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
