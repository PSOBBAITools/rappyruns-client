using RappyRuns.Core.Ghost;
using RappyRuns.Core.I18n;
using RappyRuns.Core.Sexp;

namespace RappyRuns.Tests.Ghost;

/// <summary>
/// Port of run-ghost-overlay-tests (client/tests/tests-ghost.lisp:249-465):
/// track parsing and interpolation, the split history and panel snapshot,
/// camera projection and FOV, panel placement. DisplayName = the Lisp check
/// label. The three track-recording telemetry checks there belong to the
/// telemetry port.
/// </summary>
public class GhostOverlayTests
{
    private const string TrackedGhostPayload = """
        {"run_id":7,"quest":"q","time_ms":6000,"precision":"ms",
         "source":"pb","pb":0,"rooms":[],
         "track":[[0,1,10,0.0,0.0],[1000,1,10,10.0,20.0],
                  [5000,2,11,50.0,50.0],"junk",[1,2]]}
        """;

    private const string HeightsPayload = """
        {"run_id":8,"quest":"q","time_ms":6000,
         "precision":"ms","source":"pb","pb":0,
         "rooms":[],
         "track":[[0,1,10,0.0,0.0],
                  "junk",
                  [1000,1,10,10.0,20.0],
                  [2000,1,10,20.0,20.0]],
         "track_y":[5.0,99.0,15.0]}
        """;

    private static IReadOnlyList<TrackRow> Track() => GhostReference.Parse(TrackedGhostPayload)!.Track!;

    private static IReadOnlyList<TrackRow> HeightTrack() => GhostReference.Parse(HeightsPayload)!.Track!;

    [Fact(DisplayName = "track parses and drops malformed rows")]
    public void TrackParses()
    {
        var track = Track();
        Assert.Equal(3, track.Count);
        Assert.Equal(new TrackRow(1000, 1, 10, 10.0, 20.0, null), track[1]);
    }

    [Fact(DisplayName = "position before the first sample is NIL")]
    public void BeforeFirstSample() => Assert.Null(GhostTrack.Position(Track(), -1));

    [Fact(DisplayName = "position interpolates on one floor")]
    public void Interpolates()
    {
        var p = GhostTrack.Position(Track(), 500)!.Value;
        Assert.Equal(1, p.Floor);
        Assert.Equal(10, p.Map);
        Assert.True(Math.Abs(p.X - 5.0) < 0.01);
        Assert.True(Math.Abs(p.Z - 10.0) < 0.01);
    }

    [Fact(DisplayName = "a floor change snaps instead of gliding")]
    public void FloorChangeSnaps()
    {
        var p = GhostTrack.Position(Track(), 3000)!.Value;
        Assert.Equal(1, p.Floor);
        Assert.Equal(10.0, p.X);
        Assert.Equal(20.0, p.Z);
    }

    [Fact(DisplayName = "past the end the dot rests on the last sample")]
    public void PastTheEnd()
    {
        var p = GhostTrack.Position(Track(), 999999)!.Value;
        Assert.Equal(2, p.Floor);
        Assert.Equal(50.0, p.X);
        Assert.Equal(50.0, p.Z);
    }

    // A wide sample gap also snaps (warps must not glide through walls).
    [Fact(DisplayName = "a gap beyond the lerp window snaps")]
    public void WideGapSnaps()
    {
        TrackRow[] gappy = [new(0, 1, 10, 0.0, 0.0, null), new(8000, 1, 10, 100.0, 0.0, null)];
        var p = GhostTrack.Position(gappy, 4000)!.Value;
        Assert.Equal(1, p.Floor);
        Assert.Equal(0.0, p.X);
    }

    [Fact(DisplayName = "a pre-height track yields NIL y")]
    public void PreHeightNullY() => Assert.Null(GhostTrack.Position(Track(), 500)!.Value.Y);

    // The height column arrives out-of-band in "track_y" (the wire keeps rows
    // 5-wide for v0.51/v0.52 clients; server hunt:wire-track). It is zipped
    // back on by RAW track index, so a dropped malformed row must not shift
    // later heights; a heights vector shorter than the track leaves the tail
    // heightless.
    [Fact(DisplayName = "out-of-band heights zip onto the rows by raw index")]
    public void HeightsZip()
    {
        var track = HeightTrack();
        Assert.Equal(3, track.Count);
        Assert.Equal(new TrackRow(0, 1, 10, 0.0, 0.0, 5.0), track[0]);
        // The junk row's 99.0 is skipped with it.
        Assert.Equal(new TrackRow(1000, 1, 10, 10.0, 20.0, 15.0), track[1]);
        // Heights exhausted: the tail row stays 5-wide.
        Assert.Null(track[2].Y);
    }

