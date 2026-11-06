using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.History;

/// <summary>Observed network traffic in the window (DESIGN 10, Usage history): the third metric, its column and footer.</summary>
internal sealed partial class HistoryViewModel
{
    private static readonly IReadOnlyDictionary<DateOnly, DayTraffic> NoTrafficDays = new Dictionary<DateOnly, DayTraffic>();

    private TrafficHub? _trafficHub;
    private Func<bool> _includeTools = static () => true;
    private Func<SourceSet> _enabledSources = static () => SourceSet.Everything;
    private HistoryTraffic _traffic = HistoryTraffic.None;
    private (IReadOnlyList<TrafficDay> Days, string? Since, bool IncludeTools, SourceSet Enabled)? _trafficKey;
    private string _trafficTotal = string.Empty;
    private string _trafficSinceText = string.Empty;

    /// <summary>The stored traffic days as the window counts them.</summary>
    public HistoryTraffic Traffic => _traffic;

    /// <summary>Each day of the range with its traffic (days before measuring began left out).</summary>
    public IReadOnlyDictionary<DateOnly, DayTraffic> TrafficDays { get; private set; } = NoTrafficDays;

    /// <summary>Traffic was ever measured: the table shows its column and the CSV its bytes; a remote host has none (REMOTE 7).</summary>
    public bool ShowsTraffic => _hosts.HasTraffic && _traffic.IsMeasured;

    /// <summary>The range's displayed traffic in the chart header with the traffic metric (<c>traffic.pair</c>).</summary>
    public string TrafficTotal
    {
        get => _trafficTotal;
        private set
        {
            if (Set(ref _trafficTotal, value))
            {
                OnPropertyChanged(nameof(HasTrafficTotal));
            }
        }
    }

    public bool HasTrafficTotal => !string.IsNullOrEmpty(_trafficTotal);

    /// <summary><c>history.traffic.since</c> when the range starts before measuring began, else empty.</summary>
    public string TrafficSinceText
    {
        get => _trafficSinceText;
        private set
        {
            if (Set(ref _trafficSinceText, value))
            {
                OnPropertyChanged(nameof(HasTrafficSince));
            }
        }
    }

    public bool HasTrafficSince => !string.IsNullOrEmpty(_trafficSinceText);

    public DayTraffic? TrafficAt(DateOnly day) => TrafficDays.GetValueOrDefault(day);

    /// <summary>The include-tools setting or the enabled sources changed: the shown bytes change with them.</summary>
    public void RefreshTraffic()
    {
        if (!_disposed)
        {
            ScheduleRecompute();
        }
    }

    private void AttachTraffic(TrafficHub? hub, Func<bool>? includeTools, Func<SourceSet>? enabledSources)
    {
        _trafficHub = hub;
        _includeTools = includeTools ?? (static () => true);
        _enabledSources = enabledSources ?? (static () => SourceSet.Everything);
        if (hub is not null)
        {
            hub.DaysChanged += OnTrafficDaysChanged;
        }
    }

    private void DetachTraffic()
    {
        if (_trafficHub is not null)
        {
            _trafficHub.DaysChanged -= OnTrafficDaysChanged;
        }
    }

    private void OnTrafficDaysChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            ScheduleRecompute();
        }
    }

    /// <summary>
    /// Rebuilt only when the hub published new days, the include-tools setting or the enabled sources changed; disabled
    /// sources' rows are left out (METRICS 10.5, DESIGN 10), this computer's rows stay.
    /// </summary>
    private HistoryTraffic CurrentTraffic()
    {
        if (_trafficHub is not { } hub || !_hosts.HasTraffic)
        {
            return HistoryTraffic.None;
        }

        var key = (hub.Days, hub.Since, IncludeTools: _includeTools(), Enabled: _enabledSources());
        if (_trafficKey is not { } last || !ReferenceEquals(last.Days, key.Days) || last.Since != key.Since || last.IncludeTools != key.IncludeTools
            || last.Enabled != key.Enabled)
        {
            _trafficKey = key;
            _traffic = new HistoryTraffic(TrafficVisibility.Days(key.Days, key.Enabled), key.Since, key.IncludeTools);
        }

        return _traffic;
    }

    /// <summary>Gives every day its traffic, refreshes the traffic texts, and returns the days.</summary>
    private IReadOnlyList<DaySummary> ApplyTraffic(IReadOnlyList<DaySummary> days)
    {
        var traffic = CurrentTraffic();
        TrafficDays = traffic.IsMeasured ? traffic.Days(Range, _source, Today, DateTimeOffset.Now) : NoTrafficDays;
        var withTraffic = TrafficDays.Count == 0 ? days : days.Select(d => d with { Traffic = TrafficDays.GetValueOrDefault(d.Day) }).ToArray();
        var total = TrafficDays.Values.Aggregate(TrafficBytes.Zero, (sum, day) => sum + day.Shown);
        TrafficTotal = _metric == HistoryChartMetric.Traffic && TrafficDays.Count > 0 ? HistoryTrafficText.Pair(total, _loc) : string.Empty;
        TrafficSinceText = traffic.Since is { } since && since > Range.Start && since <= Range.End
            ? _loc.Format("history.traffic.since", HistoryText.ShortDate(since, Today, _loc))
            : string.Empty;
        return withTraffic;
    }
}
