using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Perf;

/// <summary>
/// Scores one agent at <c>nowMs</c> (METRICS 6.9): the newest 20 measured responses of the last 30 minutes, the completed
/// tool calls inside their span, the exact union of their intervals, the seven components and the weighted geometric mean.
/// </summary>
internal static class PerfEvaluator
{
    /// <summary>
    /// The sample: the agent's measured responses with <c>nowMs − 30 min ≤ endMs ≤ nowMs</c>, the newest 20 by
    /// <c>endMs</c>, ties by key (ordinal: the smaller key is newer).
    /// </summary>
    public static List<ParsedResponse> Sample(IEnumerable<ParsedResponse> responses, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(responses);
        var chosen = responses.Where(r => IsMeasured(r) && r.EndMs >= nowMs - PerfFormula.WindowMs && r.EndMs <= nowMs).ToList();
        chosen.Sort(NewestFirst);
        if (chosen.Count > PerfFormula.SampleSize)
        {
            chosen.RemoveRange(PerfFormula.SampleSize, chosen.Count - PerfFormula.SampleSize);
        }

        return chosen;
    }

    /// <summary>Newest first by <c>endMs</c>, then by key (ordinal).</summary>
    public static int NewestFirst(ParsedResponse a, ParsedResponse b) =>
        a.EndMs != b.EndMs ? b.EndMs.CompareTo(a.EndMs) : string.CompareOrdinal(a.Key, b.Key);

    /// <summary>Final with a valid interval (METRICS 3.3).</summary>
    public static bool IsMeasured(ParsedResponse response) => response.Final && SpeedMath.TryGetSpeedInterval(response, out _);

    /// <param name="sample">The chosen sample (<see cref="Sample"/>), newest first.</param>
    /// <param name="actions">The agent's stored actions in file order (the last 64 count).</param>
    /// <param name="idle">The agent's phase after the staleness rule is idle.</param>
    public static PerfEvaluation Evaluate(SourceKind source, bool idle, IReadOnlyList<ParsedResponse> sample, IReadOnlyList<WorkAction> actions,
        long nowMs, IPerfBaselines? baselines = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(actions);
        var n = sample.Count;
        var intervals = new List<(long Start, long End)>(n + 8);
        long? spanStart = null;
        foreach (var response in sample)
        {
            SpeedMath.TryGetSpeedInterval(response, out var interval);
            intervals.Add((interval.StartMs, interval.EndMs));
            spanStart = spanStart is { } held ? Math.Min(held, interval.StartMs) : interval.StartMs;
        }

        var calls = n >= PerfFormula.MinSamples && spanStart is { } span ? Calls(actions, span, nowMs) : [];
        foreach (var call in calls)
        {
            intervals.Add((call.AtMs, call.ResultMs!.Value));
        }

        var active = UnionLength(intervals);
        Span<(double? Measure, int Samples)> measures = stackalloc (double?, int)[7];
        if (n >= PerfFormula.MinSamples)
        {
            MeasureResponses(source, sample, measures);
            MeasureCalls(source, calls, active, measures);
        }
        else
        {
            measures[(int)PerfComponentId.T] = (null, n);
        }

        var (components, score) = PerfFormula.Combine(source, measures);
        var form = score is null ? PerfForm.None : idle ? PerfForm.Resting : PerfForm.Live;
        var model = n > 0 ? sample[0].Model : null;
        var evaluation = new PerfEvaluation(source, form, score, n, calls.Count, active, spanStart, model, components);
        return score is not null && baselines?.Baseline(source, model ?? string.Empty) is { } baseline
            ? evaluation with { Baseline = baseline }
            : evaluation;
    }

    /// <summary>
    /// <c>C</c>: the stored actions (the last 64) with a result, <c>callMs ≥ spanStart</c>, <c>resultMs ≤ nowMs</c> and
    /// <c>0 ≤ resultMs − callMs ≤ 15 min</c>.
    /// </summary>
    public static List<WorkAction> Calls(IReadOnlyList<WorkAction> actions, long spanStart, long nowMs)
    {
        var calls = new List<WorkAction>();
        for (var i = Math.Max(0, actions.Count - PerfFormula.ToolCap); i < actions.Count; i++)
        {
            var action = actions[i];
            if (action.ResultMs is { } result && action.AtMs >= spanStart && result <= nowMs
                && result - action.AtMs >= 0 && result - action.AtMs <= PerfFormula.ToolMaxMs)
            {
                calls.Add(action);
            }
        }

        return calls;
    }

