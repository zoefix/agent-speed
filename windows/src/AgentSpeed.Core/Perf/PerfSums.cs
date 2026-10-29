namespace AgentSpeed.Core.Perf;

/// <summary>
/// The sufficient statistics of the performance score (METRICS 6.11 <i>Records</i>, 11.10 <c>perfStats</c>): additive over
/// days, files, reports and users, so any set of them pools exactly (<see cref="PerfPooled"/>). Mutable; not thread-safe.
/// </summary>
public sealed class PerfSums
{
    private readonly long[] _latencyBins;

    public PerfSums()
    {
        _latencyBins = new long[Perf.LatencyBins.Count];
    }

    private PerfSums(PerfSums other)
    {
        _latencyBins = (long[])other._latencyBins.Clone();
        N = other.N;
        OutputSum = other.OutputSum;
        DurationMsSum = other.DurationMsSum;
        ReasonN = other.ReasonN;
        ReasonOutput = other.ReasonOutput;
        ReasonDurationMsSum = other.ReasonDurationMsSum;
        CacheN = other.CacheN;
        CacheInput = other.CacheInput;
        CacheCached = other.CacheCached;
        ToolN = other.ToolN;
        ToolLnSum = other.ToolLnSum;
        ToolFailed = other.ToolFailed;
        ActiveMs = other.ActiveMs;
    }

    /// <summary>Measured responses.</summary>
    public long N { get; set; }

    public long OutputSum { get; set; }

    /// <summary>Σ <c>endMs − startMs'</c> (METRICS 3.3).</summary>
    public long DurationMsSum { get; set; }

    /// <summary>64 counts of first-content latencies (<see cref="Perf.LatencyBins"/>).</summary>
    public Span<long> LatencyBins => _latencyBins;

    public IReadOnlyList<long> Bins => _latencyBins;

    public long ReasonN { get; set; }

    public long ReasonOutput { get; set; }

    public long ReasonDurationMsSum { get; set; }

    public long CacheN { get; set; }

    public long CacheInput { get; set; }

    public long CacheCached { get; set; }

    /// <summary>Completed tool calls within 15 minutes.</summary>
    public long ToolN { get; set; }

    /// <summary>Σ <c>ln max(d, 50)</c>, added left to right in the order the calls count.</summary>
    public double ToolLnSum { get; set; }

    public long ToolFailed { get; set; }

    /// <summary>Active time by the online union rule (METRICS 6.11).</summary>
    public long ActiveMs { get; set; }

    /// <summary>Σ of the latency counts.</summary>
    public long LatencyCount
    {
        get
        {
            long count = 0;
            foreach (var bin in _latencyBins)
            {
                count += bin;
            }

            return count;
        }
    }

    public PerfSums Clone() => new(this);

    /// <summary>Adds <paramref name="other"/> field by field (bins bucket by bucket, <c>toolLnSum</c> as one double addition).</summary>
    public void Add(PerfSums other)
    {
        ArgumentNullException.ThrowIfNull(other);
        N += other.N;
        OutputSum += other.OutputSum;
        DurationMsSum += other.DurationMsSum;
        for (var k = 0; k < _latencyBins.Length; k++)
        {
            _latencyBins[k] += other._latencyBins[k];
        }

        ReasonN += other.ReasonN;
        ReasonOutput += other.ReasonOutput;
        ReasonDurationMsSum += other.ReasonDurationMsSum;
        CacheN += other.CacheN;
        CacheInput += other.CacheInput;
        CacheCached += other.CacheCached;
        ToolN += other.ToolN;
        ToolLnSum = ToolLnSum + other.ToolLnSum;
        ToolFailed += other.ToolFailed;
        ActiveMs += other.ActiveMs;
    }

    /// <summary>What one measured response adds (activeMs aside).</summary>
    /// <param name="durationMs">Its 3.3 duration.</param>
    /// <param name="latencyMs"><c>firstMs − startMs</c> when both exist and it is not negative.</param>
    public void AddResponse(SourceKind source, long output, long reasoning, long input, long cachedInput, long durationMs, long? latencyMs)
    {
        N++;
        OutputSum += output;
        DurationMsSum += durationMs;
        if (latencyMs is { } latency)
        {
            _latencyBins[Perf.LatencyBins.Of(latency)]++;
        }

        var thought = Math.Min(reasoning, output);
        if (PerfSources.RecordsReasoning(source) && thought > 0)
        {
            ReasonN++;
            ReasonOutput += thought;
            ReasonDurationMsSum += durationMs;
        }

        if (PerfSources.RecordsCache(source) && input > 0)
        {
            CacheN++;
            CacheInput += input;
            CacheCached += cachedInput;
        }
    }

    /// <summary>What one completed tool call within 15 minutes adds (activeMs aside).</summary>
    public void AddCall(long durationMs, bool failed)
    {
        ToolN++;
        ToolLnSum = ToolLnSum + Math.Log(Math.Max(durationMs, PerfFormula.ToolFloorMs));
        ToolFailed += failed ? 1 : 0;
    }

    /// <summary>Nothing counted.</summary>
    public bool IsEmpty => N == 0 && ToolN == 0 && ActiveMs == 0 && CacheN == 0 && ReasonN == 0 && LatencyCount == 0;
}
