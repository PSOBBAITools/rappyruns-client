namespace RappyRuns.Core.Game;

/// <summary>
/// Common Lisp integer/float arithmetic the parity paths depend on (spec core
/// §22 #14, risk 7): <c>round</c> is half-to-even on the exact value,
/// <c>floor</c> goes toward negative infinity, and single-float math stays in
/// single precision. Every helper here is written so the C# result is the
/// Lisp result bit for bit.
/// </summary>
public static class LispMath
{
    /// <summary>CL (floor a b) for integers: quotient toward negative infinity.</summary>
    public static long FloorDiv(long a, long b)
    {
        var q = a / b;
        if (a % b != 0 && (a < 0) != (b < 0)) q--;
        return q;
    }

    /// <summary>CL (mod a b): the remainder of <see cref="FloorDiv"/>, sign of b.</summary>
    public static long Mod(long a, long b) => a - FloorDiv(a, b) * b;

    /// <summary>
    /// CL (round a b) for integers with b &gt; 0: the exact rational a/b rounded
    /// to the nearest integer, ties to even (detect.lisp:41 elapsed-ms).
    /// </summary>
    public static long RoundDiv(long a, long b)
    {
        var q = FloorDiv(a, b);
        var r = a - q * b; // 0 <= r < b
        var twice = 2 * r;
        if (twice > b || (twice == b && (q & 1) != 0)) q++;
        return q;
    }

    /// <summary>CL (round x) of a single float: nearest integer, ties to even, exact.</summary>
    public static long Round(float x)
    {
        if (!float.IsFinite(x)) throw new OverflowException("round of a non-finite float");
        return (long)MathF.Round(x, MidpointRounding.ToEven);
    }

    /// <summary>
    /// telemetry.lisp:101 round1: (/ (round (* 10 value)) 10.0) - one decimal,
    /// in single precision throughout. The product overflowing is an error in
    /// Lisp (floating-point-overflow), so it throws here too.
    /// </summary>
    public static float Round1(float value)
    {
        var scaled = value * 10f;
        if (!float.IsFinite(scaled)) throw new OverflowException("round1 overflow");
        var r = Round(scaled);
        return r / 10f;
    }

    /// <summary>
    /// CL elapsed milliseconds: (round (* 1000 (- now start)) units-per-second)
    /// (detect.lisp:41, telemetry.lisp:81).
    /// </summary>
    public static long ElapsedMs(long now, long start, long ticksPerSecond) =>
        RoundDiv(checked(1000 * (now - start)), ticksPerSecond);
}
