namespace AgentSpeed.Core.Perf;

/// <summary>
/// The pooled evaluation of a set of records of one source (METRICS 6.11 <i>Pooled score</i>): the 6.9 formula over the
/// summed measures; a score only from <c>Σ n ≥ 20</c>, the components shown either way.
/// </summary>
public sealed record PerfPooled(SourceKind Source, long N, long Tools, long ActiveMs, double? Score, IReadOnlyList<PerfComponent> Components)
{
    public int? Shown => Score is { } score ? PerfFormula.Round(score) : null;

    public PerfComponent Component(PerfComponentId id) => Components[(int)id];

    /// <summary>Sums <paramref name="records"/> in order, then scores the sums.</summary>
    public static PerfPooled Of(IEnumerable<PerfSums> records, SourceKind source)
    {
        ArgumentNullException.ThrowIfNull(records);
        var total = new PerfSums();
        foreach (var record in records)
        {
            total.Add(record);
        }

        return Of(total, source);
    }

    public static PerfPooled Of(PerfSums s, SourceKind source)
    {
        ArgumentNullException.ThrowIfNull(s);
        Span<(double? Measure, int Samples)> measures = stackalloc (double?, int)[7];
        measures[(int)PerfComponentId.T] = (s.N >= PerfFormula.MinSamples ? s.OutputSum / (s.DurationMsSum / 1000.0) : null, Count(s.N));
        measures[(int)PerfComponentId.L] = Latency(s.Bins);
        measures[(int)PerfComponentId.R] = (s.ReasonN >= PerfFormula.MinSamples ? s.ReasonOutput / (s.ReasonDurationMsSum / 1000.0) : null,
            Count(s.ReasonN));
        measures[(int)PerfComponentId.H] = (s.CacheN >= PerfFormula.MinSamples ? (double)s.CacheCached / s.CacheInput : null, Count(s.CacheN));
        var k = s.ToolN;
        if (k >= PerfFormula.MinSamples)
        {
            measures[(int)PerfComponentId.N] = (s.ActiveMs > 0 ? k / (s.ActiveMs / 60_000.0) : null, Count(k));
            measures[(int)PerfComponentId.U] = (Math.Exp(s.ToolLnSum / k), Count(k));
            measures[(int)PerfComponentId.S] = (1 - (double)s.ToolFailed / k, Count(k));
        }
        else
        {
            measures[(int)PerfComponentId.N] = (null, Count(k));
            measures[(int)PerfComponentId.U] = (null, Count(k));
            measures[(int)PerfComponentId.S] = (null, Count(k));
        }

        var (components, score) = PerfFormula.Combine(source, measures);
        return new PerfPooled(source, s.N, k, s.ActiveMs, s.N >= PerfFormula.PooledMinN ? score : null, components);
    }

    /// <summary>L: the geometric centre of the bucket that holds the ⌈count / 2⌉-th latency in ascending order.</summary>
    private static (double?, int) Latency(IReadOnlyList<long> bins)
    {
        long count = 0;
        foreach (var bin in bins)
        {
            count += bin;
        }

        if (count < PerfFormula.MinSamples)
        {
            return (null, Count(count));
        }

        long target = (count + 1) / 2, seen = 0;
        for (var k = 0; k < bins.Count; k++)
        {
            seen += bins[k];
            if (seen >= target)
            {
                return (LatencyBins.Centre(k), Count(count));
            }
        }

        return (null, Count(count));
    }

    private static int Count(long value) => (int)Math.Min(value, int.MaxValue);
}
