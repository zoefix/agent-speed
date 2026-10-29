using AgentSpeed.Core.Unpriced;

namespace AgentSpeed.Core.Perf;

/// <summary>One performance history record (METRICS 6.11 <i>Records</i>): a local day, a source and a raw model (empty when unknown).</summary>
public sealed record PerfRecord(string Day, SourceKind Source, string Model, PerfSums Sums)
{
    /// <summary>By day, then source order (METRICS 1), then the model's UTF-8 bytes.</summary>
    public static int Compare(PerfRecord? a, PerfRecord? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        var day = string.CompareOrdinal(a.Day, b.Day);
        if (day != 0)
        {
            return day;
        }

        return a.Source != b.Source ? a.Source.CompareTo(b.Source) : Utf8Order.Instance.Compare(a.Model, b.Model);
    }
}

/// <summary>A record's identity.</summary>
public readonly record struct PerfRecordKey(string Day, SourceKind Source, string Model);
