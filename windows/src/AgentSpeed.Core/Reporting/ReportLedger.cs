using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Reporting;

/// <summary>
/// The report ledger of METRICS 11.4: which responses count for live reporting and what each one already added.
/// Increments accumulate in <see cref="Counters"/>; windows are differences of it. Owned by the engine's thread.
/// </summary>
internal sealed class ReportLedger
{
    public const int MaxCountedKeys = 65_536;

    private readonly Dictionary<string, Reported> _counted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ReportUsage Usage, int Members)> _traceRounds = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public ReportCounters Counters { get; } = new();

    public int CountedKeys => _counted.Count;

    public bool IsCounted(string key) => _counted.ContainsKey(key);

    /// <summary>Counts a response at its first live emission (a key already counted grows instead).</summary>
    /// <param name="currentEffort">The agent's current effort now, which an unknown effort freezes (EXECUTION-MODES, Ledger).</param>
    public void Count(ParsedResponse response, string modelId, ExecutionModeAttribution? currentEffort = null)
    {
        if (_counted.ContainsKey(response.Key))
        {
            Grow(response, modelId);
            return;
        }

        var values = Values(response);
        if (currentEffort is { } effort)
        {
            values = values with { Modes = values.Modes.WithLedgerEffort(effort) };
        }

        Counters.Add(ReportGrowth(values.Usage, response, null), new ReportModelKey(modelId, response.Source), values.Modes);
        _counted[response.Key] = values;
        _order.Enqueue(response.Key);
        while (_counted.Count > MaxCountedKeys && _order.TryDequeue(out var oldest))
        {
            Forget(oldest);
        }
    }

    /// <summary>Growth of a counted response (an upsert or a re-pricing): each field adds what exceeds what it already added.</summary>
    public void Grow(ParsedResponse response, string modelId)
    {
        if (!_counted.TryGetValue(response.Key, out var reported))
        {
            return;
        }

        var now = Values(response);
        var growth = now.Usage - reported.Usage;
        Counters.Add(ReportGrowth(growth, response, reported), new ReportModelKey(modelId, response.Source), reported.Modes);
        _counted[response.Key] = reported with { Usage = reported.Usage + growth, EndMs = Math.Max(reported.EndMs, now.EndMs),
            TraceRound = reported.TraceRound ?? response.TraceRoundKey };
    }

    // METRICS 4.21: only growth beyond what the other representation of the same round already reported.
    private ReportUsage ReportGrowth(ReportUsage growth, ParsedResponse response, Reported? previous)
    {
        if (response.Source != SourceKind.OpenBitFun) return growth;
        var old = previous?.Usage ?? default;
        if ((previous?.TraceRound ?? response.TraceRoundKey) is { } round)
        {
            var traces = _traceRounds.GetValueOrDefault(round);
            if (previous?.TraceRound is null) { traces.Usage += old; traces.Members++; }
            var covered = _counted.GetValueOrDefault(round).Usage - traces.Usage;
            _traceRounds[round] = (traces.Usage + growth, traces.Members);
            return growth - covered;
        }
        return _traceRounds.TryGetValue(response.Key, out var group) ? growth - (group.Usage - old) : growth;
    }

    private void Forget(string key)
    {
        if (_counted.Remove(key, out var value) && value.TraceRound is { } round)
        {
            var group = _traceRounds[round];
            if (--group.Members == 0 && !_counted.ContainsKey(round)) _traceRounds.Remove(round);
            else _traceRounds[round] = group;
        }
        if (_traceRounds.TryGetValue(key, out var traces) && traces.Members == 0) _traceRounds.Remove(key);
    }

    /// <summary>Forgets counted keys whose response left the engine's retention (by its latest end).</summary>
    public void Prune(long cutoffMs)
    {
        List<string>? expired = null;
        foreach (var (key, reported) in _counted)
        {
            if (reported.EndMs < cutoffMs)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            Forget(key);
        }

        var kept = _order.Where(_counted.ContainsKey).ToArray();
        _order.Clear();
        foreach (var key in kept)
        {
            _order.Enqueue(key);
        }
    }

    private static Reported Values(ParsedResponse response)
    {
        var tokens = response.Tokens;
        var measured = response.Final && SpeedMath.TryGetSpeedInterval(response, out var interval) ? interval.DurationMs : (long?)null;
        var usage = new ReportUsage(response.Requests, tokens.Output, tokens.Input, tokens.CachedInput, tokens.CacheWriteInput,
            tokens.Reasoning, measured is null ? 0 : tokens.Output, measured ?? 0, response.CostMicroUsd ?? 0,
            response.CostMicroUsd is null ? response.Requests : 0);
        return new(usage, response.EndMs, response.Modes, response.TraceRoundKey);
    }

    private readonly record struct Reported(ReportUsage Usage, long EndMs, ExecutionModes Modes, string? TraceRound);
}
