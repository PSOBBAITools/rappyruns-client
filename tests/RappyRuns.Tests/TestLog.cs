using System.Runtime.CompilerServices;
using RappyRuns.Core.Media;

namespace RappyRuns.Tests;

/// <summary>
/// Keeps the test run out of the real %TEMP%\ephinea-ta-recording.log, which
/// the installed client appends to and uploads as run diagnostics. Every test
/// that sets its own log path restores <see cref="Path"/>, never null.
/// </summary>
internal static class TestLog
{
    public static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "rappyruns-tests-" + Environment.ProcessId + ".log");

    [ModuleInitializer]
    internal static void Redirect() => RecordingLog.PathOverride = Path;
}
