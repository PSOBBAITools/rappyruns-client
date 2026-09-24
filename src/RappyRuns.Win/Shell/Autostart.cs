using Microsoft.Win32;

namespace RappyRuns.Win.Shell;

/// <summary>
/// The Run-key command line and the path comparison behind "is autostart on?"
/// (spec core §2.5). Pure, so the rules are testable without a registry.
/// </summary>
public static class AutostartCommand
{
    /// <summary>The flag the logon launch carries so it goes straight to the tray (core §2.3).</summary>
    public const string MinimizedFlag = "--minimized";

    /// <summary><c>"&lt;exe&gt;" --minimized</c>, as <c>autostart-command</c> (autostart-win32.lisp:96).</summary>
    public static string Format(string exePath) => $"\"{exePath}\" {MinimizedFlag}";

    /// <summary>
    /// The exe path of a registered command: the quoted part when it starts
    /// with a quote, otherwise everything before a trailing
    /// <c>--minimized</c>. Null for an empty or malformed value.
    /// </summary>
    public static string? ExtractPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end <= 1) return null;
            return s[1..end];
        }
        if (s.EndsWith(" " + MinimizedFlag, StringComparison.OrdinalIgnoreCase))
            s = s[..^(MinimizedFlag.Length + 1)].TrimEnd();
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Whether two exe paths name the same file: case-insensitive after full
    /// path normalization (separators, <c>..</c>, 8.3 short names).
    /// </summary>
    /// <remarks>
    /// Deviation (recommended by core §2.5): the Lisp check is an exact string
    /// match against its argv[0], which may differ in case or form from
    /// <see cref="Environment.ProcessPath"/>; an exact match here would show
    /// the checkbox off to users who migrated from the Lisp client. Relative
    /// paths never match - they depend on the working directory and a Run
    /// entry holding one is broken anyway.
    /// </remarks>
    public static bool SamePath(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na is not null && nb is not null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(p)) return null;
        try
        {
            p = Path.GetFullPath(p);
        }
        catch (Exception)
        {
            return null;
        }
        return LongPath(p).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static unsafe string LongPath(string path)
    {
        if (!path.Contains('~')) return path;
        var buffer = stackalloc char[1024];
        var n = NativeMethods.GetLongPathNameW(path, buffer, 1024);
        return n is > 0 and < 1024 ? new string(buffer, 0, (int)n) : path;
    }
}

/// <summary>Outcome of <see cref="Autostart.ReconcileAtStartup"/>.</summary>
public enum AutostartRepair
{
    /// <summary>No Run value: autostart is off, nothing to do.</summary>
    NotRegistered,

    /// <summary>The value is exactly the canonical command.</summary>
    UpToDate,

    /// <summary>The value was ours but stale, and was rewritten to the canonical command.</summary>
    Rewritten,

    /// <summary>The value points at another exe that still exists; left alone (reads as disabled, like Lisp).</summary>
    LeftForeign,

    /// <summary>Reading or writing the registry failed.</summary>
    Failed,
}

/// <summary>
/// "Start with Windows" through the per-user Run key - a port of
/// client/src/autostart-win32.lisp. The registry is the only source of truth:
/// nothing about autostart is kept in config.
/// </summary>
/// <remarks>
/// The root key, subkey and value name are injectable so tests can run
/// against a scratch key instead of the user's real Run key.
/// </remarks>
public sealed class Autostart
{
    /// <summary>HKCU subkey (autostart-win32.lisp:22).</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name shared with the Lisp client (autostart-win32.lisp:25).</summary>
    public const string ValueName = "RappyRunsClient";

    private readonly RegistryKey _root;
    private readonly string _keyPath;
    private readonly string _valueName;

    /// <param name="exePath">The exe to register; defaults to <see cref="Environment.ProcessPath"/> (core §2.3 allows unifying on it).</param>
    /// <param name="root">Registry root; defaults to HKCU.</param>
    /// <param name="keyPath">Subkey holding the value.</param>
    /// <param name="valueName">Value name.</param>
    public Autostart(string? exePath = null, RegistryKey? root = null, string keyPath = RunKeyPath, string valueName = ValueName)
    {
        ExePath = exePath ?? Environment.ProcessPath;
        _root = root ?? Registry.CurrentUser;
        _keyPath = keyPath;
        _valueName = valueName;
    }

    /// <summary>The running exe, or null when unknown (then autostart cannot be enabled).</summary>
    public string? ExePath { get; }

    /// <summary>What <see cref="SetEnabled"/> writes, or null without an exe path.</summary>
    public string? CanonicalCommand => string.IsNullOrEmpty(ExePath) ? null : AutostartCommand.Format(ExePath);

    /// <summary>The registered command string, or null when absent / not a string / unreadable.</summary>
    public string? ReadRegistered()
    {
        try
        {
            using var key = _root.OpenSubKey(_keyPath, writable: false);
            return key?.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the Run value exists and points at this exe
    /// (<c>autostart-enabled-p</c>, autostart-win32.lisp:121). A moved
    /// install reads as disabled, so re-enabling rewrites it. Seeds the
    /// settings checkbox; after a toggle the UI must read this again rather
    /// than trust the requested state (ui-shell R17).
    /// </summary>
    public bool IsEnabled() =>
        ExePath is not null && AutostartCommand.SamePath(AutostartCommand.ExtractPath(ReadRegistered()), ExePath);

    /// <summary>
    /// Write the canonical command, or delete the value (<c>set-autostart!</c>,
    /// autostart-win32.lisp:130). Deleting an absent value succeeds. Returns
    /// true on success; never throws.
    /// </summary>
    public bool SetEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                var command = CanonicalCommand;
                if (command is null) return false;
                using var key = _root.CreateSubKey(_keyPath, writable: true);
                key.SetValue(_valueName, command, RegistryValueKind.String);
                return true;
            }
            using (var key = _root.OpenSubKey(_keyPath, writable: true))
                key?.DeleteValue(_valueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Startup repair of a stale entry (core §10.7 #11): when the value is
    /// ours but not the canonical command, rewrite it. "Ours" means it names
    /// this exe in another spelling (case, short name, missing
    /// <c>--minimized</c>), or it names a file that no longer exists in this
    /// exe's folder - the old exe name the Lisp updater renamed to
    /// <c>.old</c> when the C# exe arrived under the canonical name. An entry
    /// pointing at some other existing exe (another install, a dev build) is
    /// left alone so starting one copy never hijacks another's autostart.
    /// Call once at startup; never throws.
    /// </summary>
    public AutostartRepair ReconcileAtStartup()
    {
        var registered = ReadRegistered();
        if (registered is null) return AutostartRepair.NotRegistered;
        var canonical = CanonicalCommand;
        if (canonical is null || ExePath is null) return AutostartRepair.Failed;
        if (string.Equals(registered, canonical, StringComparison.Ordinal)) return AutostartRepair.UpToDate;

        var path = AutostartCommand.ExtractPath(registered);
        var ours = AutostartCommand.SamePath(path, ExePath) || IsGoneSibling(path);
        if (!ours) return AutostartRepair.LeftForeign;
        return SetEnabled(true) ? AutostartRepair.Rewritten : AutostartRepair.Failed;
    }

    private bool IsGoneSibling(string? path)
    {
        if (path is null || AutostartCommand.Normalize(path) is null) return false;
        try
        {
            return !File.Exists(path) &&
                   AutostartCommand.SamePath(Path.GetDirectoryName(path), Path.GetDirectoryName(ExePath));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
