namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// When a report carries <c>quotas</c> (REPORTING-EXTENSIONS 11.3): the run's first, a changed set of entries, a changed
/// vendor percent, confidence or reset state, 60 s since the last one carried, or leaving. A section dropped for size
/// counts as unsent; switching it off sends <c>[]</c> once; a new run starts empty. Not thread-safe.
/// </summary>
public sealed class QuotaReportCadence
{
    private IReadOnlyList<QuotaReportEntry>? _last;
    private long? _lastMs;
    private bool _cleared = true;

    public void NewRun()
    {
        _last = null;
        _lastMs = null;
        _cleared = true;
    }

    /// <summary>The <c>quotas</c> this report would carry: null (absent), <c>[]</c> or the entries. Nothing is recorded until <see cref="Commit"/>.</summary>
    public IReadOnlyList<QuotaReportEntry>? Pending(long nowMs, IReadOnlyList<QuotaReportEntry> entries, bool switchOn, bool leaving)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!switchOn)
        {
            return _cleared ? null : [];
        }

        return ShouldSend(entries, _last, _lastMs ?? 0, nowMs, leaving) ? entries : null;
    }

    /// <summary>Records what a sent report carried: nothing for null, the switched-off <c>[]</c> clears the run's state.</summary>
    public void Commit(long nowMs, IReadOnlyList<QuotaReportEntry>? carried, bool switchOn)
    {
        if (carried is null)
        {
            return;
        }

        if (!switchOn)
        {
            NewRun();
            return;
        }

        (_last, _lastMs, _cleared) = (carried, nowMs, false);
    }

    /// <summary>One report: <see cref="Pending"/>, then <see cref="Commit"/> unless the section was dropped for size.</summary>
    public IReadOnlyList<QuotaReportEntry>? Step(long nowMs, IReadOnlyList<QuotaReportEntry> entries, bool switchOn = true, bool leaving = false, bool fits = true)
    {
        var pending = Pending(nowMs, entries, switchOn, leaving);
        if (pending is null || (switchOn && !fits))
        {
            return null;
        }

        Commit(nowMs, pending, switchOn);
        return pending;
    }

    public static bool ShouldSend(IReadOnlyList<QuotaReportEntry> entries, IReadOnlyList<QuotaReportEntry>? lastSent, long lastSentMs, long nowMs, bool leaving)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (lastSent is null || leaving || nowMs - lastSentMs >= EstimateLimits.SendEveryMs)
        {
            return true;
        }

        var before = new Dictionary<string, QuotaReportEntry>(StringComparer.Ordinal);
        foreach (var e in lastSent)
        {
            before[Identity(e)] = e;
        }

        var now = new Dictionary<string, QuotaReportEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            now[Identity(e)] = e;
        }

        if (before.Count != now.Count || now.Keys.Any(k => !before.ContainsKey(k)))
        {
            return true;
        }

        foreach (var (key, e) in now)
        {
            var b = before[key];
            if (e.UsedPercent != b.UsedPercent || e.Estimate.Confidence != b.Estimate.Confidence || (e.ResetsInMs is null) != (b.ResetsInMs is null))
            {
                return true;
            }
        }

        return false;
    }

    private static string Identity(QuotaReportEntry e) =>
        string.Join(",", e.Sources.Select(EstimateNames.Source)) + "|" + e.WindowId;
}
