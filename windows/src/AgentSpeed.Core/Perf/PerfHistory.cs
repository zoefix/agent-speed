using System.Globalization;
using AgentSpeed.Core.Unpriced;

namespace AgentSpeed.Core.Perf;

/// <summary>One day of a History row's trend: that day's pooled score, null (a gap) below 20 responses.</summary>
public readonly record struct PerfTrendPoint(string Day, double? Score);

/// <summary>One row of the History window's 性能表現 (METRICS 6.11 <i>History window</i>): a source and model over the range.</summary>
public sealed record PerfRangeRow(SourceKind Source, string Model, PerfPooled Pooled, IReadOnlyList<PerfTrendPoint> Trend);

/// <summary>
/// The baseline of the score card and the History window's rows (METRICS 6.11), over stored day records of every source.
/// Immutable once built.
/// </summary>
public sealed class PerfHistory : IPerfBaselines
{
    private readonly IReadOnlyList<PerfRecord> _records;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(SourceKind, string), double?> _baselines = new();

    /// <param name="today">The local date the baseline's 14 days end before.</param>
    public PerfHistory(IReadOnlyList<PerfRecord> records, string today)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
        Today = today;
    }

    public static PerfHistory Empty { get; } = new([], string.Empty);

    public IReadOnlyList<PerfRecord> Records => _records;

    public string Today { get; }

    /// <summary>
    /// 比平常: the pooled score of <paramref name="source"/> and the raw <paramref name="model"/> over the 14 local days
    /// before today; null below 20 responses.
    /// </summary>
    public double? Baseline(SourceKind source, string model) =>
        _baselines.GetOrAdd((source, model), key => BaselineOf(_records, key.Item1, key.Item2, Today));

    public static double? BaselineOf(IEnumerable<PerfRecord> records, SourceKind source, string model, string today)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (!TryParse(today, out var day))
        {
            return null;
        }

        var first = Format(day.AddDays(-PerfFormula.BaselineDays));
        var last = Format(day.AddDays(-1));
        var chosen = records.Where(r => r.Source == source && string.Equals(r.Model, model, StringComparison.Ordinal)
            && string.CompareOrdinal(r.Day, first) >= 0 && string.CompareOrdinal(r.Day, last) <= 0).Select(r => r.Sums);
        return PerfPooled.Of(chosen, source).Score;
    }

    /// <summary>The rows over [<paramref name="first"/>, <paramref name="last"/>], of one source when given.</summary>
    public IReadOnlyList<PerfRangeRow> Range(string first, string last, SourceKind? source = null) => RangeOf(_records, first, last, source);

    public static IReadOnlyList<PerfRangeRow> RangeOf(IEnumerable<PerfRecord> records, string first, string last, SourceKind? source = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var groups = new Dictionary<(SourceKind, string), List<PerfRecord>>();
        var order = new List<(SourceKind, string)>();
        foreach (var record in records)
        {
            if (string.CompareOrdinal(record.Day, first) < 0 || string.CompareOrdinal(record.Day, last) > 0
                || (source is { } only && record.Source != only))
            {
                continue;
            }

            var key = (record.Source, record.Model);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(key);
            }

            list.Add(record);
        }

        var days = Days(first, last);
        var rows = new List<PerfRangeRow>(order.Count);
        foreach (var key in order)
        {
            var part = groups[key];
            var trend = new PerfTrendPoint[days.Count];
            for (var i = 0; i < days.Count; i++)
            {
                var day = days[i];
                trend[i] = new PerfTrendPoint(day, PerfPooled.Of(part.Where(r => r.Day == day).Select(r => r.Sums), key.Item1).Score);
            }

            rows.Add(new PerfRangeRow(key.Item1, key.Item2, PerfPooled.Of(part.Select(r => r.Sums), key.Item1), trend));
        }

        rows.Sort(static (a, b) =>
        {
            if (a.Source != b.Source)
            {
                return a.Source.CompareTo(b.Source);
            }

            return a.Pooled.N != b.Pooled.N ? b.Pooled.N.CompareTo(a.Pooled.N) : Utf8Order.Instance.Compare(a.Model, b.Model);
        });
        return rows;
    }

    /// <summary>Every local date of [<paramref name="first"/>, <paramref name="last"/>].</summary>
    public static List<string> Days(string first, string last)
    {
        var days = new List<string>();
        if (!TryParse(first, out var day) || !TryParse(last, out var end))
        {
            return days;
        }

        for (; day <= end; day = day.AddDays(1))
        {
            days.Add(Format(day));
        }

        return days;
    }

    private static bool TryParse(string text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
