namespace RappyRuns.Core.Ghost;

/// <summary>
/// The game camera as READ-CAMERA reads it (psobb.lisp:386; media spec §3.7):
/// the eye position and unit view direction as the f32s at 0x00A48780 and the
/// zoom step (u32 at 0x009ACEDC, 0-4). Kept as single floats because the Lisp
/// math mixes these singles with double world coordinates, and the contagion
/// has to happen at exactly the same places for pixel parity.
/// </summary>
/// <param name="Zoom">Null when unreadable: the FOV heuristic then assumes step 1.</param>
public sealed record CameraState(float X, float Y, float Z, float DirX, float DirY, float DirZ, int? Zoom);

/// <summary>An on-screen pixel, relative to the game's client area.</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// In-world marker projection: the ghost's world position through the game
/// camera onto the client area (ghost.lisp:315-376). Transcribed from the
/// DropBox Tracker / PartyMemberTracker addons' field-verified math: a linear
/// projection onto the view plane, with an FOV heuristic keyed off the zoom step.
/// </summary>
/// <remarks>
/// Every float/double choice below mirrors the Lisp numeric contagion: literals
/// such as 0.56470588, 1e-3 and 0.600 are single floats in Lisp (the default
/// read format), <c>pi</c> is a double, world coordinates are the doubles jzon
/// parsed, camera values are f32s from process memory. Mixing single and double
/// promotes the single to double for that one operation - so a single-only
/// subexpression (the aspect ratio, the atan argument, the unnormalized basis
/// vectors) stays single. The golden test (golden/ghost/projection.json)
/// pins this against SBCL over 400 realistic cameras.
/// </remarks>
public static class GhostProjection
{
    /// <summary>768/1360: the addons' empirical constant relating the aspect ratio to the base FOV (+camera-fov-aspect-factor+).</summary>
    public const float FovAspectFactor = 0.56470588f;

    /// <summary>
    /// The screen FOV in radians for camera <paramref name="zoom"/> step (0-4,
    /// clamped; null = 1) at <paramref name="aspect"/> ratio (camera-fov,
    /// ghost.lisp:326) - the addons' heuristic, valid for aspect ratios around
    /// 1.25-1.78.
    /// </summary>
    public static double CameraFov(int? zoom, float aspect)
    {
        var z = Math.Min(4, Math.Max(0, zoom ?? 1));
        // (atan single) -> single: SBCL/LispWorks evaluate it in double and
        // round the result to single, which (float)Math.Atan reproduces.
        var atan = (float)Math.Atan(FovAspectFactor * aspect);
        var degrees = (double)(2f * atan) * (180.0 / Math.PI)
                      - (double)((z - 1) * 0.600f)
                      - (double)(Math.Min(z, 1) * 0.300f);
        return degrees * (Math.PI / 180.0);
    }

    /// <summary>
    /// Pixel of world point (<paramref name="wx"/>, <paramref name="wy"/>,
    /// <paramref name="wz"/>) on the game's <paramref name="width"/> x
    /// <paramref name="height"/> client area, projected through
    /// <paramref name="camera"/> (ghost-screen-position, ghost.lisp:337). Null
    /// when the point is behind the camera, degenerate, or the camera is missing.
    /// The eye direction is a unit vector; a zeroed one (loading screens) fails
    /// the front-facing test and returns null.
    /// </summary>
    public static ScreenPoint? ScreenPosition(CameraState? camera, int width, int height, double wx, double wy, double wz)
    {
        if (camera is null || width <= 0 || height <= 0) return null;
        double vx = wx - camera.X, vy = wy - camera.Y, vz = wz - camera.Z;
        var len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        if (!(len > (double)1e-3f)) return null;
        vx /= len;
        vy /= len;
        vz /= len;
        var fdp = camera.DirX * vx + camera.DirY * vy + camera.DirZ * vz;
        if (!(fdp > (double)1e-7f)) return null;

        var aspect = (float)width / (float)height;
        var fov = CameraFov(camera.Zoom, aspect);
        var determinant = (double)(aspect * (float)height) / (2 * Math.Tan(0.5 * fov));
        var s = determinant / fdp;
        double px = s * vx, py = s * vy, pz = s * vz;
        // right = dir x up(0,1,0); up' = right x dir. Deliberately NOT
        // normalized (their magnitude is dir's horizontal component), matching
        // the addons verbatim: the FOV heuristic was tuned against exactly
        // this scaling, so "fixing" it would move every marker (ghost.lisp:361).
        float dx = camera.DirX, dy = camera.DirY, dz = camera.DirZ;
        var rx = -dz;
        var rz = dx;
        var ux = -(dx * dy);
        var uy = dx * dx + dz * dz;
        var uz = -(dy * dz);
        var sx = Math.Round(width / 2.0 + (rx * px + rz * pz));
        var sy = Math.Round(height / 2.0 - (ux * px + uy * py + uz * pz));
        return new ScreenPoint(ToPixel(sx), ToPixel(sy));
    }

    /// <summary>
    /// A near-grazing point (fdp just above 1e-7) lands billions of pixels off
    /// screen, where Lisp returns a bignum. Clamping keeps it far off screen, so
    /// the marker's on-screen test rejects it exactly as it rejects the bignum.
    /// </summary>
    private static int ToPixel(double v) => v >= int.MaxValue ? int.MaxValue : v <= int.MinValue ? int.MinValue : (int)v;
}
