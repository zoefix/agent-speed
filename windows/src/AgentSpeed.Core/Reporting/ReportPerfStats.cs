using AgentSpeed.Core.Perf;

namespace AgentSpeed.Core.Reporting;

/// <summary>
/// One <c>perfStats</c> row as sent (METRICS 11.10, Statistics): the 6.11 record fields of one model and source, with the
/// latency buckets sparse and <c>toolLnSum</c> rounded half away from zero to 6 decimals.
/// </summary>
public static class ReportPerfRow
{
    public const int LnDecimals = 6;

    /// <summary>A row is listed when it counts a response or a tool call.</summary>
    public static bool IsListed(PerfSums sums)
    {
        ArgumentNullException.ThrowIfNull(sums);
        return sums.N + sums.ToolN >= 1;
    }

    public static ReportJson Json(SourceKind source, PerfSums sums)
    {
        ArgumentNullException.ThrowIfNull(sums);
        var bins = new List<ReportJson>();
        for (var k = 0; k < sums.Bins.Count; k++)
        {
            if (sums.Bins[k] > 0)
            {
                bins.Add(ReportJson.Array([ReportJson.Of(k), ReportJson.Of(sums.Bins[k])]));
            }
        }

        return ReportJson.Object(
            ("source", ReportJson.Of(ReportBody.SourceId(source))),
            ("n", ReportJson.Of(sums.N)),
            ("outputSum", ReportJson.Of(sums.OutputSum)),
            ("durationMsSum", ReportJson.Of(sums.DurationMsSum)),
            ("latencyBins", ReportJson.Array(bins)),
            ("reasonN", ReportJson.Of(sums.ReasonN)),
            ("reasonOutput", ReportJson.Of(sums.ReasonOutput)),
            ("reasonDurationMsSum", ReportJson.Of(sums.ReasonDurationMsSum)),
            ("cacheN", ReportJson.Of(sums.CacheN)),
            ("cacheInput", ReportJson.Of(sums.CacheInput)),
            ("cacheCached", ReportJson.Of(sums.CacheCached)),
            ("toolN", ReportJson.Of(sums.ToolN)),
            ("toolLnSum", ReportJson.Decimal(Math.Max(0, sums.ToolLnSum), LnDecimals)),
            ("toolFailed", ReportJson.Of(sums.ToolFailed)),
            ("activeMs", ReportJson.Of(sums.ActiveMs)));
    }

    /// <summary>Field-wise <paramref name="a"/> − <paramref name="b"/>, never negative: the rows counted since a build.</summary>
    public static PerfSums Minus(PerfSums a, PerfSums b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var d = new PerfSums
        {
            N = Math.Max(0, a.N - b.N),
            OutputSum = Math.Max(0, a.OutputSum - b.OutputSum),
            DurationMsSum = Math.Max(0, a.DurationMsSum - b.DurationMsSum),
            ReasonN = Math.Max(0, a.ReasonN - b.ReasonN),
            ReasonOutput = Math.Max(0, a.ReasonOutput - b.ReasonOutput),
            ReasonDurationMsSum = Math.Max(0, a.ReasonDurationMsSum - b.ReasonDurationMsSum),
            CacheN = Math.Max(0, a.CacheN - b.CacheN),
            CacheInput = Math.Max(0, a.CacheInput - b.CacheInput),
            CacheCached = Math.Max(0, a.CacheCached - b.CacheCached),
            ToolN = Math.Max(0, a.ToolN - b.ToolN),
            ToolFailed = Math.Max(0, a.ToolFailed - b.ToolFailed),
            ActiveMs = Math.Max(0, a.ActiveMs - b.ActiveMs),
        };
        d.ToolLnSum = d.ToolN > 0 ? Math.Max(0, a.ToolLnSum - b.ToolLnSum) : 0;
        for (var k = 0; k < LatencyBins.Count; k++)
        {
            d.LatencyBins[k] = Math.Max(0, a.Bins[k] - b.Bins[k]);
        }

        return d;
    }
}

