namespace RappyRuns.Tests;

/// <summary>
/// A throwaway folder under %TEMP% (<c>&lt;prefix&gt;-&lt;guid&gt;</c>), created at
/// once and deleted with everything in it on dispose. The one way the tests
/// get scratch space, so each class does not build (and forget to clean up)
/// its own temp path.
/// </summary>
public sealed class TempDir : IDisposable
{
    /// <param name="prefix">Names the folder after its test class, so a leftover is traceable.</param>
    public TempDir(string prefix = "rr-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A path inside the folder (not created).</summary>
    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Best effort: a file a test left open (or a process still running) keeps the folder.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
