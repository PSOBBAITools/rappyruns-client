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

    /// <param name="bundledDirs">
    /// Where to look for the shipped files, first match wins. Default:
    /// <see cref="DefaultBundledDirs"/>.
    /// </param>
    /// <param name="log">The client log.</param>
    /// <param name="universalTime">Seconds since 1900 for the aside name (tests pin it).</param>
    /// <param name="writeFile">Writes an installed file; default <see cref="File.WriteAllBytes(string, byte[])"/> (tests make it fail).</param>
    public AddonInstaller(IReadOnlyList<string>? bundledDirs = null, Action<string>? log = null, Func<long>? universalTime = null,
        Action<string, byte[]>? writeFile = null)
    {
        _bundledDirs = bundledDirs ?? DefaultBundledDirs();
        _log = log;
        _universalTime = universalTime ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2208988800L);
        _writeFile = writeFile ?? File.WriteAllBytes;
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
    /// only logged, with the exception type and message (an access-denied
    /// refusal says so). A running game keeps the DLL loaded and locked;
    /// Windows still allows renaming a loaded DLL, so the old one always moves
    /// aside first to a fresh <c>pinshare-input.dll.old-&lt;universal time&gt;</c>
    /// (fresh, since an earlier aside copy may itself still be loaded by
    /// another game window) and the new one takes effect the next time the
    /// game starts. Aside copies are deleted once nothing holds them - unless
    /// no DLL is installed, when the newest aside copy is put back instead.
    /// </summary>
    public void InstallInputDll(string addonDir)
    {
        var installed = Path.Combine(addonDir, InputDllFile);
        var asides = OldInputDlls(addonDir);
        // A failed restore left the working copy aside (S39): bring the newest
        // back rather than delete the only copy.
        if (!File.Exists(installed) && asides.OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } newest)
        {
            try
            {
                File.Move(newest, installed);
                _log?.Invoke($"pin share: input dll restored from {Path.GetFileName(newest)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"pin share: input dll could not be restored from {Path.GetFileName(newest)}: {e.Message}");
            }
        }
        foreach (var old in OldInputDlls(addonDir))
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
            _log?.Invoke($"pin share: input dll not installed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Copies the shipped <paramref name="name"/> into <paramref name="addonDir"/>
    /// unless the installed copy already has the same bytes. True when it
    /// wrote; false when already current or nothing is shipped under that
    /// name. The addon file creates the folder first. With
    /// <paramref name="renameAside"/> (C#, S39; the Lisp overwrote in place
    /// first): an existing copy is first renamed to a fresh
    /// <c>&lt;name&gt;.old-&lt;universal time&gt;</c> - allowed even while the game
    /// holds it loaded - and only then is the new file written, so a failed
    /// write never truncates the working copy. If the write fails (antivirus,
    /// permissions), the partial file is removed and the aside copy put back;
    /// the write's own error is what is thrown, with a failed restore added to
    /// its message. Only the DLL uses <paramref name="renameAside"/>:
    /// <see cref="OldInputDlls"/> is what cleans those aside copies up.
    /// Throws on failure; each caller handles it.
    /// </summary>
    private bool InstallFile(string name, string addonDir, bool renameAside)
    {
        var bundled = BundledFile(name);
        if (bundled is null) return false;
        var installed = Path.Combine(addonDir, name);
        var wanted = File.ReadAllBytes(bundled);
        if (File.Exists(installed) && wanted.AsSpan().SequenceEqual(File.ReadAllBytes(installed))) return false;
        if (!renameAside || !File.Exists(installed))
        {
            Directory.CreateDirectory(addonDir);
            _writeFile(installed, wanted);
            return true;
        }
        var aside = Path.Combine(addonDir, $"{name}.old-{_universalTime()}");
        File.Move(installed, aside); // a refusal here leaves the working copy untouched
        try
        {
            _writeFile(installed, wanted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(installed)) File.Delete(installed);
                File.Move(aside, installed);
            }
            catch (Exception r) when (r is IOException or UnauthorizedAccessException)
            {
                // The next install finds no DLL and brings the aside copy back.
                throw new IOException($"{e.Message} (and the previous copy could not be put back: {r.Message})", e);
            }
            throw;
        }
        return true;
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
