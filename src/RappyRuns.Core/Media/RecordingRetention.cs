using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Media;

/// <summary>A kept recording on disk: (namestring size-bytes write-date), write-date in universal time.</summary>
public readonly record struct RecordingFile(string Path, long SizeBytes, long WriteDate);

/// <summary>The local recordings size budget (media spec §1.13, recording.lisp:1387-1432).</summary>
public static class RecordingRetention
{
    /// <summary>+retention-interval-seconds+ (store.lisp): the sweep runs this often, idle only.</summary>
    public const int IntervalSeconds = 120;

    /// <summary>
    /// <c>recordings-to-evict</c> (recording.lisp:1396): which files to delete,
    /// in order, to bring the total to <paramref name="capBytes"/>. Protected
    /// paths (awaiting upload) never go; uploaded ones go first; oldest first
    /// within each tier. Empty when the cap is unset/zero or already met.
    /// </summary>
    public static IReadOnlyList<string> RecordingsToEvict(
        IReadOnlyList<RecordingFile> files, long? capBytes,
        IReadOnlyCollection<string>? protectedPaths = null, IReadOnlyCollection<string>? uploaded = null)
    {
        if (capBytes is not { } cap || cap <= 0) return [];
        var total = files.Sum(f => f.SizeBytes);
        if (total <= cap) return [];
        var prot = new HashSet<string>(protectedPaths ?? [], StringComparer.Ordinal);
        var up = new HashSet<string>(uploaded ?? [], StringComparer.Ordinal);
        var ordered = files
            .Where(f => !prot.Contains(f.Path))
            .OrderBy(f => f.WriteDate)                       // stable, oldest first
            .OrderBy(f => up.Contains(f.Path) ? 0 : 1)       // stable: uploaded tier first
            .ToList();
        var evicted = new List<string>();
        foreach (var file in ordered)
        {
            if (total <= cap) break;
            evicted.Add(file.Path);
            total -= file.SizeBytes;
        }
        return evicted;
    }

    /// <summary>
    /// <c>record-max-total-bytes</c> (recording.lisp:1427): :record-max-total-gb
    /// as bytes (GB = 1024^3, CL round = half to even), or null when unset,
    /// non-numeric or not positive. A single-float setting is multiplied in
    /// single precision, as the Lisp does.
    /// </summary>
    public static long? RecordMaxTotalBytes(SexpNode? gb)
    {
        const long giB = 1024L * 1024 * 1024;
        switch (gb)
        {
            case SInteger i when i.Value > 0:
                return i.Value * giB;
            case SFloat { IsDouble: false } f when f.Value > 0:
                return (long)Math.Round((double)((float)f.Value * 1024f * 1024f * 1024f), MidpointRounding.ToEven);
            case SFloat f when f.Value > 0:
                return (long)Math.Round(f.Value * giB, MidpointRounding.ToEven);
            default:
                return null;
        }
    }

    /// <summary>The same for a plain number (a typed config value).</summary>
    public static long? RecordMaxTotalBytes(double? gb) =>
        gb is double g && g > 0 ? (long)Math.Round(g * 1024 * 1024 * 1024, MidpointRounding.ToEven) : null;
}
