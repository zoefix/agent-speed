using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Unpriced;

namespace AgentSpeed.Presentation.History.Perf;

/// <summary>A column of the 性能表現 table that sorts.</summary>
public enum PerfSortColumn
{
    Model,
    Score,
    Usual,
    Timed,
    Throughput,
    Latency,
    Reasoning,
    Pace,
    ToolTime,
    ToolSuccess,
    Cache,
}

/// <summary>
/// The table's sort, which the window remembers (DESIGN 12): by default the score descending — rows without a score after,
/// by timed requests descending — then the model id. Any other column sorts by its value, absent values last, then the same.
/// </summary>
public sealed record PerfSort(PerfSortColumn Column = PerfSortColumn.Score, bool Descending = true)
{
    public static PerfSort Default { get; } = new();

    /// <summary>A click on a header: the same column flips, another starts descending (the model ascending).</summary>
    public PerfSort Toggle(PerfSortColumn column) =>
        column == Column ? this with { Descending = !Descending } : new PerfSort(column, column != PerfSortColumn.Model);

    public IReadOnlyList<PerfHistoryRow> Apply(IEnumerable<PerfHistoryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        list.Sort(Compare);
        return list;
    }

    private int Compare(PerfHistoryRow a, PerfHistoryRow b)
    {
        int order;
        if (Column == PerfSortColumn.Model)
        {
            order = Utf8Order.Instance.Compare(a.ModelText, b.ModelText);
            return Descending ? -order : order;
        }

        var (x, y) = (Key(a), Key(b));
        if (x is null != y is null)
        {
            return x is null ? 1 : -1;
        }

        order = x is { } vx && y is { } vy ? vx.CompareTo(vy) : 0;
        if (order != 0)
        {
            return Descending ? -order : order;
        }

        if (a.Timed != b.Timed)
        {
            return b.Timed.CompareTo(a.Timed);
        }

        order = Utf8Order.Instance.Compare(a.ModelText, b.ModelText);
        return order != 0 ? order : a.Source.CompareTo(b.Source);
    }

    private double? Key(PerfHistoryRow row) => Column switch
    {
        PerfSortColumn.Score => row.Score,
        PerfSortColumn.Usual => row.Usual,
        PerfSortColumn.Timed => row.Timed,
        _ => Cell(row, Component(Column)),
    };

    /// <summary>Components sort by their sub-score: higher is always better, whatever the unit.</summary>
    private static double? Cell(PerfHistoryRow row, PerfComponentId id) =>
        row.Cells.FirstOrDefault(c => c.Id == id)?.Value;

    public static PerfComponentId Component(PerfSortColumn column) => column switch
    {
        PerfSortColumn.Throughput => PerfComponentId.T,
        PerfSortColumn.Latency => PerfComponentId.L,
        PerfSortColumn.Reasoning => PerfComponentId.R,
        PerfSortColumn.Pace => PerfComponentId.N,
        PerfSortColumn.ToolTime => PerfComponentId.U,
        PerfSortColumn.ToolSuccess => PerfComponentId.S,
        _ => PerfComponentId.H,
    };
}
