using System.Runtime.InteropServices;
using RappyRuns.Core.PinShare;

namespace RappyRuns.Win.PinShare;

/// <summary>
/// Puts the Pin Share addon into the game's <c>addons\Pin Share\</c> and keeps
/// it current (Lisp <c>pinshare-ensure-addon</c> / <c>pinshare-install-input-dll</c>,
/// pinshare-win32.lisp:93-236). The addon (init.lua) and the input DLL ship
/// with the client under <c>data\pin-share\</c> and are copied unchanged.
/// Only init.lua and pinshare-input.dll are ever written: options.lua (the
/// player's key bindings) and anything else in the folder stay untouched,
/// and a folder that is a junction / symlink (a developer's working copy)
/// is never written into.
/// </summary>
public sealed class AddonInstaller
{
    public const string AddonFile = "init.lua";
    public const string InputDllFile = "pinshare-input.dll";
    private const string OldDllPrefix = "pinshare-input.dll.old";

    private readonly IReadOnlyList<string> _bundledDirs;
    private readonly Action<string>? _log;
    private readonly Func<long> _universalTime;
    private readonly Action<string, byte[]> _writeFile;
    private readonly Action<string, string> _moveFile;

    /// <param name="bundledDirs">
    /// Where to look for the shipped files, first match wins. Default:
    /// <see cref="DefaultBundledDirs"/>.
    /// </param>
    /// <param name="log">The client log.</param>
    /// <param name="universalTime">Seconds since 1900 for the aside name (tests pin it).</param>
    /// <param name="writeFile">Writes an installed file; default <see cref="File.WriteAllBytes(string, byte[])"/> (tests make it fail).</param>
    /// <param name="moveFile">Renames during the DLL swap; default <see cref="File.Move(string, string)"/> (tests make it fail).</param>
    public AddonInstaller(IReadOnlyList<string>? bundledDirs = null, Action<string>? log = null, Func<long>? universalTime = null,
        Action<string, byte[]>? writeFile = null, Action<string, string>? moveFile = null)
    {
        _bundledDirs = bundledDirs ?? DefaultBundledDirs();
        _log = log;
        _universalTime = universalTime ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2208988800L);
        _writeFile = writeFile ?? File.WriteAllBytes;
        _moveFile = moveFile ?? File.Move;
    }

    /// <summary>
    /// <c>&lt;exe dir&gt;\data\pin-share</c> (the delivered client), then - for
    /// a development build run from the tree - the nearest ancestor's
    /// <c>client\data\pin-share</c>, like the Lisp client's source-tree
    /// fallback. Based on <see cref="AppContext.BaseDirectory"/>, never the
    /// working directory (a relative argv[0] plus another cwd left a fresh
    /// user with "init.lua is missing" in the Lisp client).
    /// </summary>
    public static IReadOnlyList<string> DefaultBundledDirs()
    {
        var dirs = new List<string> { Path.Combine(AppContext.BaseDirectory, "data", "pin-share") };
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "client", "data", "pin-share");
            if (File.Exists(Path.Combine(candidate, AddonFile)))
            {
                dirs.Add(candidate);
                break;
            }
        }
        return dirs;
    }

    /// <summary>A shipped file (e.g. init.lua), or null when no bundled folder has it.</summary>
    public string? BundledFile(string name) =>
        _bundledDirs.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Installs or updates the addon under <paramref name="addonDir"/> and
    /// makes sure its exchange folder exists. Null when the addon is ready,
    /// else the status to show (the caller shows it and retries in 5 s).
    /// </summary>
    public PinShareStatus? EnsureAddon(string addonDir)
    {
        addonDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(addonDir));
        var plugin = Path.Combine(Path.GetDirectoryName(addonDir) ?? "", AddonFile);
        var installed = Path.Combine(addonDir, AddonFile);
        // No Solybum addon plugin: the script would never be loaded.
        if (!File.Exists(plugin)) return new PinShareStatus(PinShareStatusKind.NoAddonPlugin);
        // A developer link whose target moved away (field-observed: the
        // working copy was renamed). Nothing can be created through it, and
        // it is the user's link to fix - never ours to delete.
        if (IsDanglingLink(addonDir)) return new PinShareStatus(PinShareStatusKind.BrokenLink, addonDir);
        try
        {
            if (BundledFile(AddonFile) is not null && !IsReparsePoint(addonDir))
            {
                if (InstallFile(AddonFile, addonDir, renameAside: false))
                    _log?.Invoke($"pin share: addon installed at {installed}");
                InstallInputDll(addonDir);
            }
            Directory.CreateDirectory(Path.Combine(addonDir, "exchange"));
            return File.Exists(installed) ? null : new PinShareStatus(PinShareStatusKind.InstallFailed, "init.lua is missing");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new PinShareStatus(PinShareStatusKind.InstallFailed, e.Message);
        }
    }

    /// <summary>
    /// Installs or updates pinshare-input.dll (lets the addon's bindings take
    /// priority over the game) next to the addon. Best effort: without it the
    /// addon still works, bound keys just reach the game too, so failures are
    /// only logged, with the real exception type and message (an access-denied
    /// refusal says so). A running game keeps the DLL loaded and locked;
    /// Windows still allows renaming a loaded DLL, so the old one moves aside
    /// to a fresh <c>pinshare-input.dll.old-&lt;universal time&gt;</c> (fresh, since
    /// an earlier aside copy may itself still be loaded by another game window)
    /// and the new one takes effect the next time the game starts. Aside
    /// copies, and a <c>.new</c> file an interrupted install left, are deleted
    /// once nothing holds them.
    /// </summary>
    public void InstallInputDll(string addonDir)
    {
        var installed = Path.Combine(addonDir, InputDllFile);
        // With no DLL in place, an aside copy may be the only working one (a
        // swap whose move-back failed): keep them; only a user can tell.
        IEnumerable<string> sweep = File.Exists(installed) ? OldInputDlls(addonDir) : [];
        // A .new outlives InstallFile only if the client died mid-swap.
        foreach (var old in sweep.Append(installed + NewSuffix))
        {
            try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            if (InstallFile(InputDllFile, addonDir, renameAside: true))
                _log?.Invoke($"pin share: input dll installed at {installed}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // The refusal's own type (a failed move-back wraps it).
            var cause = e is MoveBackFailedException ? e.InnerException! : e;
            _log?.Invoke($"pin share: input dll not installed: {cause.GetType().Name}: {e.Message}");
        }
    }

    private const string NewSuffix = ".new";

    /// <summary>The swap's final rename failed and so did putting the old copy back.</summary>
    private sealed class MoveBackFailedException(string message, Exception inner) : IOException(message, inner);

    /// <summary>
    /// Copies the shipped <paramref name="name"/> into <paramref name="addonDir"/>
    /// unless the installed copy already has the same bytes. True when it
    /// wrote; false when already current or nothing is shipped under that
    /// name. The addon file creates the folder first and writes in place.
    /// With <paramref name="renameAside"/> (C#, S39; the Lisp overwrote in
    /// place and renamed only after that failed, so a failed write could
    /// truncate the working copy and a failed restore strand it aside): the
    /// new bytes are written to <c>&lt;name&gt;.new</c> first, and only a
    /// complete file is swapped in - the installed copy renamed aside to a
    /// fresh <c>&lt;name&gt;.old-&lt;universal time&gt;</c> (allowed even while the
    /// game holds it loaded), the new file renamed into place, the aside copy
    /// deleted when nothing holds it. Any failure before the swap leaves the
    /// working copy untouched; a failed final rename moves it back. The
    /// original error is what is thrown (a failed move-back is added to its
    /// message). Only the DLL uses <paramref name="renameAside"/>:
    /// <see cref="OldInputDlls"/> cleans up the aside copies the game held.
    /// Throws on failure; each caller handles it.
    /// </summary>
    private bool InstallFile(string name, string addonDir, bool renameAside)
    {
        var bundled = BundledFile(name);
        if (bundled is null) return false;
        var installed = Path.Combine(addonDir, name);
        var wanted = File.ReadAllBytes(bundled);
        if (File.Exists(installed) && wanted.AsSpan().SequenceEqual(File.ReadAllBytes(installed))) return false;
        if (!renameAside)
        {
            Directory.CreateDirectory(addonDir);
            _writeFile(installed, wanted);
            return true;
        }
        var fresh = installed + NewSuffix;
        try
        {
            _writeFile(fresh, wanted);
            if (!File.Exists(installed))
            {
                _moveFile(fresh, installed);
                return true;
            }
            var aside = AsideName(addonDir, name);
            _moveFile(installed, aside);
            try
            {
                _moveFile(fresh, installed);
            }
            catch (Exception e)
            {
                try
                {
                    _moveFile(aside, installed);
                }
                catch (Exception r) when (r is IOException or UnauthorizedAccessException)
                {
                    // The working copy stays aside, and the sweep keeps aside
                    // copies while no DLL is installed.
                    throw new MoveBackFailedException($"{e.Message} (and moving the previous copy back failed: {r.Message})", e);
                }
                throw;
            }
            // Not loaded by a game: no need to keep it until the next install.
            try { File.Delete(aside); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return true;
        }
        finally
        {
            try { File.Delete(fresh); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A <c>&lt;name&gt;.old-&lt;universal time&gt;</c> nobody has taken yet (a counter suffix when that second's is taken).</summary>
    private string AsideName(string addonDir, string name)
    {
        var stem = Path.Combine(addonDir, $"{name}.old-{_universalTime()}");
        var aside = stem;
        for (var i = 1; File.Exists(aside); i++) aside = $"{stem}-{i}";
        return aside;
    }

    /// <summary>Copies of pinshare-input.dll moved aside by earlier updates.</summary>
    public static IEnumerable<string> OldInputDlls(string addonDir)
    {
        try
        {
            return [.. Directory.EnumerateFiles(addonDir)
                .Where(path => Path.GetFileName(path).StartsWith(OldDllPrefix, StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// True when <paramref name="directory"/> is a junction / symlink: a
    /// developer's addons\Pin Share linked to a working copy, which must
    /// never be overwritten with the bundled file.
    /// </summary>
    public static bool IsReparsePoint(string directory)
    {
        var attributes = NativeMethods.GetFileAttributes(Path.TrimEndingDirectorySeparator(directory));
        return attributes != NativeMethods.InvalidFileAttributes
            && (attributes & NativeMethods.FileAttributeReparsePoint) != 0;
    }

    /// <summary>
    /// True when <paramref name="directory"/> is a junction / symlink whose
    /// target is gone: a name inside it fails with ERROR_PATH_NOT_FOUND
    /// through a dead link, where a live folder says ERROR_FILE_NOT_FOUND.
    /// </summary>
    public static bool IsDanglingLink(string directory)
    {
        if (!IsReparsePoint(directory)) return false;
        var probe = NativeMethods.GetFileAttributes(Path.Combine(directory, "pinshare-link-probe"));
        return probe == NativeMethods.InvalidFileAttributes
            && Marshal.GetLastPInvokeError() == NativeMethods.ErrorPathNotFound;
    }
}
