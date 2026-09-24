using RappyRuns.Core.Config;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Sexp;
using RappyRuns.Core.Store;
using static RappyRuns.Tests.Store.StoreTestSupport;

namespace RappyRuns.Tests.Store;

/// <summary>
/// store.lisp queue behavior: tests-misc.lisp run-upload-queue-tests and the
/// store half of run-retention-tests, tests-recorder.lisp run-video-flow-tests
/// (queue parts) and tests-helpers.lisp trim / submit checks.
/// </summary>
public sealed class RunQueueTests : IDisposable
{
    private readonly TempDir _dir = new("rr-store-test");
    private readonly string _video;
    private readonly long _now = UniversalTime.Now();

    public RunQueueTests()
    {
        _video = _dir.File("eta-test-video.mp4").Replace('\\', '/');
        File.WriteAllText(_video, "not really mp4");
    }

    public void Dispose() => _dir.Dispose();

    private RunQueue Store(params string[] runs) => TestStore(_dir, () => _now, null, runs);

    private string V => $"\"{_video}\"";

    // ---- run-upload-queue-tests ----

    [Fact]
    public void TheOldestUnattachedRecordingUploadsFirst()
    {
        var q = Store($"(:status :submitted :server-id 3 :video-path {V})",
                      $"(:status :submitted :server-id 2 :video-path {V} :video-attached t)",
                      $"(:status :submitted :server-id 1 :video-path {V})");
        Assert.Equal(1, q.UploadCandidate(_now)?.ServerId);
    }

    [Fact]
    public void ABackingOffEntryIsSkippedUntilTheBackoffExpires()
    {
        var q = Store($"(:status :submitted :server-id 2 :video-path {V})",
                      $"(:status :submitted :server-id 1 :video-path {V} :next-upload-at {_now + 900})");
        Assert.True(2 == q.UploadCandidate(_now)?.ServerId, "a backing-off entry is skipped");
        Assert.True(1 == q.UploadCandidate(_now + 1000)?.ServerId, "the backoff expires with time");
    }

    [Fact]
    public void AGivenUpEntryIsNeverACandidate()
    {
        var q = Store($"(:status :submitted :server-id 1 :video-path {V} :upload-given-up t)");
        Assert.Null(q.UploadCandidate(_now));
    }

    [Fact]
    public void AVanishedRecordingGivesUpAndTheScanMovesOn()
    {
        var q = Store($"(:status :submitted :server-id 2 :video-path {V})",
                      "(:status :submitted :server-id 1 :video-path \"C:/nowhere/gone.mp4\")");
        var changes = 0;
        q.Changed += (_, _) => changes++;
        var candidate = q.UploadCandidate(_now);
        Assert.True(2 == candidate?.ServerId, "a vanished recording gives up and the scan moves on");
        Assert.Equal(1, changes); // the give-up raises Changed so the GUI can repaint
        Assert.True(q.Entries.Single(e => e.ServerId == 1).Is(RunKeys.UploadGivenUp), "the vanished entry is marked given up");
    }

