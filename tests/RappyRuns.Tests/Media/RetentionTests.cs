using RappyRuns.Core.Media;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Tests.Media;

/// <summary>
/// Port of <c>run-retention-tests</c>' recordings-to-evict checks
/// (client/tests/tests-misc.lisp:222-248). The queue-driven half
/// (video-path-retention-sets) tests store.lisp and belongs with the store port.
/// </summary>
public class RetentionTests
{
    private static readonly RecordingFile[] Files =
    [
        new("a.mp4", 500, 100),
        new("b.mp4", 500, 200),
        new("c.mp4", 500, 300),
    ];

    private static IReadOnlyList<string> Evict(long? cap, string[]? prot = null, string[]? up = null) =>
        RecordingRetention.RecordingsToEvict(Files, cap, prot, up);

    [Fact]
    public void RecordingsToEvict()
    {
        Check.That("no cap set: nothing is evicted", Evict(null).Count == 0);
        Check.That("under the cap: nothing is evicted", Evict(2000).Count == 0);
        Check.That("over the cap: the oldest go until back under", Evict(1200).SequenceEqual(["a.mp4"]));
        Check.That("eviction keeps going until the total fits", Evict(600).SequenceEqual(["a.mp4", "b.mp4"]));
        Check.That("protected files are never evicted", Evict(100, prot: ["a.mp4"]).SequenceEqual(["b.mp4", "c.mp4"]));
        Check.That("when only protected files remain, eviction stops short",
            Evict(100, prot: ["a.mp4", "b.mp4"]).SequenceEqual(["c.mp4"]));
        Check.That("uploaded files are reclaimed before the rest", Evict(1200, up: ["c.mp4"]).SequenceEqual(["c.mp4"]));
        Check.That("uploaded first, then oldest-first among the rest", Evict(600, up: ["c.mp4"]).SequenceEqual(["c.mp4", "a.mp4"]));
    }

    [Fact]
    public void RecordMaxTotalBytes()
    {
        Assert.Equal(20L * 1024 * 1024 * 1024, RecordingRetention.RecordMaxTotalBytes(SexpNode.Int(20)));
        Assert.Null(RecordingRetention.RecordMaxTotalBytes(SexpNode.Int(0)));
        Assert.Null(RecordingRetention.RecordMaxTotalBytes((SexpNode?)null));
        Assert.Null(RecordingRetention.RecordMaxTotalBytes(SexpNode.Str("20")));
        Assert.Equal(536870912L, RecordingRetention.RecordMaxTotalBytes(new SFloat(0.5)));
        Assert.Equal(536870912L, RecordingRetention.RecordMaxTotalBytes(0.5));
    }
}
