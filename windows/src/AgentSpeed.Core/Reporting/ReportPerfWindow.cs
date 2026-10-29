using AgentSpeed.Core.Perf;

namespace AgentSpeed.Core.Reporting;

/// <summary>
/// The watch of METRICS 11.10 as live reporting publishes it to the engine. Every time the watch turns on, a new
/// <see cref="Generation"/> starts and the statistics counted before are never sent, so a break empties the statistics
/// ledger at once whatever the threads see.
/// </summary>
/// <param name="TakenResponses">Responses of this generation that builds took, in engine <paramref name="TakenEpoch"/>.</param>
/// <param name="TakenCalls">Tool calls of this generation that builds took: the engine drops a completion while 100 000 wait.</param>
public sealed record ReportPerfWatch(long Generation, bool On, long TakenResponses = 0, long TakenCalls = 0, long TakenEpoch = 0)
{
    /// <summary>A completion older than this when it is read is history (the 120 000 ms of METRICS 11.4).</summary>
    public const long FirstSeenMs = 120_000;

    /// <summary>Σ <c>n</c> of one report.</summary>
    public const long MaxResponses = 100_000;

    /// <summary>Σ <c>toolN</c> of one report.</summary>
    public const long MaxCalls = 100_000;

    public static ReportPerfWatch Off { get; } = new(0, false);
}

/// <summary>
/// The report side of the statistics ledger (METRICS 11.10, Statistics ledger): what a build takes from the engine's
/// cumulative rows. A build takes the rows since the previous build; discards and new runs leave them alone; a break (the
/// watch turning off) empties them, since the next generation starts over. One per controller, so it outlives runs and
/// transports.
/// </summary>
public sealed class ReportPerfWindow
{
    private static readonly IReadOnlyDictionary<ReportModelKey, PerfSums> None = new Dictionary<ReportModelKey, PerfSums>();

    private long _baselineEpoch = -1;
    private long _baselineGeneration = -1;
    private IReadOnlyDictionary<ReportModelKey, PerfSums> _baseline = None;
    private string? _scope;

    /// <summary>What the engine reads (through the hub) to count completions.</summary>
    public ReportPerfWatch Watch { get; private set; } = ReportPerfWatch.Off;

    /// <summary>
    /// Follows the watch: on for <paramref name="scope"/> (the service) or off. Turning on, or switching services while on,
    /// starts a new generation; turning off is a break. True when <see cref="Watch"/> changed.
    /// </summary>
    public bool Update(bool on, string? scope)
    {
        if (on == Watch.On && (!on || string.Equals(scope, _scope, StringComparison.Ordinal)))
        {
            return false;
        }

        _scope = on ? scope : null;
        Watch = on ? new ReportPerfWatch(Watch.Generation + 1, true) : Watch with { On = false };
        return true;
    }

    /// <summary>The rows a build at <paramref name="sample"/> would take, without taking them (the preview).</summary>
    public IReadOnlyDictionary<ReportModelKey, PerfSums> Content(ReportSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var counters = sample.Facts.Counters;
        if (!Watch.On || counters.PerfGeneration != Watch.Generation || counters.PerfRows.Count == 0)
        {
            return None;
        }

        var since = _baselineEpoch == sample.Facts.Epoch && _baselineGeneration == counters.PerfGeneration ? _baseline : None;
        var rows = new SortedDictionary<ReportModelKey, PerfSums>(KeyOrder.Instance);
        foreach (var (key, row) in counters.PerfRows)
        {
            var change = since.TryGetValue(key, out var before) ? ReportPerfRow.Minus(row, before) : row;
            if (ReportPerfRow.IsListed(change))
            {
                rows[key] = change;
            }
        }

        // The engine keeps at most 100 000 of each waiting; whole rows past it are dropped rather than sent invalid.
        var kept = new Dictionary<ReportModelKey, PerfSums>();
        long responses = 0, calls = 0;
        foreach (var (key, row) in rows)
        {
            if (responses + row.N > ReportPerfWatch.MaxResponses || calls + row.ToolN > ReportPerfWatch.MaxCalls)
            {
                continue;
            }

            responses += row.N;
            calls += row.ToolN;
            kept[key] = row;
        }

        return kept;
    }

    /// <summary>A build: the rows since the previous build, which it empties (also when the size rule then drops <c>models</c>).</summary>
    public IReadOnlyDictionary<ReportModelKey, PerfSums> Take(ReportSample sample)
    {
        var taken = Content(sample);
        var counters = sample.Facts.Counters;
        _baselineEpoch = sample.Facts.Epoch;
        _baselineGeneration = counters.PerfGeneration;
        _baseline = counters.PerfRows;
        if (Watch.On && counters.PerfGeneration == Watch.Generation)
        {
            Watch = Watch with { TakenResponses = counters.PerfResponses, TakenCalls = counters.PerfCalls, TakenEpoch = sample.Facts.Epoch };
        }

        return taken;
    }

    private sealed class KeyOrder : IComparer<ReportModelKey>
    {
        public static KeyOrder Instance { get; } = new();

        public int Compare(ReportModelKey x, ReportModelKey y)
        {
            var id = string.CompareOrdinal(x.ModelId, y.ModelId);
            return id != 0 ? id : x.Source.CompareTo(y.Source);
        }
    }
}
