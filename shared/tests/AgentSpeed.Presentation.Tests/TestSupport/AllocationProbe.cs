namespace AgentSpeed.Presentation.Tests.TestSupport;

/// <summary>
/// Measures allocations of a steady-state loop on the current thread. A per-frame allocation shows up in every round;
/// one-time runtime work (tiered JIT, static initializers) shows up in some rounds only, so the minimum must be zero.
/// </summary>
internal static class AllocationProbe
{
    public static (long Minimum, long Maximum) Measure(Action round, int rounds = 5)
    {
        var minimum = long.MaxValue;
        var maximum = 0L;
        for (var i = 0; i < rounds; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            round();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            minimum = Math.Min(minimum, bytes);
            maximum = Math.Max(maximum, bytes);
        }

        return (minimum, maximum);
    }

    public static void AssertNone(Action round)
    {
        var (minimum, maximum) = Measure(round);
        Assert.True(minimum == 0, $"{minimum} bytes allocated in every round (max {maximum})");
        Assert.True(maximum < 16 * 1024, $"{maximum} bytes allocated in one round");
    }
}