/// <summary>
/// A <c>perfScore</c> object (METRICS 11.10, Body): a set's highest performance score with its form (6.9 <i>Values</i>)
/// and its live thinking speed, unrounded until written.
/// </summary>
public readonly record struct ReportPerfValue(PerfForm Form, double? Score, double Thinking)
{
    /// <summary>A reported score above this, which only a broken log could give, is sent as this.</summary>
    public const int MaxScore = 10_000_000;

    /// <summary>The set rule: the highest live score, else the highest resting score, else none.</summary>
    public static ReportPerfValue Of(IEnumerable<ReportPerfAgent> agents, double thinking)
    {
        ArgumentNullException.ThrowIfNull(agents);
        double? live = null, resting = null;
        foreach (var agent in agents)
        {
            switch (agent.Form)
            {
                case PerfForm.Live:
                    live = Math.Max(live ?? double.MinValue, agent.Score);
                    break;
                case PerfForm.Resting:
                    resting = Math.Max(resting ?? double.MinValue, agent.Score);
                    break;
            }
        }

        return live is { } l ? new(PerfForm.Live, l, thinking)
            : resting is { } r ? new(PerfForm.Resting, r, thinking)
            : new(PerfForm.None, null, thinking);
    }

    /// <summary>The retired <c>taskScore</c>: the thinking speed only.</summary>
    public static ReportPerfValue Legacy(double thinking) => new(PerfForm.None, null, thinking);

    /// <summary>The score as sent: an integer 0 – 10 000 000, rounded half away from zero; null for none.</summary>
    public int? Shown => Form == PerfForm.None || Score is not { } score ? null : Math.Clamp(PerfFormula.Round(score), 0, MaxScore);

    /// <summary><c>thinkingTokS</c> as sent (METRICS 11.7).</summary>
    public double ThinkingTokS => ReportJson.Round(Math.Clamp(double.IsFinite(Thinking) ? Thinking : 0, 0, ReportBody.MaxTokS), 1);

    public string FormName => Shown is null ? "none" : Form == PerfForm.Live ? "live" : "resting";

    public ReportJson Json => ReportJson.Object(
        ("score", ReportJson.OfOptional(Shown)),
        ("form", ReportJson.Of(FormName)),
        ("thinkingTokS", ReportJson.Decimal(ThinkingTokS, 1)));

    /// <summary>The value rounded as sent, for the WebSocket changed rule.</summary>
    public (int? Score, string Form, double Thinking) Sent => (Shown, FormName, ThinkingTokS);
}

/// <summary>An agent of the all-sources set with a score (METRICS 11.10): its source, current model's report id, form and score.</summary>
public readonly record struct ReportPerfAgent(SourceKind Source, string ModelId, PerfForm Form, double Score);

/// <summary>
/// The performance score's live facts of a tick (METRICS 11.10): the agents of the all-sources set that have a score, the
/// set's thinking speed and every report id's thinking speed (its raw models' model sums added).
/// </summary>
public sealed record ReportPerfFacts(IReadOnlyList<ReportPerfAgent> Agents, double Thinking, IReadOnlyDictionary<string, double> ModelThinking)
{
    public static ReportPerfFacts Empty { get; } = new([], 0, new Dictionary<string, double>(StringComparer.Ordinal));

    /// <summary>The top-level value: the highest of every agent, with the all-sources thinking.</summary>
    public ReportPerfValue Top => ReportPerfValue.Of(Agents, Thinking);
}

/// <summary>Which score extension a report carries (METRICS 11.10, Active and Legacy).</summary>
public enum ReportScoreExtension
{
    None,

    /// <summary><c>perfScore</c>: the values and the statistics.</summary>
    Perf,

    /// <summary>The retired <c>taskScore</c>: the thinking speed only.</summary>
    LegacyTask,
}
