using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Reporting;

/// <summary>
/// The engine side of the statistics ledger (METRICS 11.10, Watch and Counting): while the watch is on, a measured response
/// counts once when it first becomes measured (if the report ledger counted its key), and a tool call once when its first
/// result is read live; each counted interval adds to <c>activeMs</c> by the online union rule (one <c>coveredUntil</c> per
/// agent and resolved id, kept until a relaunch). Owned by the engine's thread.
/// </summary>
internal sealed class ReportPerfLedger(ReportCounters counters)
{
    private const int MaxKeys = 65_536;

    private readonly Dictionary<(string Agent, string ModelId), long> _covered = [];
    private readonly HashSet<string> _measured = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>The watch the controller last published; a new generation starts the rows over (METRICS 11.10, Break).</summary>
    public ReportPerfWatch Watch { get; private set; } = ReportPerfWatch.Off;

    /// <summary>The engine instance the watch's taken counts refer to.</summary>
    public long Epoch { get; set; }

    public void Follow(ReportPerfWatch watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        Watch = watch;
        counters.StartPerfGeneration(watch.Generation);
    }

    /// <summary>
    /// An upsert: the first one that makes a response measured counts it, with its values at that moment, when
    /// <paramref name="counted"/> (the report ledger counted its key) and the watch is on. True when counted.
    /// </summary>
    public bool Response(ParsedResponse response, string modelId, bool counted)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Final || !SpeedMath.TryGetSpeedInterval(response, out var interval) || !_measured.Add(response.Key))
        {
            return false;
        }

        _order.Enqueue(response.Key);
        while (_measured.Count > MaxKeys && _order.TryDequeue(out var oldest))
        {
            _measured.Remove(oldest);
        }

        if (!counted || !Watch.On || Pending(response: true) >= ReportPerfWatch.MaxResponses)
        {
            return false;
        }

        long? latency = response.FirstMs is { } first && response.StartMs is { } start && first >= start ? first - start : null;
        var key = new ReportModelKey(modelId, response.Source);
        counters.PerfRow(key).AddResponse(response.Source, response.Tokens.Output, response.Tokens.Reasoning, response.Tokens.Input,
            response.Tokens.CachedInput, interval.DurationMs, latency);
        Active(response.AgentId, key, interval.StartMs, interval.EndMs);
        counters.CountedPerf(response: true);
        return true;
    }

    /// <summary>
    /// A tool call whose first result was read live at <paramref name="nowMs"/>: counted when the watch is on, the result is at
    /// most 2 minutes old and the call took 0 – 15 minutes. True when counted.
    /// </summary>
    public bool Call(string agentId, SourceKind source, in WorkAction action, string modelId, long nowMs)
    {
        if (!Watch.On || action.ResultMs is not { } result || nowMs - result > ReportPerfWatch.FirstSeenMs
            || result - action.AtMs < 0 || result - action.AtMs > PerfFormula.ToolMaxMs || Pending(response: false) >= ReportPerfWatch.MaxCalls)
        {
            return false;
        }

        var key = new ReportModelKey(modelId, source);
        counters.PerfRow(key).AddCall(result - action.AtMs, action.Failed == true);
        Active(agentId, key, action.AtMs, result);
        counters.CountedPerf(response: false);
        return true;
    }

    private long Pending(bool response)
    {
        var taken = Watch.TakenEpoch == Epoch ? (response ? Watch.TakenResponses : Watch.TakenCalls) : 0;
        return (response ? counters.PerfResponses : counters.PerfCalls) - taken;
    }

    private void Active(string agentId, ReportModelKey key, long start, long end)
    {
        var id = (agentId, key.ModelId);
        var added = _covered.TryGetValue(id, out var covered) ? Math.Max(0, end - Math.Max(start, covered)) : Math.Max(0, end - start);
        _covered[id] = _covered.ContainsKey(id) ? Math.Max(covered, end) : end;
        counters.PerfRow(key).ActiveMs += added;
    }
}
