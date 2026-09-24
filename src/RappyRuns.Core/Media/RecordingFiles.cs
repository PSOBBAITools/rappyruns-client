using System.Globalization;
using System.Security.Cryptography;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Media;

/// <summary><c>default-record-dir-choice</c>'s verdict.</summary>
public enum RecordDirChoice
{
    UseNew,
    Migrate,
}

/// <summary>
/// Recording file names and locations (media spec §1.11, recording.lisp:456-565).
/// </summary>
public static class RecordingFiles
{
    /// <summary>
    /// <c>sanitize-filename</c>: <c>\/:*?"&lt;&gt;|</c> and control characters
    /// (below U+0020) become dashes.
    /// </summary>
    public static string SanitizeFilename(string value) =>
        string.Create(value.Length, value, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                span[i] = c < ' ' || "\\/:*?\"<>|".Contains(c) ? '-' : c;
            }
        });

    /// <summary>
    /// <c>recording-token</c>: 8 lowercase hex digits of a 32-bit random drawn
    /// fresh per process. A frozen sequence once made two instances write the
    /// same rec-tmp file at once and corrupt it (runs 418-424).
    /// </summary>
    public static string RecordingToken()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes).ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>A file or directory exists at the path (Lisp probe-file).</summary>
    public static bool PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>deduplicate-path</c> (recording.lisp:524): the path when free, else an
    /// Explorer-style " (2)", " (3)"... before the LAST dot of the whole path.
    /// </summary>
    public static string DeduplicatePath(string path, Func<string, bool>? exists = null)
    {
        exists ??= PathExists;
        if (!exists(path)) return path;
        var dot = path.LastIndexOf('.');
        var stem = dot >= 0 ? path[..dot] : path;
        var ext = dot >= 0 ? path[dot..] : "";
        for (var n = 2; ; n++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){ext}");
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// <c>recording-tmp-path</c> (recording.lisp:543):
    /// <c>rec-tmp-YYYYMMDD-HHMMSS-token.mp4</c> in local time, in the record dir.
    /// </summary>
    public static string RecordingTmpPath(string recordDir, DateTime localNow, string token) =>
        Path.Combine(recordDir, string.Create(CultureInfo.InvariantCulture,
            $"rec-tmp-{localNow:yyyyMMdd}-{localNow:HHmmss}-{token}.mp4"));

    /// <summary>
    /// <c>run-video-filename</c> (recording.lisp:552):
    /// <c>"Towards the Future 9'59.123 (2026-07-04 2130).mp4"</c> - the in-game
    /// quest name (else the slug, else "run"), the run time with unpadded
    /// minutes, and :finished-at (else now) in local time; sanitized.
    /// </summary>
    public static string RunVideoFilename(Plist run, Func<long>? universalNow = null)
    {
        var timeMs = Lisp.GetLong(run, "TIME-MS") ?? 0;
        var totalSeconds = Lisp.Floor(timeMs, 1000);
        var msec = Lisp.Mod(timeMs, 1000);
        var minutes = Lisp.Floor(totalSeconds, 60);
        var seconds = Lisp.Mod(totalSeconds, 60);
        var finished = Lisp.GetLong(run, "FINISHED-AT") ?? (universalNow ?? Lisp.UniversalTimeNow)();
        var at = Lisp.DecodeUniversalTimeLocal(finished);
        var name = Lisp.Princ(run, "QUEST-NAME") ?? Lisp.Princ(run, "QUEST-SLUG") ?? "run";
        return SanitizeFilename(string.Create(CultureInfo.InvariantCulture,
            $"{name} {minutes}'{seconds:00}.{msec:000} ({at:yyyy}-{at:MM}-{at:dd} {at:HH}{at:mm}).mp4"));
    }

    /// <summary>
    /// <c>default-record-dir-choice</c> (recording.lisp:456): keep the new
    /// folder when it exists or there is nothing to migrate; never rename onto
    /// an existing folder.
    /// </summary>
    public static RecordDirChoice DefaultRecordDirChoice(bool oldExists, bool newExists) =>
        newExists || !oldExists ? RecordDirChoice.UseNew : RecordDirChoice.Migrate;

    /// <summary>
    /// <c>resolve-record-dir</c> (recording.lisp:466): the configured folder
    /// (spaces trimmed), else <c>%USERPROFILE%\Videos\RappyRuns\</c>; the first
    /// resolution that finds only the pre-rename <c>Videos\EphineaTA\</c>
    /// renames it, and keeps using the old name when the rename fails. Always
    /// ends in a directory separator (ensure-directory-pathname).
    /// </summary>
    public static string ResolveRecordDir(string? configured, string? userProfile = null)
    {
        var trimmed = (configured ?? "").Trim(' ');
        if (trimmed.Length > 0) return EnsureDirectory(trimmed);
        var home = userProfile ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var old = EnsureDirectory(Path.Combine(home, "Videos", "EphineaTA"));
        var @new = EnsureDirectory(Path.Combine(home, "Videos", "RappyRuns"));
        switch (DefaultRecordDirChoice(Directory.Exists(old), Directory.Exists(@new)))
        {
            case RecordDirChoice.UseNew:
                return @new;
            default:
                try
                {
                    Directory.Move(old.TrimEnd(Path.DirectorySeparatorChar), @new.TrimEnd(Path.DirectorySeparatorChar));
                    return @new;
                }
                catch (Exception)
                {
                    return old;
                }
        }
    }

    /// <summary>
    /// <c>resolve-ffmpeg-path</c> (recording.lisp:488): configured path
    /// (spaces trimmed), else the bundled <c>ffmpeg\ffmpeg.exe</c> next to the
    /// executable when it exists, else bare "ffmpeg.exe" (PATH search).
    /// </summary>
    public static string ResolveFfmpegPath(string? configured, string? exeDirectory = null)
    {
        var trimmed = (configured ?? "").Trim(' ');
        if (trimmed.Length > 0) return trimmed;
        var dir = exeDirectory ?? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var bundled = Path.Combine(dir, "ffmpeg", "ffmpeg.exe");
        return File.Exists(bundled) ? Path.GetFullPath(bundled) : "ffmpeg.exe";
    }

    /// <summary>
    /// <c>stderr-file-for</c> (ffmpeg-win32.lisp:917): a spawned ffmpeg's stderr
    /// sits next to its output, so a remux and the next capture never share one.
    /// </summary>
    public static string StderrFileFor(string outputPath) => outputPath + ".stderr.txt";

    private static string EnsureDirectory(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
