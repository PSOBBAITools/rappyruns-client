using RappyRuns.Core.Sexp;

namespace RappyRuns.Core.Ghost;

/// <summary>
/// Where the overlay panel sits in the game's client area: the
/// <c>:overlay-corner</c> setting (config.lisp:39). <see cref="Custom"/> is a
/// Ctrl+dragged spot stored in <c>:overlay-position</c>.
/// </summary>
public enum OverlayCorner
{
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft,
    MiddleRight,
    MiddleLeft,
    TopCenter,
    BottomCenter,
    Custom,
}

/// <summary>
/// A Ctrl+dragged panel spot: each axis the 0..1 fraction of the client
/// area's slack (area minus panel), so a window resize keeps the panel
/// proportionally placed and always inside. Single floats, as the Lisp drag
/// writes them (overlay-win32.lisp:933) and the config reads them back.
/// </summary>
public readonly record struct OverlayCustomPosition(float X, float Y);

/// <summary>Panel geometry shared by the overlay window and its tests (overlay-win32.lisp:397-420; media spec §2.3).</summary>
public static class OverlayLayout
{
    public const int Width = 260;

    /// <summary>The v0.51.0 timer pill: two text lines, no room rows. Used whenever no ghost is attached.</summary>
    public const int CompactHeight = 62;

    /// <summary>Where the room-split rows start, under the two header lines.</summary>
    public const int SplitTop = 58;

    public const int SplitRowHeight = 20;

    /// <summary>How many of the newest matched-room splits ride to the overlay (+overlay-split-rows+, ghost.lisp:379).</summary>
    public const int SplitRows = 8;

    /// <summary>Header + the reserved room-split rows: 58 + 8*20 + 6 = 224.</summary>
    public const int GhostHeight = SplitTop + SplitRows * SplitRowHeight + 6;

    /// <summary>The panel's gap from the client area's nearest vertical edge.</summary>
    public const int MarginX = 24;

    /// <summary>The panel's gap from the client area's nearest horizontal edge.</summary>
    public const int MarginY = 16;

    public const byte Alpha = 215;
}

/// <summary>
/// Pure panel placement (ghost.lisp:274-313), plus the config-value parsing
/// with the Lisp guards for hand-edited configs.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>
    /// The <c>:overlay-corner</c> keyword name ("top-right", any case, with or
    /// without the leading colon) as a corner. Anything unknown places like
    /// top-right, as the Lisp CASE default does; null only for a missing value.
    /// </summary>
    public static OverlayCorner ParseCorner(string? keyword) =>
        keyword?.TrimStart(':').ToLowerInvariant() switch
        {
            "top-left" => OverlayCorner.TopLeft,
            "bottom-right" => OverlayCorner.BottomRight,
            "bottom-left" => OverlayCorner.BottomLeft,
            "middle-right" => OverlayCorner.MiddleRight,
            "middle-left" => OverlayCorner.MiddleLeft,
            "top-center" => OverlayCorner.TopCenter,
            "bottom-center" => OverlayCorner.BottomCenter,
            "custom" => OverlayCorner.Custom,
            _ => OverlayCorner.TopRight,
        };

    /// <summary>The config keyword name for <paramref name="corner"/> (without colon).</summary>
    public static string KeywordName(this OverlayCorner corner) => corner switch
    {
        OverlayCorner.TopLeft => "top-left",
        OverlayCorner.BottomRight => "bottom-right",
        OverlayCorner.BottomLeft => "bottom-left",
        OverlayCorner.MiddleRight => "middle-right",
        OverlayCorner.MiddleLeft => "middle-left",
        OverlayCorner.TopCenter => "top-center",
        OverlayCorner.BottomCenter => "bottom-center",
        OverlayCorner.Custom => "custom",
        _ => "top-right",
    };

    /// <summary>
    /// The <c>:overlay-position</c> config value as a usable custom spot, or
    /// null - the guard in overlay-panel-origin (ghost.lisp:305-309): a list
    /// whose first two elements are numbers. A dotted pair <c>(0.5 . 0.7)</c>,
    /// a one-element list or a string is unusable (and must not throw).
    /// </summary>
    public static OverlayCustomPosition? ParseCustom(SexpNode? value)
    {
        if (value is not SList list || list.Items.Count < 2) return null;
        var x = list.Items[0].AsNumber;
        var y = list.Items[1].AsNumber;
        return x is { } fx && y is { } fy ? new OverlayCustomPosition((float)fx, (float)fy) : null;
    }

    /// <summary>The drag's position as the config value to save: <c>(x-frac y-frac)</c> single floats.</summary>
    public static SexpNode ToSexp(OverlayCustomPosition position) =>
        new SList([new SFloat(position.X), new SFloat(position.Y)]);

    /// <summary>
    /// Top-left of a <paramref name="w"/> x <paramref name="h"/> panel at
    /// <paramref name="corner"/> of an area, the margins in from the nearest
    /// edges (centered axes take no margin) - overlay-corner-origin,
    /// ghost.lisp:274. Origins clamp at 0 so an area smaller than the panel
    /// degrades to flush edges. <see cref="OverlayCorner.Custom"/> (without a
    /// usable position) lands in the default branch: top-right.
    /// </summary>
    public static (int X, int Y) CornerOrigin(OverlayCorner corner, int areaW, int areaH, int w, int h, int marginX, int marginY)
    {
        var x = corner switch
        {
            OverlayCorner.TopLeft or OverlayCorner.MiddleLeft or OverlayCorner.BottomLeft => marginX,
            OverlayCorner.TopCenter or OverlayCorner.BottomCenter => Math.Max(0, FloorHalf(areaW - w)),
            _ => Math.Max(0, areaW - w - marginX),
        };
        var y = corner switch
        {
            OverlayCorner.BottomLeft or OverlayCorner.BottomCenter or OverlayCorner.BottomRight => Math.Max(0, areaH - h - marginY),
            OverlayCorner.MiddleLeft or OverlayCorner.MiddleRight => Math.Max(0, FloorHalf(areaH - h)),
            _ => marginY,
        };
        return (x, y);
    }

    /// <summary>
    /// Top-left of the panel (overlay-panel-origin, ghost.lisp:295): the
    /// dragged <paramref name="custom"/> spot when <paramref name="corner"/> is
    /// Custom - each fraction clamped to 0..1 and scaled by the slack - else the
    /// corner anchor. Custom without a usable position falls through to the
    /// corner origin, i.e. top-right.
    /// </summary>
    /// <remarks>
    /// Parity: <c>(round (* frac slack))</c> multiplies a single float by an
    /// integer, so the product is single and rounds half-to-even.
    /// </remarks>
    public static (int X, int Y) PanelOrigin(OverlayCorner corner, OverlayCustomPosition? custom, int areaW, int areaH, int w, int h, int marginX, int marginY)
    {
        if (corner == OverlayCorner.Custom && custom is { } c)
            return (Scale(c.X, Math.Max(0, areaW - w)), Scale(c.Y, Math.Max(0, areaH - h)));
        return CornerOrigin(corner, areaW, areaH, w, h, marginX, marginY);
    }

    private static int Scale(float fraction, int slack)
    {
        // (min 1 (max 0 f)) returns the integer bound when f is outside, so
        // the out-of-range products are exact.
        if (!(fraction > 0)) return 0;
        if (fraction >= 1) return slack;
        return (int)Math.Round(fraction * (float)slack);
    }

    /// <summary>CL <c>(floor n 2)</c>: toward negative infinity, unlike C# integer division.</summary>
    private static int FloorHalf(int n) => (int)Math.Floor(n / 2.0);
}
