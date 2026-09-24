using RappyRuns.Core.Ghost;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Tests.Ghost;

/// <summary>
/// Port of run-ghost-tests (client/tests/tests-ghost.lisp:20-247): split
/// parsing, streaming room alignment, formatting, the completion annotation
/// and the fetch gate. DisplayName = the Lisp check label. The three
/// room-event telemetry checks there belong to the telemetry port.
/// </summary>
public class GhostTests
{
    internal const string GhostPayload = """
        {"run_id":42,"quest":"ep1-towards-the-future","time_ms":123456,
         "submitter":"teapot","precision":"ms","source":"pb","pb":0,
         "rooms":[{"floor":1,"room":10,"nth":1,"enter_ms":0},
                  {"floor":1,"room":11,"nth":1,"enter_ms":30000},
                  {"floor":1,"room":10,"nth":2,"enter_ms":60000},
                  {"floor":2,"room":1,"nth":1,"enter_ms":90000}]}
        """;

    internal static GhostReference TestGhost() => GhostReference.Parse(GhostPayload)!;

    private static Plist Run(string slug, long timeMs, params (string Key, SexpNode Value)[] extra)
    {
        var run = new Plist();
        foreach (var (key, value) in extra.Reverse()) run.Set(key, value);
        run.Set("time-ms", SexpNode.Int(timeMs));
        run.Set("quest-slug", SexpNode.Str(slug));
        return run;
    }

    [Fact(DisplayName = "ghost payload parses")]
    public void PayloadParses()
    {
        var ghost = TestGhost();
        Assert.Equal(123456, ghost.TimeMs);
        Assert.Equal("ep1-towards-the-future", ghost.QuestSlug);
        Assert.Equal("teapot", ghost.Label);
        Assert.Equal("pb", ghost.Source);
        Assert.Equal(0, ghost.Pb);
        Assert.Equal(GhostPrecision.Ms, ghost.Precision);
        Assert.Equal(4, ghost.Rooms.Count);
    }

    [Fact(DisplayName = "malformed payload parses to NIL")]
    public void MalformedPayload()
    {
        Assert.Null(GhostReference.Parse("""{"rooms":[]}"""));
        Assert.Null(GhostReference.Parse((string?)null));
    }

    // Streaming greedy alignment: a cursor over the ghost's room list, the
    // same progression-order identity /compare aligns rooms by. Each check
    // replays the Lisp sequence up to its own step.
    private static readonly (long Floor, long Room, long Ms)[] AlignmentSteps =
        [(1, 10, 800), (1, 10, 5000), (1, 11, 34000), (1, 99, 40000), (1, 10, 55000)];

    private static (GhostRace Race, long? Last) Replay(int steps)
    {
        var race = new GhostRace(TestGhost());
        long? last = null;
        foreach (var (floor, room, ms) in AlignmentSteps.Take(steps)) last = race.NoteRoom(floor, room, ms);
        return (race, last);
    }

    [Fact(DisplayName = "start room matches at once")]
    public void StartRoomMatches() => Assert.Equal(800, Replay(1).Last);

    [Fact(DisplayName = "staying in the room does not rematch")]
    public void StayingDoesNotRematch() => Assert.Equal(800, Replay(2).Last);

    [Fact(DisplayName = "next room updates the gap")]
    public void NextRoomUpdates() => Assert.Equal(4000, Replay(3).Last);

    [Fact(DisplayName = "a room the ghost never entered keeps the previous gap")]
    public void UnknownRoomKeepsGap() => Assert.Equal(4000, Replay(4).Last);

    [Fact(DisplayName = "a revisit matches the ghost's next visit of that room")]
    public void RevisitMatchesNextVisit() => Assert.Equal(-5000, Replay(5).Last);

    [Fact(DisplayName = "matched-room count")]
    public void MatchedRoomCount() => Assert.Equal(3, Replay(5).Race.MatchedRooms);

