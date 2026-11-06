using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Engine;

/// <summary>
/// Everything the engine keeps per (host, source) cell (DESIGN 13): the live values of the window (METRICS 6.3, 6.9, 9.5)
/// and today's totals and cost (6.6, 9.6). Per source they add up to the engine's per-source values.
/// </summary>
/// <param name="Unpriced">Today's unpriced requests by raw model (<c>""</c> = unknown).</param>
internal sealed record EngineCell(
    Rate Live,
    Rate LiveEstimated,
    LiveCost LiveCost,
    IntelligenceSums Intelligence,
    TokenTotals Today,
    CostTally TodayCost,
    IReadOnlyDictionary<string, long> Unpriced)
{
    public static EngineCell Empty { get; } = new(Rate.Zero, Rate.Zero, LiveCost.Zero, IntelligenceSums.Zero, TokenTotals.Zero, CostTally.Zero,
        new Dictionary<string, long>());
}

/// <summary>Today's part of a cell, kept by the day ledger as responses arrive. Mutable; owned by the ledger.</summary>
internal sealed class CellDay
{
    private readonly Dictionary<string, long> _unpriced = new(StringComparer.Ordinal);

    public TokenTotals Today { get; private set; }

    public CostTally Cost { get; private set; }

    public IReadOnlyDictionary<string, long> Unpriced => _unpriced;

    public bool IsEmpty => Today.Requests == 0 && Cost == CostTally.Zero && _unpriced.Count == 0;

    public void Apply(in TokenCounts tokens, long? cost, long requests, string model, int sign)
    {
        Today = TodayLedger.Add(Today, tokens, sign, requests);
        Cost = Cost.Add(cost, sign, requests);
        if (cost is not null)
        {
            return;
        }

        var count = _unpriced.GetValueOrDefault(model) + (sign * requests);
        if (count == 0)
        {
            _unpriced.Remove(model);
        }
        else
        {
            _unpriced[model] = count;
        }
    }
}

/// <summary>The live window's tokens, estimates, costs and thinking sums per cell, before dividing by the window length.</summary>
internal sealed class LiveCells
{
    private readonly Dictionary<HostSource, (double Output, double Total)> _tokens = [];
    private readonly Dictionary<HostSource, (double Output, double Total)> _estimated = [];
    private readonly Dictionary<HostSource, LiveCost> _cost = [];
    private readonly Dictionary<HostSource, IntelligenceSums> _intelligence = [];

    public void Add(HostSource cell, double output, double total, bool estimated)
    {
        if (output == 0 && total == 0)
        {
            return;
        }

        var sum = _tokens.GetValueOrDefault(cell);
        _tokens[cell] = (sum.Output + output, sum.Total + total);
        if (estimated)
        {
            var estimate = _estimated.GetValueOrDefault(cell);
            _estimated[cell] = (estimate.Output + output, estimate.Total + total);
        }
    }

    public void AddCost(HostSource cell, LiveCost value) => _cost[cell] = _cost.GetValueOrDefault(cell) + value;

    public void AddIntelligence(HostSource cell, double weight, double reasoning) =>
        _intelligence[cell] = _intelligence.GetValueOrDefault(cell).Add(cell.Source, weight, reasoning);

    /// <summary>Merges an agent's performance value into its cell (METRICS 6.9 <i>Sets and groups</i>).</summary>
    public void AddScore(HostSource cell, Perf.PerfSetValue score)
    {
        var sums = _intelligence.GetValueOrDefault(cell);
        _intelligence[cell] = sums with { Score = Perf.PerfSetValue.Merge(sums.Score, score) };
    }

    /// <summary>The cells: rates and costs per second, like the window's other values, and today's part from the ledger.</summary>
    public Dictionary<HostSource, EngineCell> Cells(double seconds, IReadOnlyDictionary<HostSource, CellDay> days)
    {
        var keys = new HashSet<HostSource>(_tokens.Keys);
        keys.UnionWith(_cost.Keys);
        keys.UnionWith(_intelligence.Keys);
        keys.UnionWith(days.Keys);
        var cells = new Dictionary<HostSource, EngineCell>(keys.Count);
        foreach (var key in keys)
        {
            var tokens = _tokens.GetValueOrDefault(key);
            var estimated = _estimated.GetValueOrDefault(key);
            var cost = _cost.GetValueOrDefault(key);
            var day = days.GetValueOrDefault(key);
            cells[key] = new EngineCell(
                new Rate(tokens.Output / seconds, tokens.Total / seconds),
                new Rate(estimated.Output / seconds, estimated.Total / seconds),
                new LiveCost(cost.MicroUsdPerSecond / seconds, cost.KnownOutput / seconds),
                _intelligence.GetValueOrDefault(key),
                day?.Today ?? TokenTotals.Zero,
                day?.Cost ?? CostTally.Zero,
                day is null ? EngineCell.Empty.Unpriced : new Dictionary<string, long>(day.Unpriced, StringComparer.Ordinal));
        }

        return cells;
    }
}
