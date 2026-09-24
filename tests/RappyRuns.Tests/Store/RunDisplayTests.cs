using RappyRuns.Core.I18n;
using RappyRuns.Core.Store;
using static RappyRuns.Tests.Store.StoreTestSupport;

namespace RappyRuns.Tests.Store;

/// <summary>
/// store.lisp display helpers: tests-helpers.lisp run-ux-helper-tests (labels,
/// times, standing notes and toasts), tests-misc.lisp / tests-recorder.lisp
/// label checks, tests-ghost.lisp split clock, and the SBCL golden over every
/// helper in both languages.
/// </summary>
public class RunDisplayTests
{
    private const Language En = Language.En;

    private static string Status(string entry, bool token = true, bool upload = true) =>
        RunDisplay.RunStatusLabel(P(entry), En, token, upload);

    // ---- status labels ----

    [Fact]
    public void SubmittedLabelDoesNotLeakTheUrlAndHintsAtTheVideoStep()
    {
        var label = Status("(:status :submitted :url \"https://example.com/runs/42\")");
        Assert.True(!label.Contains("http"), "submitted label does not leak the URL");
        Assert.True(label.Contains("draft") && label.Contains("video"), "submitted label says draft and hints at the video step");
    }

    [Theory]
    [InlineData("rejected label carries the reason", "(:status :rejected :reason \"too fast\")", "too fast", null)]
    [InlineData("status label: aborted drafts say the recording stays local", "(:status :submitted :aborted t :video-path \"v.mp4\")", "aborted", null)]
    [InlineData("status label: unranked drafts read record only", "(:status :submitted :unranked t)", "record only", null)]
    [InlineData("status label: saved video announces the automatic upload", "(:status :submitted :video-path \"v.mp4\")", "automatically", null)]
    [InlineData("status label: a given-up upload points back at the Upload button", "(:status :submitted :video-path \"v.mp4\" :upload-given-up t)", "Upload", null)]
    [InlineData("status label: attached video says awaiting review", "(:status :submitted :video-attached t)", "awaiting review", null)]
    [InlineData("status label: a held upload points at the browser, not review", "(:status :submitted :video-attached t :held t)", "publish", "awaiting review")]
    [InlineData("status label: an approved attached video is not awaiting review", "(:status :submitted :video-attached t :approved t)", "approved", "awaiting review")]
    [InlineData("status label: an aborted attached video is not awaiting review", "(:status :submitted :video-attached t :aborted t)", "aborted", "awaiting review")]
    public void StatusLabels(string label, string entry, string contains, string? notContains)
    {
        var text = Status(entry);
        Assert.True(text.Contains(contains, StringComparison.Ordinal), $"{label}: {text}");
        if (notContains is not null) Assert.True(!text.Contains(notContains, StringComparison.Ordinal), $"{label}: {text}");
    }

    [Fact]
    public void SavedVideoPointsAtTheUploadButtonWhenAutoUploadIsOff() =>
        Assert.Contains("Upload", Status("(:status :submitted :video-path \"v.mp4\")", upload: false));

    [Fact]
    public void QueuedLabelWithoutAnyTokenSaysTheQueueIsParked() =>
        Assert.Contains("saved on this PC", Status("(:status :queued)", token: false));

    [Fact]
    public void QueuedLabelWithAGuestTokenStaysThePlainQueued() =>
        Assert.Equal("queued", Status("(:status :queued)", token: true));

    [Fact]
    public void TheSubmittedStatusLabelAppendsTheStandingNote()
    {
        var label = Status("(:status :submitted :standing-rank 2 :standing-parties 4 :standing-delta-ms -1000)");
        Assert.True(label.Contains("draft") && label.Contains("#2"), label);
    }

    // ---- video labels ----

    [Theory]
    [InlineData("video label: uploaded", "(:video-path \"v.mp4\" :video-attached t :video-uploaded t)", "uploaded")]
    [InlineData("video label: manual attach still reads attached", "(:video-path \"v.mp4\" :video-attached t)", "attached")]
    [InlineData("video label: given up", "(:video-path \"v.mp4\" :upload-given-up t)", "upload failed")]
    [InlineData("video label: saved recording", "(:video-path \"v.mp4\")", "saved")]
    [InlineData("video label: no recording", "(:status :queued)", "")]
    public void VideoLabels(string label, string entry, string expected) =>
        Assert.True(expected == RunDisplay.RunVideoLabel(P(entry), En, null), label);

