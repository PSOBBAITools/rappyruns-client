namespace RappyRuns.Core.Config;

/// <summary>Which default recordings folder to use (recording.lisp:456 default-record-dir-choice).</summary>
public enum RecordDirChoice
{
    /// <summary>Use Videos\RappyRuns (fresh install, or already migrated).</summary>
    UseNew,

    /// <summary>Only the pre-rename Videos\EphineaTA exists: rename it in place.</summary>
    Migrate,
}

/// <summary>
/// The recordings folder: the <c>:record-dir</c> setting, or the default
/// <c>%USERPROFILE%\Videos\RappyRuns</c>, migrating the pre-rename
/// <c>Videos\EphineaTA</c> folder on first use (recording.lisp:466
/// resolve-record-dir, spec core §3.1).
/// </summary>
public static class RecordDirectory
{
    /// <summary>
    /// The pure decision: never rename onto an existing folder; migrate only
    /// when the old folder exists alone.
    /// </summary>
    public static RecordDirChoice Choose(bool oldExists, bool newExists) =>
        newExists || !oldExists ? RecordDirChoice.UseNew : RecordDirChoice.Migrate;

    /// <summary>
    /// The folder to record into. A configured folder (trimmed of spaces) wins.
    /// Otherwise <c>&lt;home&gt;\Videos\RappyRuns</c>; when only
    /// <c>&lt;home&gt;\Videos\EphineaTA</c> exists it is renamed to the new name,
    /// recordings included, and if that rename fails (a file in it is open)
    /// the old folder keeps being used so recordings never split in two.
    /// </summary>
    /// <param name="configured">The <c>:record-dir</c> value.</param>
    /// <param name="home">The user profile folder; null means %USERPROFILE% (or the home folder).</param>
    public static string Resolve(string? configured, string? home = null)
    {
        var trimmed = (configured ?? "").Trim(' ');
        if (trimmed.Length > 0) return trimmed;

        home ??= Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } profile
            ? profile
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var old = Path.Combine(home, "Videos", "EphineaTA");
        var @new = Path.Combine(home, "Videos", "RappyRuns");
        switch (Choose(Directory.Exists(old), Directory.Exists(@new)))
        {
            case RecordDirChoice.Migrate:
                try
                {
                    Directory.Move(old, @new);
                    return @new;
                }
                catch (IOException)
                {
                    return old;
                }
                catch (UnauthorizedAccessException)
                {
                    return old;
                }
            default:
                return @new;
        }
    }
}
