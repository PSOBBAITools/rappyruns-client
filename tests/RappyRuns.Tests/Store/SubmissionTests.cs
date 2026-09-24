using System.Text.Json;
using RappyRuns.Core.Config;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Sexp;
using RappyRuns.Core.Store;
using static RappyRuns.Tests.Store.StoreTestSupport;

namespace RappyRuns.Tests.Store;

/// <summary>
/// Submission and upload flows (store.lisp submit-queued!, submission-updates,
/// ensure-submission-token, upload-entry-video!): tests-recorder.lisp's
/// submission-updates checks, tests-helpers.lisp's parked-queue check, the
/// SBCL golden, and fakes for the abstracted HTTP calls.
/// </summary>
public sealed class SubmissionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static SubmitResult FromJson(SubmitOutcome outcome, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SubmitResult.FromJson(outcome, doc.RootElement.Clone());
    }

    private static string Print(IReadOnlyList<KeyValuePair<string, SexpNode>> updates) =>
        SexpWriter.Write(new SList(updates.SelectMany(kv => new[] { SexpNode.Kw(kv.Key), kv.Value }).ToList()));

    // ---- tests-recorder.lisp: submission updates ----

    [Fact]
    public void CreatedRunsRememberTheirServerId() =>
        Assert.Equal("(:STATUS :SUBMITTED :URL \"https://x/runs/42\" :SERVER-ID 42)",
            Print(Submission.Updates(FromJson(SubmitOutcome.Created, "{\"id\":42,\"url\":\"https://x/runs/42\"}"))));

    [Fact]
    public void DuplicateRunsRememberTheirServerIdToo() =>
        Assert.Contains(Submission.Updates(FromJson(SubmitOutcome.Duplicate, "{\"id\":42,\"url\":\"https://x/runs/42\"}")),
            kv => kv.Key == RunKeys.ServerId && kv.Value.AsLong == 42);

    [Fact]
    public void CreatedRunsCarryTheBoardStanding()
    {
        var updates = Submission.Updates(FromJson(SubmitOutcome.Created,
            "{\"id\":7,\"standing\":{\"rank\":2,\"parties\":5,\"previous_best_ms\":605000,\"delta_ms\":-3210}}"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.True(updates[RunKeys.StandingRank].AsLong == 2 && updates[RunKeys.StandingParties].AsLong == 5,
            "created runs carry the board rank and party count");
        Assert.True(updates[RunKeys.StandingDeltaMs].AsLong == -3210 && updates[RunKeys.StandingPrevMs].AsLong == 605000,
            "created runs carry the personal-best delta and previous time");
    }

    [Fact]
    public void ASubmissionWithNoStandingBlockAddsNoStandingKeys() =>
        Assert.DoesNotContain(Submission.Updates(FromJson(SubmitOutcome.Created, "{\"id\":7}")), kv => kv.Key == RunKeys.StandingRank);

    [Fact]
    public void AFirstTimeStandingCarriesARankButNoDelta()
    {
        var updates = Submission.Updates(FromJson(SubmitOutcome.Created, "{\"standing\":{\"rank\":1,\"parties\":1}}"));
        Assert.Contains(updates, kv => kv.Key == RunKeys.StandingRank && kv.Value.AsLong == 1);
        Assert.DoesNotContain(updates, kv => kv.Key == RunKeys.StandingDeltaMs);
    }

    [Fact]
    public void RejectedRunsCarryAReasonNotAServerId()
    {
        var updates = Submission.Updates(FromJson(SubmitOutcome.Rejected, "{\"message\":\"nope\"}"));
        Assert.DoesNotContain(updates, kv => kv.Key == RunKeys.ServerId);
        Assert.Contains("nope", updates.Single(kv => kv.Key == RunKeys.Reason).Value.AsString);
    }

    [Fact]
    public void MatchesTheLispSubmissionUpdatesOnEveryGoldenCase()
    {
        foreach (var g in Golden.Load("store/submission-updates.json").EnumerateArray())
        {
            var outcome = Enum.Parse<SubmitOutcome>(g.GetProperty("outcome").GetString()!, ignoreCase: true);
            var payload = g.GetProperty("payload").GetString()!;
            Assert.True(g.GetProperty("updates").GetString() == Print(Submission.Updates(FromJson(outcome, payload))),
                $"{outcome} {payload}");
        }
    }

    [Fact]
    public void ApiErrorsFailTheEntryWithTheMessage() =>
        Assert.Equal("(:STATUS :FAILED :REASON \"boom\")", Print(Submission.Updates(SubmitResult.ApiError("boom"))));

    [Fact]
    public void AnonymousClientLabel()
    {
        Assert.Equal("Desktop client (PC1) [guest]", Submission.AnonymousClientLabel("PC1"));
        Assert.Equal("Desktop client [guest]", Submission.AnonymousClientLabel(""));
    }

    // ---- submit-queued! with fakes ----

    private sealed class FakeRegistrar(Func<string, string> register) : IAnonymousRegistrar
    {
        public List<string> Labels { get; } = [];

        public Task<string> RegisterAnonymousAsync(string label, CancellationToken cancellationToken)
        {
            Labels.Add(label);
            return Task.FromResult(register(label));
        }
    }

    private sealed class FakeSubmitter(Func<Plist, SubmitResult> answer) : IRunSubmitter
    {
        public List<(Plist Entry, string Token)> Calls { get; } = [];

        public Task<SubmitResult> SubmitAsync(Plist entry, string token, CancellationToken cancellationToken)
        {
            Calls.Add((entry, token));
            return Task.FromResult(answer(entry));
        }
    }


    private ConfigStore Config() => ConfigStore.Open(_dir.File("config"));

    [Fact]
    public async Task SubmitQueuedParksTheQueueWhenRegistrationIsUnreachable()
    {
        var config = Config();
        var q = TestStore(_dir, runs: "(:status :queued :quest-slug \"q\" :time-ms 1)");
        var registrar = new FakeRegistrar(_ => throw new HttpRequestException("connection refused"));
        var submitter = new FakeSubmitter(_ => throw new InvalidOperationException("must not submit"));
        var result = await q.SubmitQueuedAsync(config, registrar, submitter, "PC1");
        Assert.True(result is null && config.SubmissionToken == "" && q.Entries[0].Status == RunStatus.Queued,
            "submit-queued! parks the queue when registration is unreachable");
        Assert.Equal(["Desktop client (PC1) [guest]"], registrar.Labels);
    }

    [Fact]
    public async Task AnUnlinkedClientRegistersAGuestOnceAndSavesIt()
    {
        var config = Config();
        var q = TestStore(_dir, runs: "(:status :queued :quest-slug \"q\" :time-ms 1)");
        var registrar = new FakeRegistrar(_ => "eta_guest");
        var submitter = new FakeSubmitter(_ => new SubmitResult(SubmitOutcome.Created, 5, "https://x/runs/5"));
        var result = await q.SubmitQueuedAsync(config, registrar, submitter);
        Assert.Equal("eta_guest", config.AnonToken);
        Assert.Equal("eta_guest", ConfigStore.Open(_dir.File("config")).AnonToken);
        Assert.Equal("eta_guest", submitter.Calls.Single().Token);
        Assert.Equal(RunStatus.Submitted, result!.Single().Status);
        await q.SubmitQueuedAsync(config, registrar, submitter);
        Assert.Single(registrar.Labels);
    }

    [Fact]
    public async Task SubmitQueuedSendsQueuedAndFailedEntriesAndReturnsTheUpdatedOnes()
    {
        var config = Config();
        config.ApiToken = " eta_x\r\n";
        var q = TestStore(_dir, runs:
        [
            "(:status :queued :quest-slug \"a\" :telemetry (:frames (1)))",
            "(:status :submitted :quest-slug \"b\" :server-id 1)",
            "(:status :failed :quest-slug \"c\" :reason \"net\")",
            "(:status :queued :quest-slug \"d\")",
        ]);
        var submitter = new FakeSubmitter(e => RunEntries.Get(e, RunKeys.QuestSlug).AsString switch
        {
            "a" => FromJson(SubmitOutcome.Created, "{\"id\":10,\"url\":\"u10\",\"standing\":{\"rank\":1,\"parties\":2}}"),
            "c" => FromJson(SubmitOutcome.Rejected, "{\"message\":\"bad\",\"errors\":[\"x\",\"y\"]}"),
            _ => SubmitResult.ApiError("POST /api/runs -> 500"),
        });
        var result = (await q.SubmitQueuedAsync(config, new FakeRegistrar(_ => throw new InvalidOperationException()), submitter))!;
        Assert.Equal(new[] { "a", "c", "d" }, submitter.Calls.Select(c => RunEntries.Get(c.Entry, RunKeys.QuestSlug).AsString ?? ""));
        Assert.All(submitter.Calls, c => Assert.Equal("eta_x", c.Token));
        Assert.Equal([RunStatus.Submitted, RunStatus.Rejected, RunStatus.Failed], result.Select(Status).ToArray());
        Assert.False(result[0].Is(RunKeys.Telemetry));
        Assert.Equal("bad x; y", result[1].Get(RunKeys.Reason).AsString);
        Assert.Equal("POST /api/runs -> 500", result[2].Get(RunKeys.Reason).AsString);
        Assert.Equal(1, q.Entries[0].Get(RunKeys.StandingRank).AsLong);
    }

    // ---- upload-entry-video! with a fake uploader ----

    private sealed class FakeUploader(UploadResult result, bool diagnosticsThrow = false) : IVideoUploader
    {
        public List<string> Log { get; } = [];

        public Task<UploadResult> UploadVideoAsync(long serverId, string videoPath, long? offsetMs,
            Action<long, long>? progress, CancellationToken cancellationToken)
        {
            Log.Add($"upload {serverId} {videoPath} {offsetMs?.ToString() ?? "-"}");
            progress?.Invoke(50, 200);
            progress?.Invoke(51, 200);
            progress?.Invoke(100, 200);
            return Task.FromResult(result);
        }

        public Task SendDiagnosticsAsync(long serverId, CancellationToken cancellationToken)
        {
            Log.Add($"diagnostics {serverId}");
            return diagnosticsThrow ? Task.FromException(new IOException("down")) : Task.CompletedTask;
        }
    }

    private (RunQueue Queue, RunEntry Entry) UploadStore(long now = 1000)
    {
        var q = TestStore(_dir, () => now, _ => true, "(:status :submitted :server-id 7 :video-path \"v.mp4\" :video-offset-ms 1234)");
        return (q, q.Entries[0]);
    }

    [Theory]
    [InlineData(UploadOutcome.Attached, "held", true, false)]
    [InlineData(UploadOutcome.Attached, "approved", false, true)]
    [InlineData(UploadOutcome.Duplicate, "pending", false, false)]
    public async Task AnAttachedOrDuplicateUploadMarksTheEntryAttached(UploadOutcome outcome, string status, bool held, bool approved)
    {
        var (q, entry) = UploadStore();
        var uploader = new FakeUploader(new UploadResult(outcome, Status: status));
        var percents = new List<int?>();
        q.UploadProgressChanged += (_, _) => percents.Add(q.UploadProgressPercent(entry.Data));
        var updated = await q.UploadEntryVideoAsync(entry, uploader);
        Assert.Equal(["upload 7 v.mp4 1234", "diagnostics 7"], uploader.Log);
        Assert.True(updated.Is(RunKeys.VideoAttached) && updated.Is(RunKeys.VideoUploaded));
        Assert.Equal(held, updated.Is(RunKeys.Held));
        Assert.Equal(approved, updated.Is(RunKeys.Approved));
        Assert.False(RunEntries.IsActive(q.Entries[0].Data));
        // 25%, (25% again is skipped), 50%, then cleared.
        Assert.Equal([25, 50, null], percents);
        Assert.Null(q.CurrentUpload);
    }

    [Fact]
    public async Task APendingLimitRejectionBacksOffAnHour()
    {
        var (q, entry) = UploadStore(now: 1000);
        var updated = await q.UploadEntryVideoAsync(entry, new FakeUploader(new UploadResult(UploadOutcome.Rejected, Error: "pending-limit")));
        Assert.Equal(1000 + RunQueue.UploadLimitRetrySeconds, updated.Get(RunKeys.NextUploadAt).AsLong);
        Assert.False(updated.Is(RunKeys.UploadGivenUp));
    }

    [Theory]
    [InlineData("too-big", "File too large", "File too large")]
    [InlineData("too-big", null, "too-big")]
    [InlineData(null, null, "rejected")]
    public async Task OtherRejectionsGiveUp(string? error, string? message, string expected)
    {
        var (q, entry) = UploadStore();
        var updated = await q.UploadEntryVideoAsync(entry, new FakeUploader(new UploadResult(UploadOutcome.Rejected, Error: error, Message: message), diagnosticsThrow: true));
        Assert.True(updated.Is(RunKeys.UploadGivenUp));
        Assert.Equal(expected, updated.Get(RunKeys.UploadError).AsString);
    }

    [Fact]
    public async Task ATransportFailureBacksOffFiveMinutesWithoutDiagnostics()
    {
        var (q, entry) = UploadStore(now: 1000);
        var uploader = new FakeUploader(UploadResult.ApiError("WinHttpSendRequest failed"));
        var updated = await q.UploadEntryVideoAsync(entry, uploader);
        Assert.Equal(1000 + RunQueue.UploadRetrySeconds, updated.Get(RunKeys.NextUploadAt).AsLong);
        Assert.Equal("WinHttpSendRequest failed", updated.Get(RunKeys.UploadError).AsString);
        Assert.DoesNotContain(uploader.Log, l => l.StartsWith("diagnostics", StringComparison.Ordinal));
        Assert.Equal(1, updated.Get(RunKeys.UploadFailures).AsLong);
    }

    [Fact]
    public async Task TwelveTransportFailuresInARowGiveUpLikeARejection()
    {
        var (q, entry) = UploadStore(now: 1000);
        var uploader = new FakeUploader(UploadResult.ApiError("connection reset"));
        for (var i = 1; i < RunQueue.MaxUploadFailures; i++)
        {
            var backingOff = await q.UploadEntryVideoAsync(entry, uploader);
            Assert.False(backingOff.Is(RunKeys.UploadGivenUp));
            Assert.Equal(i, backingOff.Get(RunKeys.UploadFailures).AsLong);
        }
        Assert.DoesNotContain(uploader.Log, l => l.StartsWith("diagnostics", StringComparison.Ordinal));
        var updated = await q.UploadEntryVideoAsync(entry, uploader);
        Assert.True(updated.Is(RunKeys.UploadGivenUp));
        Assert.Equal("connection reset", updated.Get(RunKeys.UploadError).AsString);
        Assert.Equal("diagnostics 7", uploader.Log[^1]);
        Assert.False(RunEntries.IsActive(q.Entries[0].Data));
        Assert.Null(q.UploadCandidate(1_000_000).Candidate);
        Assert.Equal("upload failed", RunDisplay.RunVideoLabel(q.Entries[0].Data, Language.En, null));
    }

    [Fact]
    public async Task TheFailureCountSurvivesARestart()
    {
        var (q, entry) = UploadStore(now: 1000);
        await q.UploadEntryVideoAsync(entry, new FakeUploader(UploadResult.ApiError("down")));
        await q.UploadEntryVideoAsync(entry, new FakeUploader(UploadResult.ApiError("down")));
        var reloaded = new RunQueue(q.Path, () => 1000, _ => true);
        reloaded.Load();
        Assert.Equal(2, reloaded.Entries.Single().Get(RunKeys.UploadFailures).AsLong);
    }

    [Fact]
    public async Task AServerReplyEndsTheFailureStreak()
    {
        var (q, entry) = UploadStore(now: 1000);
        for (var i = 1; i < RunQueue.MaxUploadFailures; i++)
            await q.UploadEntryVideoAsync(entry, new FakeUploader(UploadResult.ApiError("down")));
        var limited = await q.UploadEntryVideoAsync(entry, new FakeUploader(new UploadResult(UploadOutcome.Rejected, Error: "pending-limit")));
        Assert.Null(limited.Data.Get(RunKeys.UploadFailures));
        var again = await q.UploadEntryVideoAsync(entry, new FakeUploader(UploadResult.ApiError("down")));
        Assert.False(again.Is(RunKeys.UploadGivenUp));
        Assert.Equal(1, again.Get(RunKeys.UploadFailures).AsLong);
    }

    [Fact]
    public async Task AnUnreachedServerBacksOffWithoutAStrike()
    {
        var (q, entry) = UploadStore(now: 1000);
        for (var i = 0; i < RunQueue.MaxUploadFailures + 3; i++)
        {
            var updated = await q.UploadEntryVideoAsync(entry, new FakeUploader(UploadResult.ApiError("name not resolved", serverUnreached: true)));
            Assert.False(updated.Is(RunKeys.UploadGivenUp));
            Assert.Null(updated.Data.Get(RunKeys.UploadFailures));
            Assert.Equal(1000 + RunQueue.UploadRetrySeconds, updated.Get(RunKeys.NextUploadAt).AsLong);
        }
    }

    [Fact]
    public async Task AnUploadWithoutAStreakAddsNoFailureKey()
    {
        var (q, entry) = UploadStore();
        var updated = await q.UploadEntryVideoAsync(entry, new FakeUploader(new UploadResult(UploadOutcome.Attached, Status: "held")));
        Assert.Null(updated.Data.Get(RunKeys.UploadFailures));
    }

    [Fact]
    public void UploadResultFromJson()
    {
        using var doc = JsonDocument.Parse("{\"status\":\"held\",\"error\":\"pending-limit\",\"message\":\"m\"}");
        Assert.Equal(new UploadResult(UploadOutcome.Attached, "held", "pending-limit", "m"),
            UploadResult.FromJson(UploadOutcome.Attached, doc.RootElement));
    }
}
