using System.Text.Json;
using RappyRuns.Core.Ghost;

namespace RappyRuns.Tests.Ghost;

/// <summary>
/// Parity with the Lisp client over realistic inputs: every expected value
/// below was computed by client/src/ghost.lisp under SBCL
/// (desktop/tools/export-ghost-golden.lisp). Single floats are compared by
/// their bits, so an off-by-one-ulp contagion mistake shows as a pixel.
/// </summary>
public class GhostGoldenTests
{
    private static float Single(JsonElement e, string name) => BitConverter.UInt32BitsToSingle(e.GetProperty(name).GetUInt32());

    private static int? NullableInt(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();

    [Fact(DisplayName = "golden: camera projection matches the Lisp pixels")]
    public void Projection()
    {
        var cases = Golden.Load("ghost/projection.json");
        var checkedCount = 0;
        foreach (var c in cases.EnumerateArray())
        {
            var cam = c.GetProperty("camera");
            CameraState? camera = cam.ValueKind == JsonValueKind.Null
                ? null
                : new CameraState(Single(cam, "xBits"), Single(cam, "yBits"), Single(cam, "zBits"),
                    Single(cam, "dirXBits"), Single(cam, "dirYBits"), Single(cam, "dirZBits"),
                    NullableInt(cam.GetProperty("zoom")));
            var actual = GhostProjection.ScreenPosition(camera, c.GetProperty("width").GetInt32(), c.GetProperty("height").GetInt32(),
                c.GetProperty("wx").GetDouble(), c.GetProperty("wy").GetDouble(), c.GetProperty("wz").GetDouble());
            var expected = c.GetProperty("result");
            if (expected.ValueKind == JsonValueKind.Null)
                Assert.Null(actual);
            else
                Assert.Equal(new ScreenPoint(expected[0].GetInt32(), expected[1].GetInt32()), actual);
            checkedCount++;
        }
        Assert.True(checkedCount > 400);
    }

    [Fact(DisplayName = "golden: FOV heuristic matches Lisp")]
    public void Fov()
    {
        foreach (var c in Golden.Load("ghost/fov.json").EnumerateArray())
        {
            var fov = GhostProjection.CameraFov(NullableInt(c.GetProperty("zoom")), Single(c, "aspectBits"));
            Assert.Equal(c.GetProperty("fov").GetDouble(), fov);
        }
    }

    [Fact(DisplayName = "golden: streaming room alignment matches Lisp step by step")]
    public void Alignment()
    {
        foreach (var c in Golden.Load("ghost/alignment.json").EnumerateArray())
        {
            var ghost = GhostReference.Parse(c.GetProperty("payload").GetString());
            Assert.NotNull(ghost);
            var attachAt = c.GetProperty("attachAt").GetInt32();
            var race = new GhostRace();
            var i = 0;
            foreach (var step in c.GetProperty("steps").EnumerateArray())
            {
                if (i++ == attachAt) race.Ghost = ghost;
                var delta = race.NoteRoom(step.GetProperty("floor").GetInt64(), step.GetProperty("room").GetInt64(), step.GetProperty("ms").GetInt64());
                var expected = step.GetProperty("delta");
                Assert.Equal(expected.ValueKind == JsonValueKind.Null ? null : expected.GetInt64(), delta);
            }
            Assert.Equal(c.GetProperty("cursor").GetInt32(), race.Cursor);
            Assert.Equal(c.GetProperty("matched").GetInt32(), race.MatchedRooms);
            var splits = c.GetProperty("splits").EnumerateArray()
                .Select(s => new GhostSplit(s.GetProperty("room").GetInt64(), s.GetProperty("floor").GetInt64(),
                    s.GetProperty("ms").GetInt64(), s.GetProperty("delta").GetInt64()))
                .ToList();
            Assert.Equal(splits, race.Splits);
        }
    }

    [Fact(DisplayName = "golden: track interpolation matches Lisp")]
    public void Track()
    {
        foreach (var c in Golden.Load("ghost/track.json").EnumerateArray())
        {
            var track = GhostReference.Parse($$"""{"time_ms":1,"rooms":[],"track":{{c.GetProperty("track").GetRawText()}}}""")!.Track;
            foreach (var q in c.GetProperty("queries").EnumerateArray())
            {
                var actual = GhostTrack.Position(track, q.GetProperty("ms").GetInt64());
                var expected = q.GetProperty("result");
                if (expected.ValueKind == JsonValueKind.Null)
                {
                    Assert.Null(actual);
                    continue;
                }
                var p = actual!.Value;
                Assert.Equal(expected[0].GetInt64(), p.Floor);
                Assert.Equal(expected[1].GetInt64(), p.Map);
                Assert.Equal(expected[2].GetDouble(), p.X);
                Assert.Equal(expected[3].GetDouble(), p.Z);
                Assert.Equal(expected[4].ValueKind == JsonValueKind.Null ? null : expected[4].GetDouble(), p.Y);
            }
        }
    }

    [Fact(DisplayName = "golden: panel placement matches Lisp")]
    public void Placement()
    {
        foreach (var c in Golden.Load("ghost/placement.json").EnumerateArray())
        {
            var custom = c.GetProperty("custom");
            OverlayCustomPosition? pos = custom.ValueKind == JsonValueKind.Null
                ? null
                : new OverlayCustomPosition(BitConverter.UInt32BitsToSingle(custom[0].GetUInt32()), BitConverter.UInt32BitsToSingle(custom[1].GetUInt32()));
            var (x, y) = OverlayPlacement.PanelOrigin(OverlayPlacement.ParseCorner(c.GetProperty("corner").GetString()), pos,
                c.GetProperty("areaW").GetInt32(), c.GetProperty("areaH").GetInt32(), c.GetProperty("w").GetInt32(), c.GetProperty("h").GetInt32(),
                OverlayLayout.MarginX, OverlayLayout.MarginY);
            var r = c.GetProperty("result");
            Assert.Equal((r[0].GetInt32(), r[1].GetInt32()), (x, y));
        }
    }

    [Fact(DisplayName = "golden: gap and clock formatting match Lisp FORMAT")]
    public void Format()
    {
        var golden = Golden.Load("ghost/format.json");
        foreach (var d in golden.GetProperty("delta").EnumerateArray())
        {
            var ms = d.GetProperty("ms").GetInt64();
            Assert.Equal(d.GetProperty("ms1").GetString(), GhostFormat.Delta(ms, GhostPrecision.Ms));
            Assert.Equal(d.GetProperty("sec").GetString(), GhostFormat.Delta(ms, GhostPrecision.Sec));
        }
        foreach (var c in golden.GetProperty("clock").EnumerateArray())
        {
            var ms = c.GetProperty("ms").GetInt64();
            Assert.Equal(c.GetProperty("runTime").GetString(), GhostFormat.RunTime(ms));
            Assert.Equal(c.GetProperty("splitClock").GetString(), GhostFormat.SplitClock(ms));
        }
    }
}