    [Fact(DisplayName = "height interpolates between samples")]
    public void HeightInterpolates()
    {
        var p = GhostTrack.Position(HeightTrack(), 500)!.Value;
        Assert.Equal(1, p.Floor);
        Assert.True(Math.Abs(p.X - 5.0) < 0.01);
        Assert.True(Math.Abs(p.Y!.Value - 10.0) < 0.01);
    }

    [Fact(DisplayName = "a lerp into a heightless row yields NIL y")]
    public void LerpIntoHeightless() => Assert.Null(GhostTrack.Position(HeightTrack(), 1500)!.Value.Y);

    // Inline six-element rows (e.g. a future wire) still parse.
    [Fact(DisplayName = "inline height-bearing rows parse too")]
    public void InlineHeights()
    {
        var ghost = GhostReference.Parse("""
            {"run_id":9,"quest":"q","time_ms":6000,
             "precision":"ms","source":"pb","pb":0,
             "rooms":[],
             "track":[[0,1,10,0.0,0.0,5.0]]}
            """);
        Assert.Equal(new TrackRow(0, 1, 10, 0.0, 0.0, 5.0), ghost!.Track![0]);
    }

    // Own-position sampling into the race state (marker support), and the
    // split history behind the overlay's room rows.
    [Fact(DisplayName = "own floor and height update")]
    public void OwnPosition()
    {
        var race = new GhostRace();
        race.NotePosition(1, 5.0f);
        Assert.Equal(1, race.OwnFloor);
        Assert.Equal(5.0f, race.OwnY);
    }

    [Fact(DisplayName = "no ghost, no overlay data")]
    public void NoGhostNoData()
    {
        var race = new GhostRace();
        race.NotePosition(1, 5.0f);
        Assert.Null(GhostOverlayData.From(race, 600, false, 0));
    }

    [Fact(DisplayName = "no position seen yet, no overlay data")]
    public void NoPositionNoData() =>
        Assert.Null(GhostOverlayData.From(new GhostRace(GhostTests.TestGhost()), 600, false, 0));

    private static GhostRace SplitRace()
    {
        var race = new GhostRace(GhostTests.TestGhost());
        race.NotePosition(1, 5.0f);
        race.NoteRoom(1, 10, 800);
        race.NoteRoom(1, 11, 34000);
        race.NoteRoom(1, 99, 40000); // ghost never went: no split
        return race;
    }

    [Fact(DisplayName = "matched rooms record splits newest first")]
    public void SplitsNewestFirst()
    {
        var splits = SplitRace().Splits;
        Assert.Equal(2, splits.Count);
        Assert.Equal(11, splits[0].Room);
        Assert.Equal(4000, splits[0].Delta);
        Assert.Equal(10, splits[1].Room);
        Assert.Equal(800, splits[1].Ms);
        Assert.Equal(800, splits[1].Delta);
    }

    [Fact(DisplayName = "overlay data carries splits, precision and marker")]
    public void OverlayDataCarries()
    {
        var data = GhostOverlayData.From(SplitRace(), 40000, marker: true, 0)!;
        Assert.Equal(1, data.Floor);
        Assert.Equal(5.0f, data.OwnY);
        Assert.Equal(GhostPrecision.Ms, data.Precision);
        Assert.True(data.Marker);
        Assert.Equal(2, data.Splits.Count);
        Assert.Equal(11, data.Splits[0].Room);
    }

    [Fact(DisplayName = "overlay data caps the split rows")]
    public void OverlayDataCaps()
    {
        var race = SplitRace();
        race.SetSplits(Enumerable.Range(1, 20).Reverse().Select(i => new GhostSplit(i, 1, i * 1000L, 0)));
        Assert.Equal(OverlayLayout.SplitRows, GhostOverlayData.From(race, 0, false, 0)!.Splits.Count);
    }