    /// <summary>The length of the union of the intervals; empty ones add nothing.</summary>
    public static long UnionLength(List<(long Start, long End)> intervals)
    {
        intervals.Sort();
        long total = 0;
        long? start = null, end = null;
        foreach (var (s, e) in intervals)
        {
            if (e <= s)
            {
                continue;
            }

            if (end is not { } current || s > current)
            {
                if (start is { } open)
                {
                    total += end!.Value - open;
                }

                start = s;
                end = e;
            }
            else
            {
                end = Math.Max(current, e);
            }
        }

        return total + (start is { } last ? end!.Value - last : 0);
    }

    private static void MeasureResponses(SourceKind source, IReadOnlyList<ParsedResponse> sample, Span<(double? Measure, int Samples)> measures)
    {
        long output = 0, duration = 0;
        var latencies = new List<long>(sample.Count);
        long reason = 0, reasonMs = 0, cachedSum = 0, inputSum = 0;
        int thought = 0, cached = 0;
        foreach (var response in sample)
        {
            var ms = DurationMs(response);
            output += response.Tokens.Output;
            duration += ms;
            if (response.FirstMs is { } first && response.StartMs is { } start && first >= start)
            {
                latencies.Add(first - start);
            }

            var reasoning = Math.Min(response.Tokens.Reasoning, response.Tokens.Output);
            if (reasoning > 0)
            {
                thought++;
                reason += reasoning;
                reasonMs += ms;
            }

            if (response.Tokens.Input > 0)
            {
                cached++;
                cachedSum += response.Tokens.CachedInput;
                inputSum += response.Tokens.Input;
            }
        }

        measures[(int)PerfComponentId.T] = (output / (duration / 1000.0), sample.Count);
        measures[(int)PerfComponentId.L] = (latencies.Count >= PerfFormula.MinSamples ? Median(latencies) : null, latencies.Count);
        measures[(int)PerfComponentId.R] = PerfSources.RecordsReasoning(source)
            ? (thought >= PerfFormula.MinSamples ? reason / (reasonMs / 1000.0) : null, thought)
            : (null, 0);
        measures[(int)PerfComponentId.H] = PerfSources.RecordsCache(source)
            ? (cached >= PerfFormula.MinSamples ? (double)cachedSum / inputSum : null, cached)
            : (null, 0);
    }

    private static void MeasureCalls(SourceKind source, List<WorkAction> calls, long active, Span<(double? Measure, int Samples)> measures)
    {
        var k = calls.Count;
        if (!PerfSources.RecordsActions(source) || k < PerfFormula.MinSamples)
        {
            measures[(int)PerfComponentId.N] = (null, k);
            measures[(int)PerfComponentId.U] = (null, k);
            measures[(int)PerfComponentId.S] = (null, k);
            return;
        }

        double logs = 0;
        var failed = 0;
        foreach (var call in calls)
        {
            logs = logs + Math.Log(Math.Max(call.ResultMs!.Value - call.AtMs, PerfFormula.ToolFloorMs));
            failed += call.Failed == true ? 1 : 0;
        }

        measures[(int)PerfComponentId.N] = (active > 0 ? k / (active / 60_000.0) : null, k);
        measures[(int)PerfComponentId.U] = (Math.Exp(logs / k), k);
        measures[(int)PerfComponentId.S] = (1 - (double)failed / k, k);
    }

    /// <summary>The 3.3 duration <c>endMs − startMs'</c> of a measured response.</summary>
    public static long DurationMs(ParsedResponse response) =>
        SpeedMath.TryGetSpeedInterval(response, out var interval) ? interval.DurationMs : 0;

    private static double Median(List<long> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}

/// <summary>The 14-day baseline of a source and raw model (METRICS 6.11 <i>Baseline</i>), as the store last saved it.</summary>
public interface IPerfBaselines
{
    /// <summary>The pooled score; null when the 14 days before today hold fewer than 20 responses.</summary>
    double? Baseline(SourceKind source, string model);
}
