using RappyRuns.Core.Sexp;
using RappyRuns.Core.Store;

namespace RappyRuns.Tests.Store;

/// <summary>Builders mirroring the Lisp test helpers (with-test-store, make-test-run).</summary>
internal static class StoreTestSupport
{
    /// <summary>A plist from Lisp text, e.g. P("(:status :queued)").</summary>
    public static Plist P(string text) =>
        Plist.From(SexpReader.ReadOne(text)) ?? throw new ArgumentException($"not a plist: {text}");

    /// <summary>A throwaway folder, deleted on dispose.</summary>
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rr-store-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>with-test-store: a queue over a throwaway file holding <paramref name="runs"/> (newest first).</summary>
    public static RunQueue TestStore(TempDir dir, Func<long>? now = null, Func<string, bool>? fileExists = null, params string[] runs)
    {
        var queue = new RunQueue(dir.File("queue.sexp"), now, fileExists);
        queue.ResetForTests(runs.Select(P));
        return queue;
    }

    /// <summary>make-test-run (tests-recorder.lisp:90): finished-at is 2026-07-04 21:30:00 UTC as universal time.</summary>
    public static Plist TestRun(string slug = "ep1-test-quest", long timeMs = 599123, bool aborted = false)
    {
        var finishedAt = UniversalTime.FromDateTimeOffset(new DateTimeOffset(2026, 7, 4, 21, 30, 0, TimeSpan.Zero));
        return P($"(:quest-slug \"{slug}\" :time-ms {timeMs} :finished-at {finishedAt}{(aborted ? " :aborted t" : "")})");
    }

    public static string Status(RunEntry entry) => entry.Status ?? "";
}
