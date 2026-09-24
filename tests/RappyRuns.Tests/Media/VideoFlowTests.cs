using RappyRuns.Core.Media;
using RappyRuns.Core.Sexp;
using static RappyRuns.Tests.Media.MediaFixtures;

namespace RappyRuns.Tests.Media;

/// <summary>
/// Port of the recording half of <c>run-video-flow-tests</c>
/// (client/tests/tests-recorder.lisp:1014-1126): on-keep timing and
/// link-video-file!. The rest of that suite (submission-updates,
/// trim-finished-runs, entry-active-p, save-queue!, update-run!, clear-runs!,
/// run-video-label, run-status-label) tests store.lisp / gui.lisp functions and
/// belongs with the store and UI ports.
/// </summary>
public class VideoFlowTests
{
    [Fact]
    public void OnKeepWaitsForRemuxThenFiresOnce()
    {
        var kept = new List<(string Path, Plist Run)>();
        var h = new Harness((path, run) => kept.Add((path, run)));
        h.Step(true);
        h.Step(false, Run());
        h.Backend.Alive = false;
        h.Step(false);
        Check.That("on-keep waits for the remux", kept.Count == 0);
        h.Step(false);
        Check.That("on-keep is called once with the final path and best run",
            kept.Count == 1 && kept[0].Path.Contains("9'59.123", StringComparison.Ordinal) &&
            kept[0].Run.Get("QUEST-SLUG")!.AsString == "ep1-test-quest");
    }

    [Fact]
    public void OnKeepNotCalledForAbandonedCaptures()
    {
        var kept = new List<string>();
        var h = new Harness((path, _) => kept.Add(path));
        h.Step(true);
        h.Step(false); // abandoned
        h.Backend.Alive = false;
        h.Step(false);
        Check.That("on-keep is not called for abandoned captures", kept.Count == 0);
    }

    [Fact]
    public void OnKeepNotCalledWhenCaptureAborts()
    {
        var kept = new List<string>();
        var h = new Harness((path, _) => kept.Add(path));
        h.Step(true);
        h.Backend.Alive = false;
        h.Step(true); // ffmpeg died
        Check.That("on-keep is not called when the capture aborts", kept.Count == 0);
    }

    [Fact]
    public void ErroringOnKeepNeitherSticksNorReports()
    {
        var h = new Harness((_, _) => throw new InvalidOperationException("callback boom"));
        h.Step(true);
        h.Step(false, Run());
        h.Backend.Alive = false;
        h.Step(false);
        h.Step(false);
        Check.That("an erroring on-keep neither sticks nor reports",
            h.Recorder.State == RecorderState.Idle && h.Recorder.LastError is null);
    }
}