    // A room where the reference killed nothing is matched (the gap still
    // moves) but records no split row; kills absent from the wire (an older
    // server) means unknown, so those rooms keep their rows.
    private static GhostRace KillsRace()
    {
        var ghost = GhostReference.Parse("""
            {"run_id":10,"quest":"q","time_ms":99000,
             "precision":"ms","source":"pb","pb":0,
             "rooms":[{"floor":1,"room":10,"nth":1,"enter_ms":0,"kills":3},
                      {"floor":1,"room":11,"nth":1,"enter_ms":30000,"kills":0},
                      {"floor":1,"room":12,"nth":1,"enter_ms":60000,"kills":5}]}
            """);
        var race = new GhostRace(ghost);
        race.NoteRoom(1, 10, 500);
        race.NoteRoom(1, 11, 34000);
        return race;
    }

    [Fact(DisplayName = "the zero-kill room still moves the gap")]
    public void ZeroKillMovesGap() => Assert.Equal(4000, KillsRace().DeltaMs);

    [Fact(DisplayName = "a zero-kill room records no split row")]
    public void ZeroKillNoRow()
    {
        var race = KillsRace();
        race.NoteRoom(1, 12, 61000);
        Assert.Equal(3, race.MatchedRooms);
        Assert.Equal([12L, 10L], race.Splits.Select(s => s.Room));
    }

    [Fact(DisplayName = "split clock formats m:ss")]
    public void SplitClock() => Assert.Equal("12:34", GhostFormat.SplitClock(754321));

    [Fact(DisplayName = "split clock zero-pads seconds")]
    public void SplitClockPads() => Assert.Equal("1:01", GhostFormat.SplitClock(61000));

    // In-world marker projection (camera math ported from the DropBox Tracker
    // / PartyMemberTracker addons).
    private static readonly CameraState Camera = new(0f, 0f, 0f, 0f, 0f, 1f, 1);

    [Fact(DisplayName = "a point straight ahead projects to the screen center")]
    public void StraightAhead() =>
        Assert.Equal(new ScreenPoint(680, 384), GhostProjection.ScreenPosition(Camera, 1360, 768, 0.0, 0.0, 100.0));

    [Fact(DisplayName = "a point above the eye line projects above the center")]
    public void AboveEyeLine()
    {
        var p = GhostProjection.ScreenPosition(Camera, 1360, 768, 0.0, 10.0, 100.0)!.Value;
        Assert.Equal(680, p.X);
        Assert.True(p.Y < 384);
    }

    [Fact(DisplayName = "sideways offsets move sx off center and mirror")]
    public void SidewaysMirror()
    {
        var s = GhostProjection.ScreenPosition(Camera, 1360, 768, 10.0, 0.0, 100.0)!.Value.X;
        var m = GhostProjection.ScreenPosition(Camera, 1360, 768, -10.0, 0.0, 100.0)!.Value.X;
        Assert.NotEqual(680, s);
        Assert.Equal(s - 680, 680 - m);
    }

    [Fact(DisplayName = "a point behind the camera projects to NIL")]
    public void BehindCamera() => Assert.Null(GhostProjection.ScreenPosition(Camera, 1360, 768, 0.0, 0.0, -100.0));

    [Fact(DisplayName = "the eye point itself projects to NIL")]
    public void EyePoint() => Assert.Null(GhostProjection.ScreenPosition(Camera, 1360, 768, 0.0, 0.0, 0.0));

    [Fact(DisplayName = "a zeroed direction (loading screen) projects to NIL")]
    public void ZeroedDirection() =>
        Assert.Null(GhostProjection.ScreenPosition(new CameraState(0f, 0f, 0f, 0f, 0f, 0f, 1), 1360, 768, 0.0, 0.0, 100.0));

    [Fact(DisplayName = "a missing camera projects to NIL")]
    public void MissingCamera() => Assert.Null(GhostProjection.ScreenPosition(null, 1360, 768, 0.0, 0.0, 100.0));

    [Fact(DisplayName = "fov heuristic lands near 90 degrees at 16:9")]
    public void FovNear90()
    {
        var fov = GhostProjection.CameraFov(1, 1360.0f / 768.0f);
        Assert.InRange(fov, 1.5, 1.65);
        Assert.True(fov > 1.5 && fov < 1.65);
    }

