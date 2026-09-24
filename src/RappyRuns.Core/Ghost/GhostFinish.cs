using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Ghost;

/// <summary>
/// The completion comparison (ghost.lisp:523-552): a finished run against the
/// ghost's official final time. Official times share a base per category by
/// construction, so the final gap is ms-accurate whatever the ghost's room
/// precision.
/// </summary>
public static class GhostFinish
{
    /// <summary>
    /// Should <paramref name="ghost"/>'s final time be compared against the
    /// completed <paramref name="run"/> (ghost-covers-run-p, ghost.lisp:523)?
    /// The quest slug must match (segment ghosts compare against their segment
    /// run), the run must not be aborted, and both must share a pb board
    /// category - unless the user chose the target explicitly, or the ghost
    /// predates the pb field.
    /// </summary>
    public static bool CoversRun(GhostReference ghost, Plist run)
    {
        if (!string.Equals(run.Get("quest-slug")?.AsString, ghost.QuestSlug, StringComparison.Ordinal)) return false;
        if (IsTrue(run.Get("aborted"))) return false;
        return ghost.Source == "target"
               || ghost.Pb is null
               || ghost.Pb == (IsTrue(run.Get("pb")) ? 1 : 0);
    }

    /// <summary>
    /// Stamp each completed run the ghost covers with the final comparison
    /// (annotate-ghost-runs, ghost.lisp:535): <c>:ghost-delta-ms</c> (run time
    /// minus ghost time, negative = beat the ghost), <c>:ghost-time-ms</c> and
    /// <c>:ghost-label</c>. Stamped runs are fresh copies (Lisp LIST* conses new
    /// keys onto the old plist without mutating it) with the three keys first;
    /// the rest are returned as-is. With no ghost the same list comes back.
    /// Call it on the poll thread before enqueueing (main.lisp:134), while the
    /// quest is still loaded - the ghost is dropped once no quest is.
    /// </summary>
    public static IReadOnlyList<Plist> AnnotateRuns(GhostReference? ghost, IReadOnlyList<Plist> runs)
    {
        if (ghost is null) return runs;
        var result = new List<Plist>(runs.Count);
        foreach (var run in runs)
        {
            // A run without an integer :time-ms cannot be compared (Lisp would
            // signal inside the subtraction; every real run carries one).
            if (CoversRun(ghost, run) && run.Get("time-ms")?.AsLong is { } timeMs)
            {
                var stamped = run.Clone();
                // Set puts a new key at the front, so set in reverse order to
                // get (:ghost-delta-ms :ghost-time-ms :ghost-label . run).
                stamped.Set("ghost-label", SexpNode.Str(ghost.Label ?? ""));
                stamped.Set("ghost-time-ms", SexpNode.Int(ghost.TimeMs));
                stamped.Set("ghost-delta-ms", SexpNode.Int(timeMs - ghost.TimeMs));
                result.Add(stamped);
            }
            else
            {
                result.Add(run);
            }
        }
        return result;
    }

    /// <summary>Lisp truthiness of a plist value: absent or NIL is false, anything else true.</summary>
    private static bool IsTrue(SexpNode? value) => value is not null && !value.IsNil;
}
