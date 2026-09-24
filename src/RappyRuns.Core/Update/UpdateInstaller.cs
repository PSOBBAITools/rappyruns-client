using System.IO.Compression;

namespace RappyRuns.Core.Update;

/// <summary>An update step failed; nothing (or, after a rollback, nothing that matters) changed.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A verified update extracted to the stage folder.</summary>
/// <param name="StageDir">Where the zip was extracted.</param>
/// <param name="NewExePath">The staged RappyRunsClient.exe.</param>
public sealed record StagedUpdate(string StageDir, string NewExePath);

/// <summary>An update placed into the install folder, not yet launched.</summary>
/// <param name="RunningExe">The exe that was running (now moved to <see cref="OldExe"/>).</param>
/// <param name="OldExe"><see cref="RunningExe"/> + ".old": the rollback copy.</param>
/// <param name="TargetExe">&lt;installDir&gt;\RappyRunsClient.exe, the new build.</param>
/// <param name="InstallDir">The install folder.</param>
/// <param name="TargetBackup">
/// When the running exe had another name and a RappyRunsClient.exe already existed,
/// that file moved aside here (<see cref="TargetExe"/> + ".old") so a rollback can put
/// it back; null otherwise.
/// </param>
public sealed record InstalledUpdate(string RunningExe, string OldExe, string TargetExe, string InstallDir,
    string? TargetBackup = null);

/// <summary>
/// The in-process replacement for the Lisp PowerShell helper (<c>updater-script-text</c>,
/// spec core §10.5): a running exe on Windows can be renamed but not overwritten, so
/// the client moves itself to "&lt;exe&gt;.old", places the new exe under the canonical
/// name, merge-copies <c>data\*</c> and (best effort) <c>ffmpeg\*</c>, starts the new
/// exe without arguments and exits. Handles zips from both the C# and the Lisp
/// pipelines (Compress-Archive writes '\' separators), so a downgrade to a Lisp build
/// installs the same way. Pure file operations; the process launch lives in
/// RappyRuns.Win.Update.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into a fresh <paramref name="stageDir"/> and
    /// verifies it carries RappyRunsClient.exe at its root — all BEFORE touching the
    /// install, so a bad download can never break the existing client. Entry names
    /// with '\' separators are treated as paths; entries escaping the stage folder are
    /// refused. Throws <see cref="UpdateException"/>.
    /// </summary>
    public static StagedUpdate Stage(string zipPath, string stageDir)
    {
        try
        {
            if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);
            Directory.CreateDirectory(stageDir);
            var root = Path.GetFullPath(stageDir);
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.Length == 0) continue;
                    var isDirectory = name.EndsWith('/');
                    var relative = name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
                    if (relative.Length == 0) continue;
                    var destination = Path.GetFullPath(Path.Combine(root, relative));
                    if (!destination.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                        throw new UpdateException($"zip entry escapes the stage folder: {entry.FullName}");
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }
            var newExe = Path.Combine(stageDir, UpdateConstants.ExeName);
            if (!File.Exists(newExe)) throw new UpdateException($"no {UpdateConstants.ExeName} in the update zip");
            return new StagedUpdate(stageDir, newExe);
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateException($"could not extract the update: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Swaps the staged build in: moves <paramref name="runningExe"/> to "&lt;exe&gt;.old"
    /// (retrying <paramref name="moveAttempts"/> times, <paramref name="retryDelay"/> apart,
    /// as a file of the running image may linger locked), copies the new exe to
    /// &lt;installDir&gt;\RappyRunsClient.exe, merge-copies stage\data into
    /// &lt;installDir&gt;\data (overwrite, never delete), and stage\ffmpeg likewise but
    /// best effort (a leftover recorder may hold ffmpeg.exe). Any failure after the move
    /// rolls back (<see cref="Rollback"/>) and throws <see cref="UpdateException"/>.
    /// The running exe is never deleted, only moved.
    /// </summary>
    public static InstalledUpdate Install(StagedUpdate staged, string runningExe, string installDir,
        Action<string>? log = null, int moveAttempts = 10, TimeSpan? retryDelay = null)
    {
        var old = runningExe + UpdateConstants.OldSuffix;
        var target = Path.Combine(installDir, UpdateConstants.ExeName);
        // A running exe of another name leaves an existing RappyRunsClient.exe in
        // place: it moves aside too, or a rollback would delete a file this
        // update never owned.
        var sameName = string.Equals(Path.GetFullPath(runningExe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        var installed = new InstalledUpdate(runningExe, old, target, installDir,
            sameName ? null : target + UpdateConstants.OldSuffix);
        var moved = false;
        for (var i = 0; i < moveAttempts && !moved; i++)
        {
            try
            {
                File.Move(runningExe, old, overwrite: true);
                moved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"move attempt {i + 1} failed: {ex.Message}");
                if (i + 1 < moveAttempts) Thread.Sleep(retryDelay ?? TimeSpan.FromSeconds(1));
            }
        }
        if (!moved) throw new UpdateException("could not move the old exe aside");
        try
        {
            if (installed.TargetBackup is { } backup)
            {
                if (File.Exists(target)) File.Move(target, backup, overwrite: true);
                else installed = installed with { TargetBackup = null };
            }
            File.Copy(staged.NewExePath, target, overwrite: true);
            var newData = Path.Combine(staged.StageDir, "data");
            if (Directory.Exists(newData)) MergeCopy(newData, Path.Combine(installDir, "data"));
            var newFfmpeg = Path.Combine(staged.StageDir, "ffmpeg");
            if (Directory.Exists(newFfmpeg))
            {
                try
                {
                    MergeCopy(newFfmpeg, Path.Combine(installDir, "ffmpeg"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"ffmpeg update skipped: {ex.Message}");
                }
            }
            return installed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"update failed: {ex.Message}");
            Rollback(installed);
            throw new UpdateException($"update failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Restores "&lt;exe&gt;.old" as the running exe's original name, first removing the
    /// half-installed new exe (whether it has the same or a different name — the Lisp
    /// script left a partially copied same-name exe in place, which then blocked the
    /// rollback). A RappyRunsClient.exe that existed beside a differently named running
    /// exe is restored from <see cref="InstalledUpdate.TargetBackup"/>, never lost. Also
    /// used when launching the new exe fails. Best effort, never throws.
    /// </summary>
    public static bool Rollback(InstalledUpdate installed)
    {
        try
        {
            bool restored;
            if (File.Exists(installed.OldExe))
            {
                if (File.Exists(installed.TargetExe)) File.Delete(installed.TargetExe);
                if (File.Exists(installed.RunningExe)) File.Delete(installed.RunningExe);
                File.Move(installed.OldExe, installed.RunningExe);
                restored = true;
            }
            else
            {
                restored = File.Exists(installed.RunningExe);
            }
            if (installed.TargetBackup is { } backup && File.Exists(backup))
            {
                if (File.Exists(installed.TargetExe)) File.Delete(installed.TargetExe);
                File.Move(backup, installed.TargetExe);
            }
            return restored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes the zip and the stage folder after a successful hand-over (best effort).</summary>
    public static void Cleanup(string zipPath, string stageDir)
    {
        UpdateFiles.TryDelete(zipPath);
        UpdateFiles.TryDeleteDirectory(stageDir);
    }

    /// <summary>
    /// <c>Copy-Item src\* dest -Recurse -Force</c>: every file of <paramref name="source"/>
    /// lands in <paramref name="destination"/> (overwriting), folders merge, and files
    /// only in the destination stay.
    /// </summary>
    public static void MergeCopy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            MergeCopy(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
