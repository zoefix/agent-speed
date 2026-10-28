using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Presentation.History;

/// <summary>
/// One day's traffic for the selected source, or over every source with this computer's total (METRICS 10.9).
/// </summary>
/// <param name="Own">The agents' own processes.</param>
/// <param name="Tools">Their tool processes.</param>
/// <param name="BySource">Per source, as displayed (tools per the setting), non-zero only, for stacking.</param>
/// <param name="Machine">This computer; only with every source.</param>
/// <param name="RecordedMs">How long the day was recorded.</param>
/// <param name="Partial">The day was recorded for less than its elapsed time.</param>
public sealed record DayTraffic(
    TrafficBytes Own,
    TrafficBytes Tools,
    IReadOnlyDictionary<SourceKind, TrafficBytes> BySource,
    TrafficBytes? Machine,
    long RecordedMs,
    bool Partial,
    bool IncludeTools)
{
    /// <summary>The displayed bytes: tools per the setting.</summary>
    public TrafficBytes Shown => IncludeTools ? Own + Tools : Own;
}

/// <summary>
/// The stored traffic days and how they count in the Usage History window (METRICS 10.9, DESIGN 10). Days before
/// measuring began have none: they show as not recorded, never as 0.
/// </summary>
public sealed class HistoryTraffic
{
    /// <summary>Sampling starts a moment after launch and the last write lags up to 60 s.</summary>
    private const long PartialSlackMs = 120_000;

    private readonly Dictionary<DateOnly, Dictionary<string, TrafficDay>> _rows = [];

    public HistoryTraffic(IEnumerable<TrafficDay> days, string? since, bool includeTools)
    {
        ArgumentNullException.ThrowIfNull(days);
        foreach (var row in days)
        {
            if (row is null || !HistoryRanges.TryParseDay(row.Day, out var day))
            {
                continue;
            }

            if (!_rows.TryGetValue(day, out var keyed))
            {
                keyed = new Dictionary<string, TrafficDay>(StringComparer.Ordinal);
                _rows[day] = keyed;
            }

            keyed[row.Key] = row;
        }

        Since = HistoryRanges.TryParseDay(since, out var first) ? first : _rows.Count > 0 ? _rows.Keys.Min() : null;
        IncludeTools = includeTools;
    }

    public static HistoryTraffic None { get; } = new([], null, includeTools: true);

    /// <summary>The first day with a stored sample (<c>trafficSince</c>).</summary>
    public DateOnly? Since { get; }

    public bool IncludeTools { get; }

    public bool HasData => _rows.Count > 0;

    /// <summary>Traffic was ever measured: the table shows its column.</summary>
    public bool IsMeasured => HasData || Since is not null;

    public bool NotRecorded(DateOnly day) => Since is not { } since || day < since;

    /// <summary>
    /// The day's traffic for <paramref name="source"/> (every source when null); null before measuring began.
    /// <paramref name="now"/> decides how much of today has elapsed, for the partial mark.
    /// </summary>
    public DayTraffic? Day(DateOnly day, SourceKind? source, DateOnly today, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (NotRecorded(day))
        {
            return null;
        }

        _rows.TryGetValue(day, out var keyed);
        var own = TrafficBytes.Zero;
        var tools = TrafficBytes.Zero;
        var bySource = new Dictionary<SourceKind, TrafficBytes>();
        long sampled = 0;
        TrafficDay? machine = null;
        foreach (var row in keyed?.Values ?? Enumerable.Empty<TrafficDay>())
        {
            if (row.IsMachine)
            {
                machine = row;
                continue;
            }

            if (row.Source is not { } kind || (source is { } only && only != kind))
            {
                continue;
            }

            own += new TrafficBytes(Math.Max(0, row.Rx), Math.Max(0, row.Tx));
            tools += new TrafficBytes(Math.Max(0, row.RxTools), Math.Max(0, row.TxTools));
            var shown = row.Total(IncludeTools);
            if (!shown.IsZero)
            {
                bySource[kind] = bySource.GetValueOrDefault(kind) + shown;
            }

            sampled = Math.Max(sampled, row.SampledMs);
        }

        var recorded = machine?.SampledMs ?? sampled;
        var partial = recorded < ElapsedMs(day, today, now, zone ?? TimeZoneInfo.Local) - PartialSlackMs;
        TrafficBytes? machineBytes = source is null
            ? machine is { } m ? new TrafficBytes(Math.Max(0, m.Rx), Math.Max(0, m.Tx)) : TrafficBytes.Zero
            : null;
        return new DayTraffic(own, tools, bySource, machineBytes, Math.Max(0, recorded), partial, IncludeTools);
    }

    /// <summary>
    /// Days with agent traffic of <paramref name="source"/> (every source when null) as displayed, tools per the setting,
    /// oldest first. This computer's own total does not count (DESIGN 10, Traffic without token usage).
    /// </summary>
    public IReadOnlyList<DateOnly> AgentDays(SourceKind? source)
    {
        var days = new List<DateOnly>();
        foreach (var (day, keyed) in _rows)
        {
            foreach (var row in keyed.Values)
            {
                if (row.Source is { } kind && (source is null || source == kind) && !row.Total(IncludeTools).IsZero)
                {
                    days.Add(day);
                    break;
                }
            }
        }

        days.Sort();
        return days;
    }

    /// <summary>Sources with agent traffic on any stored day, in <see cref="SourceKind"/> order.</summary>
    public IReadOnlySet<SourceKind> SourcesWithTraffic()
    {
        var found = new HashSet<SourceKind>();
        foreach (var keyed in _rows.Values)
        {
            foreach (var row in keyed.Values)
            {
                if (row.Source is { } kind && !row.Total(IncludeTools).IsZero)
                {
                    found.Add(kind);
                }
            }
        }

        return found;
    }

    /// <summary>Every day of <paramref name="range"/> with its traffic (days before measuring began left out).</summary>
    public IReadOnlyDictionary<DateOnly, DayTraffic> Days(DayRange range, SourceKind? source, DateOnly today, DateTimeOffset now,
        TimeZoneInfo? zone = null)
    {
        var days = new Dictionary<DateOnly, DayTraffic>();
        for (var i = 0; i < range.DayCount; i++)
        {
            var day = range.DayAt(i);
            if (Day(day, source, today, now, zone) is { } traffic)
            {
                days[day] = traffic;
            }
        }

        return days;
    }

    private static long ElapsedMs(DateOnly day, DateOnly today, DateTimeOffset now, TimeZoneInfo zone)
    {
        var start = Midnight(day, zone);
        var end = day >= today ? now : Midnight(day.AddDays(1), zone);
        return (long)Math.Max(0, (end - start).TotalMilliseconds);
    }

    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