    [Fact]
    public void AnInFlightUploadShowsItsPercentOnlyOnItsOwnEntry()
    {
        using var dir = new TempDir();
        var queue = new RunQueue(dir.File("q.sexp"));
        // The Lisp test bound *upload-progress* to (7 50 200); here the
        // progress comes from a running upload, so check the pure pieces.
        var progress = new UploadProgress(7, 50, 200);
        Assert.Equal(25, progress.Percent);
        Assert.Contains("25%", RunDisplay.RunVideoLabel(P("(:server-id 7 :video-path \"v.mp4\")"), En, progress.Percent));
        Assert.Null(queue.UploadProgressPercent(P("(:server-id 8 :video-path \"v.mp4\")")));
        Assert.Equal("saved", RunDisplay.RunVideoLabel(P("(:server-id 8 :video-path \"v.mp4\")"), En, null));
    }

    // ---- times ----

    [Fact]
    public void FormatRunTimeFormatsMinutesSecondsMillis() => Assert.Equal("9:59.123", RunDisplay.FormatRunTime(599123));

    [Fact]
    public void FormatImprovementMsShowsASubMinuteDeltaInSeconds() => Assert.Equal("3.21s", RunDisplay.FormatImprovementMs(3210));

    [Theory]
    [InlineData("split clock formats m:ss", 754321, "12:34")]
    [InlineData("split clock zero-pads seconds", 61000, "1:01")]
    public void SplitClock(string label, long ms, string expected) =>
        Assert.True(expected == RunDisplay.FormatSplitClock(ms), label);

