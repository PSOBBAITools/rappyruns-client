using System.Diagnostics;

namespace RappyRuns.Core.Game;

/// <summary>
/// The detector's clocks, injectable so tests and the differential golden can
/// drive time exactly (the Lisp tests sleep; the golden script fakes
/// get-internal-real-time). <see cref="Now"/> is a monotonic tick count in
/// <see cref="TicksPerSecond"/> units (Lisp get-internal-real-time /
/// internal-time-units-per-second); <see cref="UniversalTime"/> is CL universal
/// time (seconds since 1900-01-01 UTC), stamped into runs as :finished-at.
/// </summary>
public interface IGameClock
{
    long Now { get; }

    long TicksPerSecond { get; }

    long UniversalTime { get; }

    /// <summary>Local wall-clock time, for trigger-log time stamps (trigger-log.lisp:60 time-of-day).</summary>
    DateTime LocalNow { get; }
}

/// <summary>The real clocks: Stopwatch ticks and the system time.</summary>
public sealed class SystemGameClock : IGameClock
{
    public static readonly SystemGameClock Instance = new();

    /// <summary>Seconds between 1900-01-01 and 1970-01-01 (spec core §3.3).</summary>
    public const long UnixToUniversal = 2208988800;

    public long Now => Stopwatch.GetTimestamp();

    public long TicksPerSecond => Stopwatch.Frequency;

    public long UniversalTime => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + UnixToUniversal;

    public DateTime LocalNow => DateTime.Now;
}

/// <summary>A clock the caller moves by hand (tests, golden replays).</summary>
public sealed class ManualGameClock(long ticksPerSecond = 1_000_000) : IGameClock
{
    public long Now { get; set; }

    public long TicksPerSecond { get; } = ticksPerSecond;

    public long UniversalTime { get; set; } = 3967948800;

    public DateTime LocalNow { get; set; } = new(2025, 9, 24, 12, 0, 0);

    public void AdvanceMs(long ms) => Now += ms * TicksPerSecond / 1000;
}
