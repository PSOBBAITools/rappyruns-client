using System.Text;
using RappyRuns.Core;
using RappyRuns.Core.Update;

namespace RappyRuns.Host;

/// <summary>
/// "Started OK" marker for the bridge updater (Lisp v0.61.x, PLAN.md
/// decision 1). The bridge deletes the file, launches the new exe, and rolls
/// back to RappyRunsClient.exe.old unless the file appears with the new
/// process's PID within its timeout. Written once the UI has loaded and
/// talked to the host, so a client that cannot show its window never
/// counts as installed.
/// </summary>
public static class StartupMarker
{
    /// <summary>
    /// started-marker-path (updater.lisp:302): in <c>windows-temp-dir</c> (TEMP, then
    /// TMP, then home) - the bridge helper reads it there, so never
    /// <see cref="Path.GetTempPath"/> (TMP first). Resolved at run time (spec core §3.1).
    /// </summary>
    public static string FilePath => Path.Combine(UpdateFiles.TempDir(), "rappyruns-client-started.txt");

    private static bool _written;

    /// <summary>Writes "&lt;pid&gt; &lt;version&gt;" once per process. Failures are ignored.</summary>
    public static void Write()
    {
        if (_written) return;
        _written = true;
        try
        {
            File.WriteAllText(FilePath, $"{Environment.ProcessId} {ClientVersion.Display}\n", new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