    [Fact]
    public void TimeFormatsMatchLispOnTheGoldenTable()
    {
        var g = Golden.Load("store/formats.json");
        foreach (var row in g.GetProperty("runTime").EnumerateArray())
            Assert.Equal(row.GetProperty("text").GetString(), RunDisplay.FormatRunTime(row.GetProperty("ms").GetInt64()));
        foreach (var row in g.GetProperty("splitClock").EnumerateArray())
            Assert.Equal(row.GetProperty("text").GetString(), RunDisplay.FormatSplitClock(row.GetProperty("ms").GetInt64()));
        var count = 0;
        foreach (var row in g.GetProperty("improvement").EnumerateObject())
        {
            var ms = long.Parse(row.Name, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(row.Value.GetString() == RunDisplay.FormatImprovementMs(ms), $"format-improvement-ms {ms}");
            count++;
        }
        Assert.True(count > 6000);
    }

    // ---- standing notes ----

    [Theory]
    [InlineData("run-standing-note leads with the personal best, then the rank", "(:standing-rank 2 :standing-parties 5 :standing-delta-ms -3210)", new[] { "PB! -3.21s", "#2" }, null)]
    [InlineData("run-standing-note behind the own best shows the gap, still ranked", "(:standing-rank 3 :standing-parties 8 :standing-delta-ms 1500)", new[] { "1.50", "#3" }, null)]
    [InlineData("run-standing-note on a solo board hides the rank on a PB", "(:standing-rank 1 :standing-parties 1 :standing-delta-ms -3210)", new[] { "PB! -3.21s" }, "#")]
    [InlineData("run-standing-note on a solo board shows the gap behind the own best", "(:standing-rank 1 :standing-parties 1 :standing-delta-ms 16830)", new[] { "16.83s" }, "#")]
    [InlineData("run-standing-note marks a first run when there is no prior time", "(:standing-rank 1 :standing-parties 1)", new[] { "first run" }, "#")]
    public void StandingNotes(string label, string entry, string[] contains, string? notContains)
    {
        var note = RunDisplay.RunStandingNote(P(entry), En);
        Assert.True(note is not null, label);
        Assert.True(contains.All(c => note.Contains(c, StringComparison.Ordinal)), $"{label}: {note}");
        if (notContains is not null) Assert.True(!note.Contains(notContains, StringComparison.Ordinal), $"{label}: {note}");
    }

    [Fact]
    public void RunStandingNoteIsNilWhenTheServerSentNoStanding() =>
        Assert.Null(RunDisplay.RunStandingNote(P("(:status :submitted)"), En));

    // ---- standing toasts ----

    [Fact]
    public void StandingToastCelebratesAProvisionalNumberOneOverAnotherParty()
    {
        var toast = RunDisplay.StandingToast(P("(:quest-name \"Lost HEAT SWORD\" :time-ms 599123 :standing-rank 1 :standing-parties 5 :standing-delta-ms -3210)"), En);
        Assert.True(toast is { } t && t.Title.Contains("#1") && t.Text.Contains("Lost HEAT SWORD") && t.Text.Contains("9:59.123"));
    }

    [Fact]
    public void StandingToastCelebratesATop3EntryThatBeatSomebody()
    {
        var toast = RunDisplay.StandingToast(P("(:quest-name \"Q\" :time-ms 60000 :standing-rank 3 :standing-parties 8)"), En);
        Assert.True(toast is { } t && t.Title.Contains("#3") && t.Text.Contains("of 8"));
    }

    [Fact]
    public void StandingToastStaysQuietInLastPlaceEvenInsideTheTop3() =>
        Assert.Null(RunDisplay.StandingToast(P("(:quest-name \"Q\" :time-ms 60000 :standing-rank 3 :standing-parties 3 :standing-delta-ms 1500)"), En));

    [Fact]
    public void StandingToastOnASoloBoardCelebratesThePbNeverNumberOneOfOne()
    {
        var toast = RunDisplay.StandingToast(P("(:quest-name \"Q\" :time-ms 60000 :standing-rank 1 :standing-parties 1 :standing-delta-ms -3210)"), En);
        Assert.True(toast is { } t && !t.Title.Contains('#') && t.Text.Contains("3.21s"));
    }

    [Fact]
    public void StandingToastMarksAFirstRunOnASoloBoardWithoutARank()
    {
        var toast = RunDisplay.StandingToast(P("(:quest-name \"Q\" :time-ms 60000 :standing-rank 1 :standing-parties 1)"), En);
        Assert.True(toast is { } t && t.Title.Contains("First") && !t.Text.Contains('#'));
    }

    [Fact]
    public void StandingToastSaysNothingWhenBehindYourOwnBestMidBoard() =>
        Assert.Null(RunDisplay.StandingToast(P("(:quest-name \"Q\" :time-ms 60000 :standing-rank 5 :standing-parties 8 :standing-delta-ms 1500)"), En));

    [Fact]
    public void StandingToastIsNilWithoutAStanding() =>
        Assert.Null(RunDisplay.StandingToast(P("(:quest-name \"Q\" :time-ms 60000 :status :submitted)"), En));

    [Fact]
    public void ToastForPrefersTheStandingThenTheGhostAndCarriesTheUrl()
    {
        var standing = RunDisplay.ToastFor(P("(:quest-name \"Q\" :time-ms 60000 :standing-rank 1 :standing-parties 3 :ghost-delta-ms -5 :url \"https://x/runs/1\")"), En);
        Assert.Equal("https://x/runs/1", standing!.Url);
        Assert.Contains("#1", standing.Title);
        var ghost = RunDisplay.ToastFor(P("(:quest-name \"Q\" :time-ms 60000 :standing-rank 5 :standing-parties 8 :standing-delta-ms 1500 :ghost-delta-ms -1125)"), En);
        Assert.Equal("Ghost beaten!", ghost!.Title);
        Assert.Null(RunDisplay.ToastFor(P("(:quest-name \"Q\" :time-ms 60000)"), En));
    }

    // ---- everything against Lisp, both languages ----

    [Fact]
    public void EveryHelperMatchesLispOnTheGoldenEntries()
    {
        var rows = 0;
        foreach (var row in Golden.Load("store/display.json").EnumerateArray())
        {
            var text = row.GetProperty("entry").GetString()!;
            var entry = P(text);
            var language = Languages.FromCode(row.GetProperty("language").GetString());
            string? Str(string name) => row.GetProperty(name).GetString();
            var why = $"{language} {text}";

            foreach (var label in row.GetProperty("status").EnumerateObject())
            {
                var token = label.Name.StartsWith("token", StringComparison.Ordinal);
                var upload = label.Name.EndsWith("-upload", StringComparison.Ordinal);
                Assert.True(label.Value.GetString() == RunDisplay.RunStatusLabel(entry, language, token, upload), $"status {label.Name} {why}");
            }
            // The golden was taken with an upload of server 7 at 50/200 in flight.
            var percent = RunEntries.Get(entry, RunKeys.ServerId).AsLong == 7 ? new UploadProgress(7, 50, 200).Percent : (int?)null;
            Assert.True(Str("video") == RunDisplay.RunVideoLabel(entry, language, percent), $"video {why}");
            Assert.True(Str("note") == RunDisplay.EntryNote(entry, language), $"note {why}");
            Assert.True(Str("standingNote") == RunDisplay.RunStandingNote(entry, language), $"standing note {why}");
            Assert.True(Str("ghostNote") == RunDisplay.GhostNote(entry, language), $"ghost note {why}");
            AssertToast(row.GetProperty("standingToast"), RunDisplay.StandingToast(entry, language), $"standing toast {why}");
            AssertToast(row.GetProperty("ghostToast"), RunDisplay.GhostToast(entry, language), $"ghost toast {why}");
            rows++;
        }
        Assert.True(rows > 60);
    }

    private static void AssertToast(System.Text.Json.JsonElement expected, (string Title, string Text)? actual, string why)
    {
        if (expected.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            Assert.True(actual is null, why);
            return;
        }
        Assert.True(actual is not null, why);
        Assert.True(expected.GetProperty("title").GetString() == actual.Value.Title, $"{why} title {actual.Value.Title}");
        Assert.True(expected.GetProperty("text").GetString() == actual.Value.Text, $"{why} text {actual.Value.Text}");
    }
}
