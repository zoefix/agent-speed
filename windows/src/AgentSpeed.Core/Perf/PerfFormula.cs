namespace AgentSpeed.Core.Perf;

/// <summary>
/// The constants and the weighted geometric mean of the performance score (METRICS 6.9 <i>Components</i>): 1000 is the
/// reference level and there is no upper limit.
/// </summary>
public static class PerfFormula
{
    public const int SampleSize = 20;
    public const int MinSamples = 3;
    public const int ProvisionalBelow = 10;
    public const long WindowMs = 30 * 60_000;
    public const int ToolCap = 64;
    public const long ToolMaxMs = 15 * 60_000;
    public const long ToolFloorMs = 50;
    public const int PooledMinN = 20;
    public const int BaselineDays = 14;
    public const double Reference = 1000;

    private const double ThroughputFloor = 0.01;
    private const double RatioLow = 0.05;
    private const double RatioHigh = 20;

    public static IReadOnlyList<PerfComponentId> Order { get; } =
        [PerfComponentId.T, PerfComponentId.L, PerfComponentId.N, PerfComponentId.R, PerfComponentId.H, PerfComponentId.U, PerfComponentId.S];

    public static double Weight(PerfComponentId id) => id switch
    {
        PerfComponentId.T => 0.30,
        PerfComponentId.L => 0.20,
        PerfComponentId.N => 0.15,
        PerfComponentId.R => 0.10,
        PerfComponentId.H => 0.10,
        PerfComponentId.U => 0.10,
        _ => 0.05,
    };

    /// <summary>The ratio of a measure to its reference, before clamping.</summary>
    public static double RawRatio(PerfComponentId id, double measure) => id switch
    {
        PerfComponentId.T => measure / 50,
        PerfComponentId.L => 5000 / Math.Max(measure, 250),
        PerfComponentId.N => measure / 3,
        PerfComponentId.R => measure / 20,
        PerfComponentId.H => measure / 0.95,
        PerfComponentId.U => 400 / measure,
        _ => measure / 0.95,
    };

    /// <summary>T has a floor and no upper bound; every other ratio is clamped to [0.05, 20].</summary>
    public static double Clamp(PerfComponentId id, double ratio) =>
        id == PerfComponentId.T ? Math.Max(ThroughputFloor, ratio) : Math.Min(RatioHigh, Math.Max(RatioLow, ratio));

    /// <summary>
    /// The components and the score of a set of measures (null: absent) with their samples, in <see cref="Order"/>.
    /// The sums are plain left-to-right additions, as the reference's <c>seq_sum</c>.
    /// </summary>
    public static (PerfComponent[] Components, double? Score) Combine(SourceKind source, ReadOnlySpan<(double? Measure, int Samples)> measures)
    {
        var components = new PerfComponent[Order.Count];
        double logSum = 0, weights = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var id = Order[i];
            var (measure, samples) = measures[i];
            if (measure is not { } value)
            {
                var reason = PerfSources.NotRecorded(id, source) ? PerfAbsence.NotRecorded : PerfAbsence.FewSamples;
                components[i] = new PerfComponent(id, reason, samples, null, null);
                continue;
            }

            var ratio = Clamp(id, RawRatio(id, value));
            components[i] = new PerfComponent(id, PerfAbsence.None, samples, value, ratio);
            logSum = logSum + Weight(id) * Math.Log(ratio);
            weights = weights + Weight(id);
        }

        return (components, weights > 0 ? Reference * Math.Exp(logSum / weights) : null);
    }

    /// <summary>Rounded half away from zero exactly as the reference; saturates at the int range.</summary>
    public static int Round(double value)
    {
        var shifted = value >= 0 ? Math.Floor(value + 0.5) : -Math.Floor(-value + 0.5);
        if (double.IsNaN(shifted))
        {
            return 0;
        }

        return shifted >= int.MaxValue ? int.MaxValue : shifted <= -int.MaxValue ? -int.MaxValue : (int)shifted;
    }
}
