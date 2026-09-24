using System.Globalization;

namespace RappyRuns.Core.Ghost;

/// <summary>
/// Display helpers for the ghost race (ghost.lisp:477-521) and the two run
/// clocks they build on (store.lisp:89-105).
/// </summary>
public static class GhostFormat
{
    /// <summary>
    /// The live gap: "-3.2s" / "+4s" (format-ghost-delta, ghost.lisp:481).
    /// Whole seconds when the ghost is only second-accurate (frame-derived
    /// splits). Zero counts as "+".
    /// </summary>
    /// <remarks>
    /// Parity: Lisp prints <c>(/ magnitude 1000.0)</c> - a single float - with
    /// <c>~,1F</c>, which rounds the float's exact binary value half away from
    /// zero (1250 -> "1.3" but 1050 -> "1.0", since 1.05f is 1.04999995).
    /// Whole seconds use CL ROUND: half to even (2500 -> "2", 3500 -> "4").
    /// Pinned by golden/ghost/format.json.
    /// </remarks>
    public static string Delta(long deltaMs, GhostPrecision precision)
    {
        var sign = deltaMs < 0 ? "-" : "+";
        var magnitude = Math.Abs(deltaMs);
        if (precision == GhostPrecision.Ms)
        {
            var seconds = (float)magnitude / 1000f;
            var exact = new decimal((double)seconds);
            var rounded = Math.Round(exact, 1, MidpointRounding.AwayFromZero);
            return sign + rounded.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }
        return sign + RoundHalfEven(magnitude, 1000).ToString(CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>
    /// The clear time "m:ss.mmm" (format-run-time, store.lisp:89). Minutes are
    /// not zero-padded and keep counting past an hour ("65:00.000").
    /// </summary>
    public static string RunTime(long ms)
    {
        var (totalSeconds, msec) = FloorDivRem(ms, 1000);
        var (minutes, seconds) = FloorDivRem(totalSeconds, 60);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}.{msec:000}");
    }

    /// <summary>"m:ss", the seconds-precision clock of the overlay's split rows (format-split-clock, store.lisp:98).</summary>
    public static string SplitClock(long ms)
    {
        var (minutes, seconds) = FloorDivRem(FloorDivRem(ms, 1000).Quotient, 60);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    /// <summary>
    /// "vs 12:34.567 -3.2s" for <paramref name="race"/>, or just the target
    /// time before the first matched room (ghost-vs-text, ghost.lisp:490) -
    /// shared by the status line and the overlay. Null when no ghost is attached.
    /// </summary>
    public static string? VsText(GhostRace? race)
    {
        var ghost = race?.Ghost;
        if (race is null || ghost is null) return null;
        var text = "vs " + RunTime(ghost.TimeMs);
        return race.DeltaMs is { } delta ? text + " " + Delta(delta, ghost.Precision) : text;
    }

    /// <summary>" | vs 12:34.567 -3.2s" for the quest-status line while a ghost is attached (ghost-status-suffix, ghost.lisp:503).</summary>
    public static string? StatusSuffix(GhostRace? race) =>
        VsText(race) is { } text ? " | " + text : null;

    /// <summary>" -3.2s" for the window title: the gap alone, null before the first matched room (ghost-title-suffix, ghost.lisp:513).</summary>
    public static string? TitleSuffix(GhostRace? race) =>
        race?.Ghost is { } ghost && race.DeltaMs is { } delta ? " " + Delta(delta, ghost.Precision) : null;

    /// <summary>CL (floor n d) for positive d: quotient toward negative infinity and a non-negative remainder.</summary>
    private static (long Quotient, long Remainder) FloorDivRem(long n, long d)
    {
        var q = Math.DivRem(n, d, out var r);
        if (r < 0)
        {
            q--;
            r += d;
        }
        return (q, r);
    }

    /// <summary>CL (round n d) for non-negative n: half to even.</summary>
    private static long RoundHalfEven(long n, long d)
    {
        var q = Math.DivRem(n, d, out var r);
        if (r * 2 > d || (r * 2 == d && q % 2 != 0)) q++;
        return q;
    }
}
