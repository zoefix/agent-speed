using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Engine;

internal readonly record struct Interval(long StartMs, long EndMs)
{
    public long DurationMs => EndMs - StartMs;
}

/// <summary>Response timing rules (METRICS 3.3).</summary>
internal static class SpeedMath
{
    public const long MaxIntervalMs = 15 * 60_000;
    public const long MinDurationMs = 500;

    /// <summary>The widened speed interval <c>[start', end]</c>, or false when the response never counts as speed.</summary>
    public static bool TryGetSpeedInterval(ParsedResponse response, out Interval interval)
    {
        if (response.StartMs is not { } start || !TrySpan(start, response.EndMs, out var span) || span > MaxIntervalMs)
        {
            interval = default;
            return false;
        }

        var end = response.EndMs;
        var effectiveStart = span >= MinDurationMs ? start : end < long.MinValue + MinDurationMs ? long.MinValue : end - MinDurationMs;
        interval = new Interval(effectiveStart, end);
        return true;
    }

    /// <summary>
    /// <c>end − start</c> when <c>end ≥ start</c> and the difference fits 64 bits; a saturated start (METRICS 3.6) can lie farther
    /// from the end than that.
    /// </summary>
    public static bool TrySpan(long startMs, long endMs, out long spanMs)
    {
        spanMs = unchecked(endMs - startMs);
        return endMs >= startMs && spanMs >= 0;
    }

    /// <summary>The first-content latency <c>firstMs − startMs</c> (METRICS 6.9 L), or null without both times in order.</summary>
    public static long? LatencyMs(ParsedResponse response) =>
        response.FirstMs is { } first && response.StartMs is { } start && TrySpan(start, first, out var latency) ? latency : null;

    public static long Overlap(long a0, long a1, long b0, long b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    public static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }
}