    // A room the GHOST passed through but the live side skips must not desync
    // later matches: the cursor lookahead steps over it.
    [Fact(DisplayName = "skipping a ghost room still matches the one after it")]
    public void SkippedGhostRoom()
    {
        var race = new GhostRace(TestGhost());
        race.NoteRoom(1, 10, 500);
        Assert.Equal(2000, race.NoteRoom(2, 1, 92000));
    }

    // A late-arriving ghost joins mid-route: the race tracked the progression
    // from the start, and the cursor finds the current position on the next
    // room change.
    [Fact(DisplayName = "no ghost yet, no gap")]
    public void NoGhostNoGap() => Assert.Null(new GhostRace().NoteRoom(1, 10, 800));

    [Fact(DisplayName = "late-attached ghost matches from the next room on")]
    public void LateAttach()
    {
        var race = new GhostRace();
        race.NoteRoom(1, 10, 800);
        race.Ghost = TestGhost();
        Assert.Equal(1000, race.NoteRoom(1, 11, 31000));
    }

    // The lookahead bound keeps a detour from consuming the whole list.
    private static GhostRace FarRace()
    {
        var rooms = Enumerable.Range(0, 20)
            .Select(i => (GhostRoom?)new GhostRoom(1, 100 + i, 1, 1000L * i, null))
            .Append(new GhostRoom(9, 9, 1, 99000, null))
            .ToList();
        return new GhostRace(new GhostReference("q", null, 1, null, null, null, GhostPrecision.Ms, rooms, null));
    }

    [Fact(DisplayName = "a match past the lookahead window is not taken")]
    public void LookaheadNotTaken() => Assert.Null(FarRace().NoteRoom(9, 9, 500));

    [Fact(DisplayName = "the unmatched entry leaves the cursor in place")]
    public void LookaheadCursorStays()
    {
        var race = FarRace();
        race.NoteRoom(9, 9, 500);
        Assert.Equal(1500, race.NoteRoom(1, 100, 1500));
    }

    [Fact(DisplayName = "status suffix shows the target before any match")]
    public void StatusSuffixTarget() => Assert.Equal(" | vs 2:03.456", GhostFormat.StatusSuffix(new GhostRace(TestGhost())));

    [Fact(DisplayName = "status suffix shows the gap after a match")]
    public void StatusSuffixGap()
    {
        var race = new GhostRace(TestGhost()) { DeltaMs = 4000 };
        Assert.Equal(" | vs 2:03.456 +4.0s", GhostFormat.StatusSuffix(race));
    }

    [Fact(DisplayName = "race without an attached ghost shows nothing")]
    public void SuffixWithoutGhost() => Assert.Null(GhostFormat.StatusSuffix(new GhostRace()));

    [Fact(DisplayName = "no race, no suffix")]
    public void SuffixWithoutRace() => Assert.Null(GhostFormat.StatusSuffix(null));

    // Completion annotation.
    private static IReadOnlyList<Plist> Annotated() => GhostFinish.AnnotateRuns(TestGhost(),
    [
        Run("ep1-towards-the-future", 113456),
        Run("some-other-quest", 113456),
        Run("ep1-towards-the-future", 113456, ("aborted", SexpNode.T)),
        // The live run discharged a Photon Blast: another board, no comparison.
        Run("ep1-towards-the-future", 113456, ("pb", SexpNode.T)),
    ]);

    [Fact(DisplayName = "beat-the-ghost delta stamped")]
    public void DeltaStamped()
    {
        var run = Annotated()[0];
        Assert.Equal(-10000, run.Get("ghost-delta-ms")?.AsLong);
        Assert.Equal(123456, run.Get("ghost-time-ms")?.AsLong);
        Assert.Equal("teapot", run.Get("ghost-label")?.AsString);
        // LIST* order: the stamp leads the plist.
        Assert.Equal(["GHOST-DELTA-MS", "GHOST-TIME-MS", "GHOST-LABEL", "QUEST-SLUG", "TIME-MS"], run.Keys);
    }

