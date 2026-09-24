namespace RappyRuns.Core.Update;

/// <summary>
/// File-system side of the self-updater: well-known paths, the downloaded-zip sanity
/// check, the writability probe and the startup sweep (updater.lisp, spec core §10.4-10.5).
/// Paths are resolved at call time, never cached.
/// </summary>
public static class UpdateFiles
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04]; // "PK\3\4"

    /// <summary>
    /// <c>windows-temp-dir</c> (updater.lisp:296): %TEMP%, else %TMP%, else the user's
    /// home folder, exactly as the Lisp resolves it. TEMP comes first (unlike
    /// <see cref="Path.GetTempPath"/>, which asks TMP first) so the C# client and the
    /// Lisp bridge updater agree on the startup marker and the update leftovers.
    /// <para>Contract paths by resolution: this one - the startup marker, the update
    /// zip, the stage folder and the Lisp helper script (all <c>windows-temp-dir</c> in
    /// Lisp). <see cref="Path.GetTempPath"/> - the recording log and the gdigrab probe's
    /// stderr (<c>hcl:get-temp-directory</c> in Lisp).</para>
    /// </summary>
    public static string TempDir() => TempDir(Environment.GetEnvironmentVariable);

    /// <summary><see cref="TempDir()"/> over a given environment (tests).</summary>
    public static string TempDir(Func<string, string?> getenv)
    {
        var temp = getenv("TEMP");
        if (!string.IsNullOrEmpty(temp)) return temp;
        var tmp = getenv("TMP");
        if (!string.IsNullOrEmpty(tmp)) return tmp;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : home;
    }

    /// <summary><c>update-zip-path</c>: %TEMP%\RappyRunsClient-update.zip.</summary>
    public static string ZipPath(string? tempDir = null) => Path.Combine(tempDir ?? TempDir(), UpdateConstants.ZipFileName);

    /// <summary>The extraction folder, %TEMP%\rappyruns-update-stage.</summary>
    public static string StageDir(string? tempDir = null) => Path.Combine(tempDir ?? TempDir(), UpdateConstants.StageDirName);

    /// <summary>+rejected-update-name+: where the bridge update helper records a rolled-back tag (config folder).</summary>
    public const string RejectedFileName = "update-rejected.txt";

    /// <summary>+rejected-update-days+: how long a rolled-back release is skipped by the automatic pass.</summary>
    public const int RejectedDays = 3;

    /// <summary>
    /// <c>rejected-update-tag</c> (updater.lisp:323): the release tag written to
    /// <c>&lt;configDir&gt;\update-rejected.txt</c> by the update helper that had to
    /// roll it back, when the file is younger than <see cref="RejectedDays"/> days;
    /// else null. The first line, trimmed of spaces, tabs, line ends and a BOM.
    /// Never throws.
    /// </summary>
    public static string? RejectedTag(string configDir, DateTime? utcNow = null)
    {
        try
        {
            var path = Path.Combine(configDir, RejectedFileName);
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if ((utcNow ?? DateTime.UtcNow) - info.LastWriteTimeUtc >= TimeSpan.FromDays(RejectedDays)) return null;
            using var reader = new StreamReader(path, System.Text.Encoding.UTF8);
            var tag = reader.ReadLine()?.Trim(' ', '\t', '\r', '\n', '﻿');
            return string.IsNullOrEmpty(tag) ? null : tag;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>valid-update-zip-p</c>: a cheap corruption check — the byte size the API
    /// promised (skipped when <paramref name="expectedSize"/> is null) and the PK
    /// local-file-header magic, so an HTML error page never gets installed. No
    /// signature or hash check exists (none did in Lisp either). Missing file → false.
    /// </summary>
    public static bool IsValidZip(string path, long? expectedSize)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (expectedSize is { } size && file.Length != size) return false;
            var magic = new byte[4];
            return file.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 && magic.AsSpan().SequenceEqual(ZipMagic);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>install-dir-writable-p</c>: whether <paramref name="installDir"/> accepts a write
    /// (it will not under Program Files without elevation), by creating and deleting
    /// eta-write-probe.tmp. Checked before downloading so the failure mode is the manual
    /// download hint, not a half-applied update.
    /// </summary>
    public static bool IsDirWritable(string installDir)
    {
        var probe = Path.Combine(installDir, UpdateConstants.WriteProbeName);
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>cleanup-old-update-files</c> (spec core §10.5 step 5, §10.7 #7): delete
    /// &lt;installDir&gt;\RappyRunsClient.exe.old (plus <paramref name="extraOldExeNames"/>
    /// ".old" files, e.g. a pre-rename exe name) and the temp leftovers (the Lisp helper
    /// script, the zip, the stage folder). Every step is best effort. Returns true when
    /// no .old exe remains — false means it is still locked (the previous process has
    /// not exited yet); see <see cref="CleanupOldUpdateFilesAsync"/>.
    /// </summary>
    public static bool CleanupOldUpdateFiles(string installDir, string? tempDir = null,
        IEnumerable<string>? extraOldExeNames = null)
    {
        var temp = tempDir ?? TempDir();
        TryDelete(Path.Combine(temp, UpdateConstants.LegacyScriptName));
        TryDelete(Path.Combine(temp, UpdateConstants.ZipFileName));
        TryDeleteDirectory(Path.Combine(temp, UpdateConstants.StageDirName));
        var clean = true;
        foreach (var name in new[] { UpdateConstants.ExeName }.Concat(extraOldExeNames ?? []))
        {
            var old = Path.Combine(installDir, name + UpdateConstants.OldSuffix);
            clean &= TryDelete(old);
        }
        return clean;
    }

    /// <summary>
    /// The exe names (without ".old") of every "*.exe.old" in <paramref name="installDir"/>:
    /// <see cref="UpdateInstaller.Install"/> moves the RUNNING exe to "&lt;its name&gt;.old",
    /// and a client started under another name (a renamed or pre-rename exe) comes back
    /// as RappyRunsClient.exe, which cannot know the old name. Deviation from the Lisp
    /// sweep (RappyRunsClient.exe.old only), which left such files behind.
    /// </summary>
    public static IReadOnlyList<string> OldExeNamesIn(string installDir)
    {
        try
        {
            return Directory.EnumerateFiles(installDir, "*.exe" + UpdateConstants.OldSuffix)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.EndsWith(".exe" + UpdateConstants.OldSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(n => n[..^UpdateConstants.OldSuffix.Length])
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// <see cref="CleanupOldUpdateFiles"/> retried while the .old exe stays locked. The
    /// C# updater starts the new exe before the old process has exited (the Lisp helper
    /// waited for the exit first), so the first sweep can find the old image still
    /// mapped; retry for up to <paramref name="attempts"/> × <paramref name="interval"/>.
    /// </summary>
    public static async Task<bool> CleanupOldUpdateFilesAsync(string installDir, string? tempDir = null,
        IEnumerable<string>? extraOldExeNames = null, int attempts = 30, TimeSpan? interval = null,
        CancellationToken cancellationToken = default)
    {
        var names = extraOldExeNames?.ToList();
        for (var i = 0; ; i++)
        {
            if (CleanupOldUpdateFiles(installDir, tempDir, names)) return true;
            if (i + 1 >= attempts) return false;
            await Task.Delay(interval ?? TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes a file; true when it no longer exists.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes a directory tree, best effort.</summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next startup sweeps again.
        }
    }
}
