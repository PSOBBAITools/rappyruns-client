namespace RappyRuns.Core.Ghost;

/// <summary>
/// Snapshot for the overlay's ghost panel - vs header, room-split rows and the
/// in-world marker (ghost-overlay-data, ghost.lisp:383). All plain immutable
/// data: the overlay thread interpolates the marker itself from
/// <see cref="Track"/> and <see cref="ElapsedAt"/>, so it glides between the
/// poll loop's 4 Hz updates. <see cref="Marker"/> (the :ghost-marker setting)
/// rides along so the overlay thread never reads the config.
/// </summary>
/// <param name="Floor">The live player's floor: the marker shows only for a same-floor ghost.</param>
/// <param name="OwnY">The live player's height, the marker's fallback for heightless tracks.</param>
/// <param name="Splits">Newest first, capped at <see cref="OverlayLayout.SplitRows"/>.</param>
/// <param name="ElapsedMs">The quest telemetry clock when the snapshot was taken.</param>
/// <param name="ElapsedAt">The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> of that instant.</param>
public sealed record GhostOverlayData(
    bool Marker,
    long Floor,
    float? OwnY,
    IReadOnlyList<TrackRow>? Track,
    long GhostTimeMs,
    string? Label,
    GhostPrecision Precision,
    IReadOnlyList<GhostSplit> Splits,
    long ElapsedMs,
    long ElapsedAt)
{
    /// <summary>
    /// The panel snapshot for <paramref name="race"/>, or null until a ghost is
    /// attached and the player's position has been seen - the ghostless overlay
    /// stays the compact timer pill.
    /// </summary>
    public static GhostOverlayData? From(GhostRace race, long elapsedMs, bool marker, long elapsedAt)
    {
        if (race.Ghost is not { } ghost || race.OwnFloor is not { } floor) return null;
        return new GhostOverlayData(
            marker,
            floor,
            race.OwnY,
            ghost.Track,
            ghost.TimeMs,
            ghost.Label,
            ghost.Precision,
            race.Splits.Take(OverlayLayout.SplitRows).ToArray(),
            elapsedMs,
            elapsedAt);
    }

    /// <summary>
    /// Should the in-world marker be live (overlay-marker-wanted-p,
    /// overlay-win32.lisp:547)? The ghost has a track to interpolate AND the
    /// setting is on. This - not the mere ghost panel - makes the overlay span
    /// the client area and repaint at 30 Hz.
    /// </summary>
    public bool MarkerWanted => Marker && Track is { Count: > 0 };

    /// <summary>
    /// The live elapsed-ms at <paramref name="nowTimestamp"/>, extrapolated from
    /// the snapshot so the dot glides at the repaint rate
    /// (overlay-effective-elapsed, overlay-win32.lisp:628).
    /// </summary>
    public long EffectiveElapsed(long nowTimestamp) =>
        ElapsedMs + (long)Math.Round(1000.0 * (nowTimestamp - ElapsedAt) / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>
    /// The ghost's position at <paramref name="elapsedMs"/>, or null before its
    /// first sample or once the ghost has finished (overlay-ghost-position,
    /// overlay-win32.lisp:635): a ghost past its own final time has left.
    /// </summary>
    public GhostPosition? GhostPositionAt(long elapsedMs) =>
        Track is { Count: > 0 } && elapsedMs <= GhostTimeMs ? GhostTrack.Position(Track, elapsedMs) : null;

    /// <summary>
    /// Where to draw the marker on a <paramref name="width"/> x
    /// <paramref name="height"/> client area, or null when it should not be
    /// drawn (overlay-draw-ghost-marker, overlay-win32.lisp:670): no fresh
    /// camera, the ghost on another floor, or off screen by more than 50 px. A
    /// heightless track borrows the live player's height (same floor, so
    /// usually the same ground), else 0.
    /// </summary>
    public ScreenPoint? MarkerPoint(CameraState? camera, int width, int height, GhostPosition? ghostPos)
    {
        if (camera is null || ghostPos is not { } pos || pos.Floor != Floor) return null;
        var y = pos.Y ?? (OwnY is { } own ? own : 0.0);
        var p = GhostProjection.ScreenPosition(camera, width, height, pos.X, y, pos.Z);
        if (p is not { } s) return null;
        return s.X > -50 && s.X < width + 50 && s.Y > -50 && s.Y < height + 50 ? s : null;
    }

    /// <summary>The marker's name pill: the submitter, or "ghost" when unnamed.</summary>
    public string MarkerLabel => string.IsNullOrEmpty(Label) ? "ghost" : Label;
}