    [Fact(DisplayName = "other quest untouched")]
    public void OtherQuestUntouched() => Assert.Null(Annotated()[1].Get("ghost-delta-ms"));

    [Fact(DisplayName = "aborted run untouched")]
    public void AbortedUntouched() => Assert.Null(Annotated()[2].Get("ghost-delta-ms"));

    [Fact(DisplayName = "pb category mismatch withholds the comparison")]
    public void PbMismatch() => Assert.Null(Annotated()[3].Get("ghost-delta-ms"));

    // An explicitly chosen target compares across pb categories - the user
    // picked it on purpose.
    [Fact(DisplayName = "a chosen target ignores the pb dimension")]
    public void ChosenTargetIgnoresPb()
    {
        var target = TestGhost() with { Source = "target" };
        Assert.True(GhostFinish.CoversRun(target, Run("ep1-towards-the-future", 1, ("pb", SexpNode.T))));
    }

    [Fact(DisplayName = "no ghost leaves runs alone")]
    public void NoGhostLeavesRunsAlone()
    {
        IReadOnlyList<Plist> runs = [Run("q", 1)];
        Assert.Same(runs, GhostFinish.AnnotateRuns(null, runs));
    }

    [Fact(DisplayName = "ms gap formats with one decimal")]
    public void MsGap() => Assert.Equal("-3.2s", GhostFormat.Delta(-3210, GhostPrecision.Ms));

    [Fact(DisplayName = "sec gap formats whole seconds")]
    public void SecGap() => Assert.Equal("+4s", GhostFormat.Delta(4400, GhostPrecision.Sec));

    [Fact(DisplayName = "sec gap rounds")]
    public void SecGapRounds() => Assert.Equal("-2s", GhostFormat.Delta(-1600, GhostPrecision.Sec));

    [Fact(DisplayName = "url encoding passes unreserved and encodes spaces")]
    public void UrlEncodingSpaces() => Assert.Equal("Very%20Hard", GhostFetchRequest.UrlEncodeComponent("Very Hard"));

    [Fact(DisplayName = "url encoding is utf-8 percent-encoded")]
    public void UrlEncodingUtf8() => Assert.Equal("A%E3%81%82", GhostFetchRequest.UrlEncodeComponent("Aあ"));

    // Fetch gating: one ask per quest load, refetch after a lobby visit (even
    // at the same allocation address), none without the setting.
    private static GhostLoadInfo Load(bool enabled = true) =>
        new(["ep1-towards-the-future", "ep1-towards-the-future-solo"], "Ultimate", 1, enabled, HasSubmissionToken: true, AccountMode: null);

    [Fact(DisplayName = "fetch wanted on a fresh quest load")]
    public void FetchWantedFresh()
    {
        var request = new GhostSession().FetchWanted(4660, "Towards the Future", () => Load());
        Assert.NotNull(request);
        Assert.Equal("ep1-towards-the-future", request.Slugs[0]);
        Assert.Equal("Ultimate", request.Difficulty);
        Assert.Equal(1, request.PartySize);
    }

    [Fact(DisplayName = "same load never asks twice")]
    public void FetchOncePerLoad()
    {
        var session = new GhostSession();
        session.FetchWanted(4660, "Towards the Future", () => Load());
        Assert.Null(session.FetchWanted(4660, "Towards the Future", () => Load()));
    }

    [Fact(DisplayName = "reload after a lobby visit asks again")]
    public void FetchAfterLobby()
    {
        var session = new GhostSession();
        session.FetchWanted(4660, "Towards the Future", () => Load());
        session.FetchWanted(4660, "Towards the Future", () => Load());
        // Lobby (no quest loaded) forgets the pointer, so reloading the quest
        // at the same address fetches again.
        session.FetchWanted(0, null, () => Load());
        Assert.Equal("ep1-towards-the-future", session.FetchWanted(4660, "Towards the Future", () => Load())?.Slugs[0]);
    }

    [Fact(DisplayName = "setting off never asks")]
    public void FetchGateSettingOff() =>
        Assert.Null(new GhostSession().FetchWanted(4660, "Towards the Future", () => Load(enabled: false)));

