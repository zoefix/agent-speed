using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Perf;

/// <summary>
/// The performance history records of agents read whole (METRICS 6.11 <i>Records</i>): responses and tool calls taken in
/// the order they complete, per local day, source and raw model, with <c>activeMs</c> by the online union rule (one
/// <c>coveredUntil</c> per agent and model). Not thread-safe.
/// </summary>
internal sealed class PerfRecorder(Func<long, string?> dayOf)
{
    private readonly Dictionary<PerfRecordKey, PerfSums> _records = [];
    private readonly Dictionary<(string Agent, string Model), long> _covered = [];

    public bool IsEmpty => _records.Count == 0;

    /// <summary>A completion of <paramref name="agentId"/>; one without a date (METRICS 8) is left out.</summary>
    public void Add(string agentId, SourceKind source, in PerfCompletion completion)
    {
        if (completion.Response is { } response)
        {
            Response(agentId, source, response);
        }
        else
        {
            Call(agentId, source, completion.Action);
        }
    }

    /// <summary>A response with its final values; one that never became measured does not count.</summary>
    public void Response(string agentId, SourceKind source, ParsedResponse response)
    {
        if (!response.Final || !SpeedMath.TryGetSpeedInterval(response, out var interval) || dayOf(response.EndMs) is not { } day)
        {
            return;
        }

        var model = response.Model ?? string.Empty;
        long? latency = response.FirstMs is { } first && response.StartMs is { } start && first >= start ? first - start : null;
        Record(day, source, model).AddResponse(source, response.Tokens.Output, response.Tokens.Reasoning, response.Tokens.Input,
            response.Tokens.CachedInput, interval.DurationMs, latency);
        Active(agentId, source, model, interval.StartMs, interval.EndMs);
    }

    /// <summary>A tool call with its first result, within 15 minutes.</summary>
    public void Call(string agentId, SourceKind source, in WorkAction action)
    {
        if (action.ResultMs is not { } result || result - action.AtMs < 0 || result - action.AtMs > PerfFormula.ToolMaxMs
            || dayOf(result) is not { } day)
        {
            return;
        }

        var model = action.Model ?? string.Empty;
        Record(day, source, model).AddCall(result - action.AtMs, action.Failed == true);
        Active(agentId, source, model, action.AtMs, result);
    }

    /// <summary>Every record, by day, source order and model (UTF-8 bytes).</summary>
    public List<PerfRecord> Records()
    {
        var records = _records.Select(r => new PerfRecord(r.Key.Day, r.Key.Source, r.Key.Model, r.Value)).ToList();
        records.Sort(PerfRecord.Compare);
        return records;
    }

    private PerfSums Record(string day, SourceKind source, string model)
    {
        var key = new PerfRecordKey(day, source, model);
        if (!_records.TryGetValue(key, out var sums))
        {
            _records[key] = sums = new PerfSums();
        }

        return sums;
    }

    private void Active(string agentId, SourceKind source, string model, long start, long end)
    {
        var key = (agentId, model);
        var added = _covered.TryGetValue(key, out var covered) ? Math.Max(0, end - Math.Max(start, covered)) : Math.Max(0, end - start);
        _covered[key] = _covered.ContainsKey(key) ? Math.Max(covered, end) : end;
        if (added > 0 && dayOf(end) is { } day)
        {
            Record(day, source, model).ActiveMs += added;
        }
    }
}

/// <summary>
/// One completion of an agent (METRICS 6.11 <i>Completion order</i>): a response when it first became measured, or a tool
/// call when its first result was read.
/// </summary>
internal readonly record struct PerfCompletion(ParsedResponse? Response, WorkAction Action)
{
    public static PerfCompletion Of(ParsedResponse response) => new(response, default);

    public static PerfCompletion Of(in WorkAction action) => new(null, action);
}
