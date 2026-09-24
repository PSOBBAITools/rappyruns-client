namespace RappyRuns.Core.Ghost;

/// <summary>The ghost's interpolated position on its track at one instant.</summary>
/// <param name="Y">Null on pre-height rows: the marker then borrows the live player's own height.</param>
public readonly record struct GhostPosition(long Floor, long Map, double X, double Z, double? Y);

/// <summary>
/// In-world marker support: where the ghost dot is at any elapsed time
/// (ghost.lisp:233-272). Pure, so the tests pin the interpolation.
/// </summary>
public static class GhostTrack
{
    /// <summary>
    /// Interpolate between two track samples only when they are this close
    /// (+track-lerp-max-gap-ms+); across a bigger gap (a warp, missing data)
    /// the dot snaps instead of gliding through walls.
    /// </summary>
    public const long LerpMaxGapMs = 3000;

    /// <summary>
    /// The ghost at <paramref name="elapsedMs"/> on <paramref name="track"/>
    /// (ghost-track-position, ghost.lisp:239), linearly interpolated between
    /// neighbouring samples on the same floor. Null before the first sample or
    /// on an empty track; past the last sample the dot rests there.
    /// </summary>
    /// <remarks>
    /// Parity: the Lisp fraction is a single float - <c>(/ (- elapsed ms)
    /// (float (- next-ms ms)))</c> - while the coordinates are the doubles jzon
    /// parsed, so the product promotes the single fraction to double.
    /// </remarks>
    public static GhostPosition? Position(IReadOnlyList<TrackRow>? track, long elapsedMs)
    {
        if (track is null || track.Count == 0) return null;
        var n = track.Count;
        if (elapsedMs < track[0].Ms) return null;
        // Binary search: the last row with ms <= elapsed.
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2; // (ceiling (+ lo hi) 2), both non-negative
            if (track[mid].Ms <= elapsedMs) lo = mid;
            else hi = mid - 1;
        }
        var row = track[lo];
        if (lo + 1 >= n) return new GhostPosition(row.Floor, row.Map, row.X, row.Z, row.Y);
        var next = track[lo + 1];
        if (next.Floor == row.Floor && next.Ms - row.Ms < LerpMaxGapMs && next.Ms > row.Ms)
        {
            var f = (double)((float)(elapsedMs - row.Ms) / (float)(next.Ms - row.Ms));
            double? y = row.Y is { } y0 && next.Y is { } y1 ? y0 + f * (y1 - y0) : null;
            return new GhostPosition(row.Floor, row.Map, row.X + f * (next.X - row.X), row.Z + f * (next.Z - row.Z), y);
        }
        return new GhostPosition(row.Floor, row.Map, row.X, row.Z, row.Y);
    }
}