    [Fact(DisplayName = "fov shrinks as the camera zooms in")]
    public void FovShrinks() => Assert.True(GhostProjection.CameraFov(0, 1.5f) > GhostProjection.CameraFov(4, 1.5f));

    // Panel placement: the :overlay-corner setting picks which corner of the
    // client area the overlay panel occupies (the top-right default sat on
    // PSO's own minimap once the ghost panel grew tall).
    private static (int, int) Origin(OverlayCorner corner, int areaW = 1360, int areaH = 768) =>
        OverlayPlacement.CornerOrigin(corner, areaW, areaH, 260, 352, 24, 16);

    [Fact(DisplayName = "top-right origin keeps the old geometry")]
    public void TopRight() => Assert.Equal((1076, 16), Origin(OverlayCorner.TopRight));

    [Fact(DisplayName = "top-left origin")]
    public void TopLeft() => Assert.Equal((24, 16), Origin(OverlayCorner.TopLeft));

    [Fact(DisplayName = "bottom-right origin")]
    public void BottomRight() => Assert.Equal((1076, 400), Origin(OverlayCorner.BottomRight));

    [Fact(DisplayName = "bottom-left origin")]
    public void BottomLeft() => Assert.Equal((24, 400), Origin(OverlayCorner.BottomLeft));

    [Fact(DisplayName = "middle-right centers vertically without a margin")]
    public void MiddleRight() => Assert.Equal((1076, 208), Origin(OverlayCorner.MiddleRight));

    [Fact(DisplayName = "middle-left origin")]
    public void MiddleLeft() => Assert.Equal((24, 208), Origin(OverlayCorner.MiddleLeft));

    [Fact(DisplayName = "top-center centers horizontally without a margin")]
    public void TopCenter() => Assert.Equal((550, 16), Origin(OverlayCorner.TopCenter));

    [Fact(DisplayName = "bottom-center origin")]
    public void BottomCenter() => Assert.Equal((550, 400), Origin(OverlayCorner.BottomCenter));

    [Fact(DisplayName = "an unknown corner places like top-right")]
    public void UnknownCorner() => Assert.Equal((1076, 16), Origin(OverlayPlacement.ParseCorner(":center")));

    [Fact(DisplayName = "an area smaller than the panel clamps to the edges")]
    public void SmallAreaClamps() => Assert.Equal((0, 0), Origin(OverlayCorner.BottomRight, 200, 200));

    // The Ctrl+dragged custom spot: fractions of the area's slack. Custom
    // values go through the config parser, as hand-edited configs do.
    private static (int, int) Panel(OverlayCorner corner, SexpNode? custom, int areaW = 1360, int areaH = 768) =>
        OverlayPlacement.PanelOrigin(corner, OverlayPlacement.ParseCustom(custom), areaW, areaH, 260, 352, 24, 16);

    private static SexpNode Floats(params float[] values) => new SList(values.Select(v => (SexpNode)new SFloat(v)).ToList());

    [Fact(DisplayName = "custom origin scales by the slack")]
    public void CustomScales() => Assert.Equal((550, 416), Panel(OverlayCorner.Custom, Floats(0.5f, 1.0f)));

    [Fact(DisplayName = "custom origin clamps out-of-range fractions")]
    public void CustomClamps() => Assert.Equal((0, 416), Panel(OverlayCorner.Custom, Floats(-0.5f, 1.5f)));

    [Fact(DisplayName = "custom without a usable position places top-right")]
    public void CustomUnusable()
    {
        Assert.Equal((1076, 16), Panel(OverlayCorner.Custom, null));
        Assert.Equal((1076, 16), Panel(OverlayCorner.Custom, SexpNode.Nil));
        Assert.Equal((1076, 16), Panel(OverlayCorner.Custom, Floats(0.5f)));
        Assert.Equal((1076, 16), Panel(OverlayCorner.Custom, new SList([new SFloat(0.5f)], new SFloat(0.7f))));
        Assert.Equal((1076, 16), Panel(OverlayCorner.Custom, SexpNode.Str("junk")));
    }

    [Fact(DisplayName = "a preset corner ignores the stored custom spot")]
    public void PresetIgnoresCustom() => Assert.Equal((24, 400), Panel(OverlayCorner.BottomLeft, Floats(0.5f, 0.5f)));

