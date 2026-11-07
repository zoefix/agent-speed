namespace AgentSpeed.Core.Lan;

/// <summary>The push receivers of LAN 4.7, with their minimum interval and heartbeat.</summary>
public enum LanReceiverKind
{
    Stream,
    Udp,
    Http,
}

/// <summary>
/// When one push receiver sends (LAN 4.7) on a monotonic millisecond clock: at once when it starts, every changed frame but
/// at most one per minimum interval (a held change goes out at the first tick once the interval has passed), and the latest
/// frame after a heartbeat interval without a send. Not thread-safe.
/// </summary>
public sealed class LanPushSchedule(LanReceiverKind kind)
{
    /// <summary>LAN 4.7: engine ticks are not exactly 500 ms apart, so an interval counts as passed 100 ms early.</summary>
    public const long SlackMs = 100;

    private long? _last;
    private bool _held;

    public long MinimumMs { get; } = kind switch
    {
        LanReceiverKind.Http => 1000,
        _ => 500,
    };

    public long HeartbeatMs { get; } = kind switch
    {
        LanReceiverKind.Stream => 5000,
        LanReceiverKind.Udp => 1000,
        _ => 10_000,
    };

    public bool Started => _last is not null;

    /// <summary>Takes the tick at <paramref name="nowMs"/>; true when the receiver should send its latest frame now.</summary>
    public bool Due(long nowMs, bool changed)
    {
        if (_last is not { } last)
        {
            return true;
        }

        _held |= changed;
        return (_held && nowMs - last >= MinimumMs - SlackMs) || nowMs - last >= HeartbeatMs - SlackMs;
    }

    /// <summary>A frame went out at <paramref name="nowMs"/>.</summary>
    public void Sent(long nowMs)
    {
        _last = nowMs;
        _held = false;
    }

    /// <summary>The send times of a receiver that is never busy, for <c>(time, changed)</c> ticks (the vectors).</summary>
    public static IReadOnlyList<long> Simulate(LanReceiverKind kind, IEnumerable<(long At, bool Changed)> ticks)
    {
        var schedule = new LanPushSchedule(kind);
        var sent = new List<long>();
        foreach (var (at, changed) in ticks)
        {
            if (schedule.Due(at, changed))
            {
                schedule.Sent(at);
                sent.Add(at);
            }
        }

        return sent;
    }
}

/// <summary>An HTTP target's wait after its <c>n</c>-th failure in a row (LAN 5.3): 2, 4, 8, 16, then 30 s.</summary>
public static class LanBackoff
{
    private static readonly long[] Steps = [2000, 4000, 8000, 16_000, 30_000];

    public static long Ms(int failures) => failures <= 0 ? 0 : Steps[Math.Min(failures, Steps.Length) - 1];
}
