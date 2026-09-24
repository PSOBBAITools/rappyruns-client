using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Media;

/// <summary>
/// How the runs completed during one capture relate to its video: which run
/// names and receives the file, where each run starts in the video, and where
/// the remux cuts the tail (media spec §1.2, §1.9). Runs are the detector's
/// plists - the same instances the poll loop enqueues right after
/// <see cref="Recorder.Step"/>, which is why :video-offset-ms is stamped in place.
/// </summary>
public static class SessionRuns
{
    /// <summary>+keep-tail-seconds+ (recording.lisp:331): video kept past the last run's end.</summary>
    public const int KeepTailSeconds = 2;

    /// <summary>
    /// <c>best-session-run</c> (recording.lisp:567): the longest COMPLETED run;
    /// aborted runs only when nothing completed (their entries never upload,
    /// so a finished segment beats the longer aborted stay it was cut from).
    /// Stable: the earliest of equal times wins, like the Lisp list sort.
    /// </summary>
    public static Plist? BestSessionRun(IReadOnlyList<Plist> runs)
    {
        var completed = runs.Where(r => !Lisp.Truthy(r, "ABORTED")).ToList();
        var pool = completed.Count > 0 ? completed : runs;
        return pool.OrderByDescending(r => Lisp.GetLong(r, "TIME-MS") ?? 0).FirstOrDefault();
    }

    /// <summary>
    /// <c>session-video-duration-ms</c> (recording.lisp:1142): the remux's
    /// <c>-t</c> - two seconds past the capture elapsed at the LAST run's end;
    /// null when nothing completed (that capture is deleted, not remuxed).
    /// </summary>
    public static long? SessionVideoDurationMs(long? runEndMs) =>
        runEndMs is { } end ? end + 1000L * KeepTailSeconds : null;

    /// <summary>
    /// The pure half of <c>note-run-video-timing</c> (recording.lisp:1102), given
    /// the one elapsed reading. Returns the new run-end mark (every run moves it,
    /// aborted ones too) and stamps each run with :video-offset-ms = elapsed -
    /// time-ms when that is non-negative and the run has none yet. A negative
    /// offset is NOT stamped (never clamped to 0 - that is an undecided spec
    /// change, S06): the run that starts a capture always lands there, which is
    /// why the trim tracks the end directly instead of offset + duration (run
    /// 5348). The key is appended in place, as the Lisp NCONC does, because the
    /// submission queue holds the same plist.
    /// </summary>
    public static long NoteRunVideoTiming(long elapsedMs, long? runEndMs, IEnumerable<Plist> runs)
    {
        var end = runEndMs is { } previous && previous >= elapsedMs ? previous : elapsedMs;
        foreach (var run in runs)
        {
            var offset = elapsedMs - (Lisp.GetLong(run, "TIME-MS") ?? 0);
            var existing = run.Get("VIDEO-OFFSET-MS");
            if (offset >= 0 && (existing is null || existing.IsNil))
                Lisp.Append(run, "VIDEO-OFFSET-MS", SexpNode.Int(offset));
        }
        return end;
    }
}
