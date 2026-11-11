namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>The percent shown for the current instance (QUOTAS 10.7), and the reset it counts to.</summary>
public readonly record struct LiveValue(double Percent, bool Estimated, long? ResetsAtMs);

public static class LivePercent
{
    /// <param name="firstAfterReset">The start of the first local response that started at or after R and has ended (5-hour windows).</param>
    public static LiveValue Of(
        double p, long t, long r, long s, int minutes, QuotaWindowKind kind, double? cap, SpendLedger ledger, string account,
        string? name, long nowMs, long? firstAfterReset)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (nowMs < r)
        {
            if (cap is not { } k)
            {
                return new LiveValue(p, false, r);
            }

            var live = p + 100.0 * (ledger.Outer(account, name, s, nowMs).Cost - ledger.Outer(account, name, s, t).Cost) / k;
            live = Math.Min(Math.Max(live, p), Math.Max(100.0, p));
            return new LiveValue(live, live - p >= 0.5, r);
        }

        var length = minutes * EstimateLimits.Minute;
        long start;
        if (kind == QuotaWindowKind.FiveHour)
        {
            if (firstAfterReset is not { } first)
            {
                return new LiveValue(0.0, false, null);
            }

            start = SpendLedger.MinuteOf(first);
        }
        else
        {
            start = r + (nowMs - r) / length * length;
        }

        var next = start + length;
        if (cap is not { } c)
        {
            return new LiveValue(0.0, false, next);
        }

        return new LiveValue(Math.Min(100.0, 100.0 * ledger.Outer(account, name, start, nowMs).Cost / c), true, next);
    }
}