    [Fact(DisplayName = "custom in an area smaller than the panel pins to 0")]
    public void CustomSmallArea() => Assert.Equal((0, 0), Panel(OverlayCorner.Custom, Floats(0.7f, 0.7f), 200, 200));

    // --- Beyond the Lisp checks: the overlay-thread pure halves.

    [Fact(DisplayName = "overlay content builds the clock, vs line and delta color like update-ghost-overlay")]
    public void ContentForQuest()
    {
        var race = new GhostRace(GhostTests.TestGhost());
        var neutral = OverlayContent.ForQuest(61234, true, race, null, OverlayCorner.TopRight, null, Language.En);
        Assert.Equal("1:01.234 REC", neutral.Line1);
        Assert.Equal("vs 2:03.456", neutral.Line2);
        Assert.Equal(GhostDeltaState.Neutral, neutral.DeltaState);
        Assert.Equal(OverlayLayout.CompactHeight, neutral.PanelHeight);

        race.DeltaMs = -3210;
        var ahead = OverlayContent.ForQuest(null, false, race, null, OverlayCorner.TopRight, null, Language.En);
        Assert.Equal("0:00.000", ahead.Line1);
        Assert.Equal("vs 2:03.456 -3.2s", ahead.Line2);
        Assert.Equal(GhostDeltaState.Ahead, ahead.DeltaState);
        race.DeltaMs = 0;
        Assert.Equal(GhostDeltaState.Behind, OverlayContent.ForQuest(1, false, race, null, OverlayCorner.TopRight, null, Language.En).DeltaState);
        Assert.Null(OverlayContent.ForQuest(1, false, null, null, OverlayCorner.TopRight, null, Language.En).Line2);
    }

    [Fact(DisplayName = "split rows read Room n, the m:ss clock and the gap")]
    public void SplitRowTexts()
    {
        var content = OverlayContent.ForQuest(0, false, null, null, OverlayCorner.TopRight, null, Language.Ja);
        Assert.Equal(("部屋 11", "0:34", "+4.0s"), content.SplitRow(new GhostSplit(11, 1, 34000, 4000), GhostPrecision.Ms));
    }

    [Fact(DisplayName = "the marker hides after the ghost's finish and on another floor")]
    public void MarkerGates()
    {
        var data = GhostOverlayData.From(SplitRace(), 0, true, 0)! with
        {
            Track = [new TrackRow(0, 1, 10, 0.0, 100.0, null), new TrackRow(1000, 1, 10, 0.0, 100.0, null)],
            GhostTimeMs = 5000,
        };
        Assert.True(data.MarkerWanted);
        Assert.NotNull(data.GhostPositionAt(5000));
        Assert.Null(data.GhostPositionAt(5001));
        var pos = data.GhostPositionAt(500);
        // Heightless track: borrows the own height 5.0 -> slightly below center.
        var p = data.MarkerPoint(Camera, 1360, 768, pos)!.Value;
        Assert.Equal(680, p.X);
        Assert.True(p.Y < 384);
        Assert.Null((data with { Floor = 2 }).MarkerPoint(Camera, 1360, 768, pos));
        Assert.Null(data.MarkerPoint(null, 1360, 768, pos));
        Assert.Equal("teapot", data.MarkerLabel);
        Assert.Equal("ghost", (data with { Label = "" }).MarkerLabel);
        Assert.False((data with { Marker = false }).MarkerWanted);
    }

    [Fact(DisplayName = "corner keywords round-trip and the drag position saves as single floats")]
    public void CornerKeywords()
    {
        foreach (var corner in Enum.GetValues<OverlayCorner>())
            Assert.Equal(corner, OverlayPlacement.ParseCorner(corner.KeywordName()));
        Assert.Equal(OverlayCorner.BottomLeft, OverlayPlacement.ParseCorner("BOTTOM-LEFT"));
        Assert.Equal(OverlayCorner.TopRight, OverlayPlacement.ParseCorner(null));
        // SexpWriter directly: the derived records' synthesized ToString
        // recurses (SFloat.ToString -> PrintMembers -> Elements), see report.
        Assert.Equal("(0.25 0.75)", SexpWriter.Write(OverlayPlacement.ToSexp(new OverlayCustomPosition(0.25f, 0.75f))));
    }
}