    [Fact]
    public void ACleanScanRaisesNoChange()
    {
        var q = Store($"(:status :submitted :server-id 1 :video-path {V})");
        var changes = 0;
        q.Changed += (_, _) => changes++;
        Assert.NotNull(q.UploadCandidate(_now));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void UpdatingAnEntryThatLeftTheListSavesAndAnnouncesNothing()
    {
        var q = Store("(:status :submitted :server-id 1)");
        var entry = q.Entries.Single();
        q.Clear();
        File.Delete(q.Path);
        var changes = 0;
        q.Changed += (_, _) => changes++;
        var copy = q.Update(entry, (RunKeys.Held, SexpNode.T));
        Assert.True(copy.Is(RunKeys.Held), "the copy is still returned, as in Lisp");
        Assert.Empty(q.Entries);
        Assert.Equal(0, changes);
        Assert.False(File.Exists(q.Path), "nothing is saved");
    }

    [Fact]
    public void UpdatingAGoneEntryStillRetriesAFailedSave()
    {
        var q = Store("(:status :queued)", "(:status :submitted :server-id 1)");
        var gone = q.Entries.Single(e => e.ServerId == 1);
        Directory.CreateDirectory(q.Path); // a directory in the way: the save fails
        var failures = 0;
        q.SaveFailed += (_, _) => failures++;
        q.Clear();
        Assert.Equal(1, failures);
        Directory.Delete(q.Path);
        var changes = 0;
        q.Changed += (_, _) => changes++;
        q.Update(gone, (RunKeys.Held, SexpNode.T));
        Assert.Equal(0, changes);
        Assert.True(File.Exists(q.Path), "the owed save is retried");
    }

    [Fact]
    public void EntriesWithoutAServerDraftCannotUploadYet() =>
        Assert.Null(Store($"(:status :queued :video-path {V})").UploadCandidate(_now));

    [Fact]
    public void AnAbortedRunsRecordingNeverUploads()
    {
        var q = Store($"(:status :submitted :server-id 2 :video-path {V})",
                      $"(:status :submitted :server-id 1 :video-path {V} :aborted t)");
        Assert.Equal(2, q.UploadCandidate(_now)?.ServerId);
    }

    [Fact]
    public void AnAbortedOnlyQueueHasNoUploadCandidate() =>
        Assert.Null(Store($"(:status :submitted :server-id 1 :video-path {V} :aborted t)").UploadCandidate(_now));

    [Fact]
    public void AnUnrankedRunsRecordingNeverUploads() =>
        Assert.Null(Store($"(:status :submitted :server-id 1 :video-path {V} :unranked t)").UploadCandidate(_now));

    [Theory]
    [InlineData("a given-up upload is no longer active", "(:status :submitted :server-id 1 :video-path \"v.mp4\" :upload-given-up t)")]
    [InlineData("an aborted run with a pending video is not active", "(:status :submitted :server-id 1 :video-path \"v.mp4\" :aborted t)")]
    [InlineData("an unranked run with a pending video is not active", "(:status :submitted :server-id 1 :video-path \"v.mp4\" :unranked t)")]
    [InlineData("attached entries are not active", "(:status :submitted :server-id 2 :video-path \"w.mp4\" :video-attached t)")]
    [InlineData("rejected runs without a server id are not kept for video", "(:status :rejected :video-path \"v.mp4\")")]
    public void InactiveEntries(string label, string entry) =>
        Assert.False(RunEntries.IsActive(P(entry)), label);

    [Theory]
    [InlineData("(:status :queued)")]
    [InlineData("(:status :failed)")]
    [InlineData("(:status :submitted :server-id 1 :video-path \"v.mp4\")")]
    public void ActiveEntries(string entry) => Assert.True(RunEntries.IsActive(P(entry)));

    [Fact]
    public void TrackingModeOffLeavesTheRunUntouched()
    {
        var run = TestRun();
        Assert.Same(run, RunEntries.ApplyTrackingMode(run, trackingOnly: false, trackingPrivate: false));
    }

    [Fact]
    public void TrackingModeStampsUnranked()
    {
        var stamped = RunEntries.ApplyTrackingMode(TestRun(), trackingOnly: true, trackingPrivate: false);
        Assert.True(RunEntries.Is(stamped, RunKeys.Unranked));
        Assert.False(RunEntries.Is(stamped, RunKeys.RunPrivate));
    }

    [Fact]
    public void ThePrivateSubSettingStampsRunPrivateToo()
    {
        var run = TestRun();
        var stamped = RunEntries.ApplyTrackingMode(run, trackingOnly: true, trackingPrivate: true);
        Assert.True(RunEntries.Is(stamped, RunKeys.Unranked) && RunEntries.Is(stamped, RunKeys.RunPrivate));
        // (append (list :unranked t) (list :run-private t) run)
        Assert.Equal(["UNRANKED", "RUN-PRIVATE", "QUEST-SLUG", "TIME-MS", "FINISHED-AT"], stamped.Keys.ToArray());
        Assert.False(run.Contains(RunKeys.Unranked));
    }

    [Fact]
    public void AbortedRunsPassThroughTrackingModeUntouched()
    {
        var aborted = TestRun(aborted: true);
        Assert.Same(aborted, RunEntries.ApplyTrackingMode(aborted, trackingOnly: true, trackingPrivate: true));
    }

    // ---- run-retention-tests (store half: video-path-retention-sets) ----

    [Fact]
    public void RetentionSetsFromTheQueue()
    {
        var q = Store("(:status :submitted :server-id 4 :video-path \"up.mp4\" :video-attached t)",
                      "(:status :submitted :server-id 3 :video-path \"pending.mp4\")",
                      "(:status :submitted :server-id 2 :video-path \"aborted.mp4\" :aborted t)",
                      "(:status :submitted :server-id 1 :video-path \"unranked.mp4\" :unranked t)");
        var (prot, uploaded) = q.VideoPathRetentionSets();
        Assert.True(prot.Contains("pending.mp4"), "a file awaiting upload is protected");
        Assert.True(uploaded.Contains("up.mp4"), "an uploaded file is reclaimable first");
        Assert.True(!prot.Contains("aborted.mp4") && !uploaded.Contains("aborted.mp4"), "an aborted run's video is neither protected nor uploaded");
        Assert.True(!prot.Contains("unranked.mp4") && !uploaded.Contains("unranked.mp4"), "an unranked run's video is not protected either");
    }

    // ---- run-ux-helper-tests: trimming ----

    [Fact]
    public void TrimKeepsActiveEntriesAndCapsFinishedOnes()
    {
        var runs = Enumerable.Range(0, 70)
            .Select(i => P($"(:status {(i % 7) switch { 3 => ":queued", 5 => ":failed", _ => ":submitted" }} :n {i})"))
            .ToList();
        var trimmed = RunEntries.TrimFinished(runs, 50);
        static bool Unsent(Plist p) => RunEntries.IsUnsent(p);
        Assert.True(runs.Count(Unsent) == trimmed.Count(Unsent), "trim keeps every queued/failed entry");
        Assert.True(50 == trimmed.Count(p => RunEntries.Status(p) == RunStatus.Submitted), "trim caps finished entries at the limit");
        Assert.True(runs.Take(10).Select(p => p.Get("n")!.AsLong).SequenceEqual(trimmed.Take(10).Select(p => p.Get("n")!.AsLong)),
            "trim keeps the newest finished entries in order");
    }

    // ---- run-video-flow-tests: queue parts ----

    [Fact]
    public void LinkVideoFileMatchesByNaturalKeyAfterUpdates()
    {
        var q = Store();
        var run = TestRun();
        var entry = q.Enqueue(run);
        q.Update(entry, (RunKeys.Status, SexpNode.Kw(RunStatus.Submitted)), (RunKeys.ServerId, SexpNode.Int(7)));
        var linked = q.LinkVideoFile(run, "C:/v/run.mp4");
        Assert.True(linked?.VideoPath?.Contains("run.mp4") == true, "link-video-file! matches by natural key after updates");
        Assert.True(linked?.ServerId == 7, "linked entry still carries its server id");
        Assert.True(q.LinkVideoFile(TestRun(slug: "ep1-other"), "C:/v/x.mp4") is null, "link-video-file! returns NIL for unknown runs");
        Assert.False(linked!.Is(RunKeys.Untrimmed));
    }

    [Fact]
    public void AnUntrimmedRecordingIsLinkedButNeverAutoUploaded()
    {
        var now = _now;
        var q = TestStore(_dir, () => now, null);
        // Finished a day ago: inside the 14-day hold.
        var run = P($"(:quest-slug \"ep1-test-quest\" :time-ms 599123 :finished-at {now - 86400})");
        var entry = q.Enqueue(run);
        q.Update(entry, (RunKeys.Status, SexpNode.Kw(RunStatus.Submitted)), (RunKeys.ServerId, SexpNode.Int(7)));
        var linked = q.LinkVideoFile(run, _video, untrimmed: true);
        Assert.Equal(_video, linked?.VideoPath);
        Assert.True(linked!.Is(RunKeys.Untrimmed));
        Assert.Null(q.UploadCandidate(now));
        Assert.True(RunEntries.IsActive(linked.Data, now));
        Assert.Contains(_video, q.VideoPathRetentionSets().Protected);
        Assert.Equal("draft - use Upload to YouTube", RunDisplay.RunStatusLabel(linked.Data, Language.En, hasSubmissionToken: true));
        Assert.Equal("saved - check the end", RunDisplay.RunVideoLabel(linked.Data, Language.En, null));
        // Kept across a restart while held.
        var reloaded = new RunQueue(q.Path, () => now, null);
        reloaded.Load();
        Assert.True(reloaded.Entries.Single().Is(RunKeys.Untrimmed));
        // A clean file linked later clears the mark and the entry uploads again.
        var relinked = q.LinkVideoFile(run, _video);
        Assert.False(relinked!.Is(RunKeys.Untrimmed));
        Assert.Equal(7, q.UploadCandidate(now)?.ServerId);
    }

    [Fact]
    public void AnUntrimmedRecordingIsReleasedFourteenDaysAfterTheRun()
    {
        var now = _now;
        var q = TestStore(_dir, () => now, null);
        var finished = now - RunEntries.UntrimmedKeepSeconds + 60;
        var run = P($"(:quest-slug \"ep1-test-quest\" :time-ms 599123 :finished-at {finished})");
        var entry = q.Enqueue(run);
        q.Update(entry, (RunKeys.Status, SexpNode.Kw(RunStatus.Submitted)), (RunKeys.ServerId, SexpNode.Int(7)));
        var linked = q.LinkVideoFile(run, _video, untrimmed: true)!;
        Assert.True(RunEntries.IsActive(linked.Data, now));
        Assert.Contains(_video, q.VideoPathRetentionSets().Protected);
        now += 60; // exactly 14 days after the run
        Assert.False(RunEntries.IsActive(linked.Data, now));
        Assert.DoesNotContain(_video, q.VideoPathRetentionSets().Protected);
        Assert.Null(q.UploadCandidate(now));
        q.Save();
        var reloaded = new RunQueue(q.Path, () => now, null);
        reloaded.Load();
        Assert.Empty(reloaded.Entries);
    }

    [Fact]
    public void AHeldUntrimmedRecordingWhoseFileVanishedGivesUp()
    {
        var now = _now;
        var q = TestStore(_dir, () => now, null,
            $"(:status :submitted :server-id 1 :video-path \"C:/nowhere/gone.mp4\" :untrimmed t :finished-at {now - 60})");
        Assert.Null(q.UploadCandidate(now));
        Assert.True(q.Entries.Single().Is(RunKeys.UploadGivenUp));
        Assert.False(RunEntries.IsActive(q.Entries.Single().Data, now));
    }

    [Fact]
    public void AnUntrimmedRecordingWithoutAFinishTimeIsNotHeld() =>
        Assert.False(RunEntries.IsActive(P("(:status :submitted :server-id 1 :video-path \"v.mp4\" :untrimmed t)"), 1000));

    [Fact]
    public void ActiveEntriesSurviveTrimmingAttachedOnesDoNot()
    {
        var unattached = P("(:status :submitted :server-id 1 :video-path \"v.mp4\")");
        var attached = P("(:status :submitted :server-id 2 :video-path \"w.mp4\" :video-attached t)");
        var runs = new List<Plist> { unattached };
        runs.AddRange(Enumerable.Range(0, 60).Select(i => P($"(:status :submitted :n {i})")));
        runs.Add(attached);
        var trimmed = RunEntries.TrimFinished(runs, 50);
        Assert.True(trimmed.Contains(unattached), "unattached video survives the finished-run cap");
        Assert.False(trimmed.Contains(attached), "attached video counts as finished and trims away");
    }

    [Fact]
    public void SaveQueueKeepsActiveEntriesAndDropsFinishedTelemetry()
    {
        var q = Store("(:status :submitted :server-id 1 :video-path \"v.mp4\" :telemetry (:frames ()))",
                      "(:status :queued :telemetry (:frames ()))");
        q.Save();
        var saved = SexpReader.TryReadFile(q.Path)!.Elements.Select(Plist.From).ToList();
        Assert.True(saved.Count == 2, "queue file keeps both active entries");
        Assert.True(!saved[0]!.Contains(RunKeys.Telemetry), "persisted video entry drops its telemetry");
        Assert.True(saved[1]!.Contains(RunKeys.Telemetry), "persisted queued entry keeps its telemetry");
    }

    [Fact]
    public void ASubmittedEntryReleasesItsTelemetryInMemory()
    {
        var q = Store("(:status :queued :telemetry (:frames (1)))");
        var submitted = q.Update(q.Entries[0], (RunKeys.Status, SexpNode.Kw(RunStatus.Submitted)), (RunKeys.ServerId, SexpNode.Int(9)));
        Assert.False(submitted.Is(RunKeys.Telemetry));
        Assert.False(q.Entries[0].Is(RunKeys.Telemetry));
    }

    [Fact]
    public void AFailedEntryKeepsItsTelemetryForTheRetry()
    {
        var q = Store("(:status :queued :telemetry (:frames (1)))");
        var failed = q.Update(q.Entries[0], (RunKeys.Status, SexpNode.Kw(RunStatus.Failed)), (RunKeys.Reason, SexpNode.Str("net")));
        Assert.True(failed.Is(RunKeys.Telemetry));
    }

    [Fact]
    public void ClearKeepsOnlyUnsentEntries()
    {
        var q = Store("(:status :submitted :server-id 1)",
                      "(:status :queued)",
                      "(:status :failed :reason \"boom\")",
                      "(:status :submitted :server-id 2 :video-path \"v.mp4\")",
                      "(:status :submitted :server-id 3 :video-path \"w.mp4\" :video-attached t)",
                      "(:status :duplicate :server-id 4)",
                      "(:status :rejected :reason \"nope\")");
        Assert.True(q.Clear() == 5, "clear-runs! reports the removed count");
        Assert.True(q.Entries.Select(Status).SequenceEqual(["QUEUED", "FAILED"]), "clear keeps only queued and failed entries, in order");
        Assert.True(SexpReader.TryReadFile(q.Path)!.Elements.Count == 2, "clear persists the surviving queue");
    }

    // ---- behavior beyond the Lisp checks ----

    [Fact]
    public void EnqueuePushesAQueuedCopyToTheFrontAndSaves()
    {
        var q = Store("(:status :submitted :server-id 1 :video-path \"v.mp4\")");
        var run = TestRun();
        var entry = q.Enqueue(run);
        Assert.Equal(entry.Id, q.Entries[0].Id);
        Assert.StartsWith("(:STATUS :QUEUED :QUEST-SLUG \"ep1-test-quest\" ", SexpWriter.Write(entry.Data.ToSexp()));
        Assert.False(run.Contains(RunKeys.Status));
        Assert.Equal(2, SexpReader.TryReadFile(q.Path)!.Elements.Count);
    }

    [Fact]
    public void AnUpdateFromAStaleCopyIsNotLost()
    {
        var q = Store("(:status :queued :quest-slug \"q\")");
        var stale = q.Entries[0];
        q.Update(stale, (RunKeys.VideoPath, SexpNode.Str("v.mp4")));
        var result = q.Update(stale, (RunKeys.Status, SexpNode.Kw(RunStatus.Submitted)), (RunKeys.ServerId, SexpNode.Int(3)));
        Assert.Equal("v.mp4", result.VideoPath);
        Assert.Equal("v.mp4", q.Entries[0].VideoPath);
        Assert.Equal(RunStatus.Submitted, q.Entries[0].Status);
    }

    [Fact]
    public void UpdateSetsNewKeysAtTheFrontLikeSetfGetf()
    {
        var q = Store("(:status :queued :quest-slug \"q\")");
        var updated = q.Update(q.Entries[0], Submission.Updates(new SubmitResult(SubmitOutcome.Created, 42, "u")));
        Assert.Equal("(:SERVER-ID 42 :URL \"u\" :STATUS :SUBMITTED :QUEST-SLUG \"q\")", SexpWriter.Write(updated.Data.ToSexp()));
    }

    [Fact]
    public void LoadAppendsSavedEntriesAfterTheInMemoryOnes()
    {
        var q = Store("(:status :queued :n 1)");
        q.Save();
        var fresh = new RunQueue(q.Path);
        // In memory only (Enqueue would save over the file first; the app
        // loads at startup before anything is enqueued).
        fresh.ResetForTests([P("(:n 2)")]);
        fresh.Load();
        Assert.Equal([2L, 1L], fresh.Entries.Select(e => e.Get("n").AsLong!.Value).ToArray());
    }

    [Fact]
    public void LoadingAMissingOrCorruptFileLoadsNothing()
    {
        var q = new RunQueue(_dir.File("missing.sexp"));
        q.Load();
        Assert.Empty(q.Entries);
        File.WriteAllText(_dir.File("bad.sexp"), "((:status :queued");
        var bad = new RunQueue(_dir.File("bad.sexp"));
        bad.Load();
        Assert.Empty(bad.Entries);
    }

    [Fact]
    public void AnEmptyQueueSavesNil()
    {
        var q = Store("(:status :submitted :server-id 1)");
        q.Save();
        Assert.Equal("COMMON-LISP:NIL", File.ReadAllText(q.Path));
    }

    [Fact]
    public void FinishedEntriesAreCappedAtFifty()
    {
        var q = Store();
        for (var i = 0; i < 60; i++)
        {
            var e = q.Enqueue(P($"(:n {i})"));
            q.Update(e, (RunKeys.Status, SexpNode.Kw(RunStatus.Duplicate)));
        }
        Assert.Equal(RunQueue.MaxFinishedRuns, q.Entries.Count);
        Assert.Equal(59, q.Entries[0].Get("n").AsLong);
    }

    [Fact]
    public void UniversalTimeMatchesUnixOffset()
    {
        Assert.Equal(2208988800, UniversalTime.FromDateTimeOffset(DateTimeOffset.UnixEpoch));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UniversalTime.ToDateTimeOffset(UniversalTime.FromDateTimeOffset(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void HostedVideoReplaceable()
    {
        Assert.True(RunEntries.HostedVideoReplaceable(P("(:video-uploaded t)")));
        Assert.False(RunEntries.HostedVideoReplaceable(P("(:video-uploaded t :video-url \"https://youtu.be/x\")")));
        Assert.False(RunEntries.HostedVideoReplaceable(P("(:video-attached t)")));
    }

    [Fact]
    public void SameRunUsesTheNaturalKey()
    {
        Assert.True(RunEntries.SameRun(TestRun(), P("(:status :queued)").Also(p => { foreach (var k in TestRun().Keys) p.Set(k, TestRun().Get(k)!); })));
        Assert.False(RunEntries.SameRun(TestRun(), TestRun(timeMs: 1)));
        Assert.True(RunEntries.SameRun(P("(:a 1)"), P("(:quest-slug nil)")));
    }
}

internal static class PlistTestExtensions
{
    public static Plist Also(this Plist plist, Action<Plist> action)
    {
        action(plist);
        return plist;
    }
}