    // --- Beyond the Lisp checks: the orchestration the Lisp tests cannot reach.

    [Fact(DisplayName = "fetch request path carries the other slugs, difficulty, party size and pb=0")]
    public void FetchPath()
    {
        var request = new GhostFetchRequest(["a", "b", "c"], "Very Hard", 2);
        Assert.Equal("/api/quests/a/ghost?slugs=b,c&difficulty=Very%20Hard&party_size=2&pb=0", request.PathAndQuery());
        Assert.Equal("/api/quests/a/ghost?party_size=1&pb=0", new GhostFetchRequest(["a"], null, 1).PathAndQuery());
        // S25: the session's account mode rides along when it is known.
        Assert.Equal("/api/quests/a/ghost?party_size=1&pb=0&account_mode=sandbox",
            new GhostFetchRequest(["a"], null, 1, AccountMode: "sandbox").PathAndQuery());
    }

    [Fact(DisplayName = "a fetch reply for a superseded load is dropped")]
    public async Task StaleFetchDropped()
    {
        var session = new GhostSession();
        var gate = new TaskCompletionSource<string?>();
        var task = session.MaybeStartFetch(4660, "q", () => Load(), _ => gate.Task);
        Assert.NotNull(task);
        session.FetchWanted(0, null, () => Load()); // back to the lobby
        gate.SetResult(GhostPayload);
        await task;
        Assert.Null(session.Ghost);

        var fresh = session.MaybeStartFetch(4661, "q", () => Load(), _ => Task.FromResult<string?>(GhostPayload));
        await fresh!;
        Assert.Equal(123456, session.Ghost?.TimeMs);
    }

    [Fact(DisplayName = "Reset (the game exited) drops the ghost, the race and a fetch still in flight (S37)")]
    public async Task ResetDropsInFlightFetch()
    {
        var session = new GhostSession();
        var gate = new TaskCompletionSource<string?>();
        var task = session.MaybeStartFetch(4660, "q", () => Load(), _ => gate.Task);
        session.Step(true, new CameraState(0, 0, 0, 0, 0, 1, 1), new GhostPlayer(1, 10, 5f), 800, _ => true);
        Assert.NotNull(session.Race);
        session.Reset();
        Assert.Null(session.Race);
        Assert.Null(session.LiveCamera);
        gate.SetResult(GhostPayload);
        await task!;
        Assert.Null(session.Ghost);
    }

    [Fact(DisplayName = "a failing fetch leaves no ghost")]
    public async Task FailingFetch()
    {
        var session = new GhostSession();
        await session.MaybeStartFetch(1, "q", () => Load(), _ => throw new HttpRequestException("401"))!;
        Assert.Null(session.Ghost);
    }

    [Fact(DisplayName = "step tracks rooms from quest start, attaches only a matching ghost, and drops state outside a quest")]
    public void StepLifecycle()
    {
        var session = new GhostSession();
        var camera = new CameraState(0, 0, 0, 0, 0, 1, 1);
        session.Step(true, camera, new GhostPlayer(1, 10, 5f), 800, _ => true);
        Assert.NotNull(session.Race);
        Assert.Same(camera, session.LiveCamera);
        Assert.Null(session.Race.DeltaMs);

        session.Ghost = TestGhost();
        session.Step(true, camera, new GhostPlayer(1, 10, 5f), 900, slug => slug == "other-quest");
        Assert.Null(session.Race!.Ghost); // a stale ghost of another quest never races

        session.Step(true, camera, new GhostPlayer(1, 11, 5f), 31000, slug => slug == "ep1-towards-the-future");
        Assert.Equal(1000, session.Race!.DeltaMs);
        Assert.Equal(" +1.0s", session.TitleSuffix());
        Assert.NotNull(session.OverlayData(31000, marker: true));

        session.Step(false, camera, null, null, _ => true);
        Assert.Null(session.Race);
        Assert.Null(session.LiveCamera);
    }
}
