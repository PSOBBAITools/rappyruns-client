using System.ComponentModel;
using System.Diagnostics;
using RappyRuns.Core.Update;

namespace RappyRuns.Win.Update;

/// <summary>
/// The hand-over at the end of a self-update (replaces updater.lisp
/// <c>launch-updater-and-quit</c> and its PowerShell helper, spec core §10.5):
/// stage → swap → release the single-instance guard → start the new exe with no
/// arguments. On success the caller exits immediately; on failure everything is
/// rolled back and the running client simply carries on.
/// </summary>
public static class UpdateLauncher
{
    /// <summary>The running exe (single-file publish: the exe itself).</summary>
    public static string CurrentExePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("no process path");

    /// <summary>The folder of the running exe (Lisp <c>install-dir</c>).</summary>
    public static string InstallDir => Path.GetDirectoryName(CurrentExePath)!;

    /// <summary>
    /// Applies <paramref name="zipPath"/> and starts the new build.
    /// <para>Call it after stopping the poll loop (join up to 10 s so the recorder shuts
    /// down cleanly) — the Lisp client did the same before quitting. Then:</para>
    /// <list type="number">
    /// <item>extract to %TEMP%\rappyruns-update-stage and require RappyRunsClient.exe (nothing touched yet);</item>
    /// <item>move the running exe to "&lt;exe&gt;.old", place the new one as RappyRunsClient.exe, merge data\ and ffmpeg\;</item>
    /// <item><paramref name="releaseSingleInstance"/>: the app MUST release its single-instance
    /// mutex and destroy its message window here, or the new process (C# or a Lisp
    /// downgrade) sees a running instance and exits;</item>
    /// <item>start the new exe with no arguments, working directory = install folder;</item>
    /// <item>delete the zip and stage folder.</item>
    /// </list>
    /// Returns true when the new process started — the caller must then exit at once
    /// (<see cref="Environment.Exit"/>). Returns false after a rollback; <paramref name="log"/>
    /// has the reason. If <paramref name="releaseSingleInstance"/> ran, the caller must
    /// re-acquire it (or restart itself) before continuing.
    /// </summary>
    public static bool ApplyAndRestart(string zipPath, Action releaseSingleInstance, Action<string>? log = null,
        string? runningExe = null, string? stageDir = null)
    {
        var exe = runningExe ?? CurrentExePath;
        var installDir = Path.GetDirectoryName(exe)!;
        var stage = stageDir ?? UpdateFiles.StageDir();
        InstalledUpdate installed;
        try
        {
            var staged = UpdateInstaller.Stage(zipPath, stage);
            installed = UpdateInstaller.Install(staged, exe, installDir, log);
        }
        catch (UpdateException ex)
        {
            log?.Invoke(ex.Message);
            UpdateFiles.TryDeleteDirectory(stage);
            return false;
        }
        releaseSingleInstance();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(installed.TargetExe)
            {
                WorkingDirectory = installDir,
                UseShellExecute = true,
            });
            if (process is null) throw new InvalidOperationException("the new client did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            log?.Invoke($"update failed: {ex.Message}");
            UpdateInstaller.Rollback(installed);
            return false;
        }
        UpdateInstaller.Cleanup(zipPath, stage);
        return true;
    }
}
