using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Unpriced;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Theme;

namespace AgentSpeed.History;

/// <summary>Why the window's data changed: a new filter or range animates; live data and texts update in place.</summary>
internal enum HistoryUpdate
{
    Filter,
    Live,
    Sort,
    Text,
}

/// <summary>
/// State of the Usage History window (DESIGN 4b, DESIGN 9): the filter, the range, the chart metric, how subscriptions
/// count, and everything derived from the hub's days in the display currency. Live updates from the hub (and changes of
/// currency, rates or subscription marks) are coalesced to at most four recomputations per second; today shown on its
/// own is re-read quietly every 30 seconds while the window is on screen.
/// </summary>
internal sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CoalesceInterval = TimeSpan.FromMilliseconds(250);

    private readonly HistoryHub _hub;
    private readonly CostDisplay _cost;
    private readonly Action<bool>? _includeChanged;
    private readonly DispatcherTimer _pending;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _messageTimer;
    private readonly DispatcherTimer _liveDay;
    private Localizer _loc;
    private long _lastRecompute;
    private SourceKind? _source;
    private HistoryRangeKind _rangeKind = HistoryRangeKind.Days30;
    private DayRange? _custom;
    private HistoryColumn _sortColumn = HistoryColumn.Date;
    private bool _sortDescending = true;
    private bool _syncingSources;
    private SourceOption? _selectedSource;
    private string _subtitle = string.Empty;
    private bool _isEmpty = true;
    private bool _isAnalyzing;
    private bool _isFailed;
    private double _progress;
    private string _stateText = string.Empty;
    private string _filesText = string.Empty;
    private string _message = string.Empty;
    private IReadOnlyList<LegendItem> _legend = [];
    private bool _isSingleDay;
    private bool _isInProgress;
    private string _chartTitle = string.Empty;
    private bool _hasBreakdown;
    private HistoryChartMetric _metric = HistoryChartMetric.Output;
    private bool _includeSubscriptions;
    private bool _offersSubscriptions;
    private string _breakdownTitle = string.Empty;
    private string _chartName = string.Empty;
    private bool _showsSpeedKey = true;
    private readonly Func<PriceTable> _currentTable;
    private readonly UnpricedApp _app;
    private UnpricedView _unpriced = UnpricedView.Empty;
    private (PriceTable Table, SourceSet Excluded) _unpricedBasis = (PriceBook.Bundled, default);
    private IReadOnlyList<UnpricedGroupItem> _unpricedGroups = [];
    private string _unpricedCount = string.Empty;
    private string _unpricedPricedNow = string.Empty;
    private bool _hasUnpriced;
    private string _unpricedText = string.Empty;
    private string _rateNote = string.Empty;
    private (string Currency, decimal Rate, long RateDate, SourceSet Marked, string Language) _costKey;
    private bool _shown;
    private bool _hasTokenUsage = true;
    private bool _hasAgentTraffic;
    private DateTimeOffset? _quietSince;
    private bool _disposed;

    /// <param name="includeChanged">Persists the window's own include-subscriptions choice.</param>
    /// <param name="traffic">Observed traffic by day (METRICS 10.9); null: the window has no traffic.</param>
    /// <param name="includeTools">The include-tools setting (DESIGN 10), read at every recomputation.</param>
    /// <param name="enabledSources">The sources turned on in Settings → Data Sources; others show no traffic (DESIGN 10).</param>
    /// <param name="currentTable">The current price table, which gives unpriced requests their reasons (METRICS 9.10).</param>
    /// <param name="appVersion">This build's version, for the copied diagnostics.</param>
    public HistoryViewModel(HistoryHub hub, Localizer loc, CostDisplay cost, Dispatcher dispatcher, Action<bool>? includeChanged = null,
        Core.Traffic.TrafficHub? traffic = null, Func<bool>? includeTools = null, Func<SourceSet>? enabledSources = null,
        Func<PriceTable>? currentTable = null, string? appVersion = null)
    {
        _hub = hub;
        _currentTable = currentTable ?? (() => PriceBook.Bundled);
        _app = UnpricedApp.Windows(appVersion ?? "0.0.0");
        _loc = loc;
        _cost = cost;
        _includeChanged = includeChanged;
        Money = new HistoryMoney(cost);
        _includeSubscriptions = cost.Settings.HistoryIncludeSubscriptions ?? cost.Settings.IncludeSubscriptions;
        _costKey = CostKey();
        _pending = new DispatcherTimer(CoalesceInterval, DispatcherPriority.Background, (_, _) =>
        {
            _pending!.Stop();
            Recompute(HistoryUpdate.Live);
        }, dispatcher);
        _pending.Stop();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => OnClock(), dispatcher);
        _messageTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) =>
        {
            _messageTimer!.Stop();
            Message = string.Empty;
        }, dispatcher);
        _messageTimer.Stop();
        _liveDay = new DispatcherTimer(LiveDay.RefreshInterval, DispatcherPriority.Background, (_, _) => RefreshLiveDay(), dispatcher);
        _liveDay.Stop();
        Cards = Enum.GetValues<SummaryMetric>().Select(m => new SummaryCardView(m)).ToArray();
        RetryCommand = new RelayCommand(() =>
        {
            _quietSince = null;
            _hub.Refresh();
        });
        _hub.Changed += OnHubChanged;
        _cost.Changed += OnCostChanged;
        AttachTraffic(traffic, includeTools, enabledSources);
        Recompute(HistoryUpdate.Filter);
    }

    /// <summary>After every recomputation, on the UI thread.</summary>
    public event EventHandler<HistoryUpdate>? Updated;

    public Localizer Loc => _loc;

    public DateOnly Today { get; private set; }

    public SourceKind? Source => _source;

    public HistoryRangeKind RangeKind => _rangeKind;

    public DayRange Range { get; private set; }

    /// <summary>First and last day with data for the current filter (all sources when the filter has none).</summary>
    public DayRange? DataBounds { get; private set; }

    /// <summary>Days with data in the range, oldest first.</summary>
    public IReadOnlyList<DaySummary> Days { get; private set; } = [];

    /// <summary>The table rows: <see cref="Days"/> in the chosen sort order.</summary>
    public IReadOnlyList<DaySummary> Rows { get; private set; } = [];

    public HistoryBars Bars { get; private set; } = HistoryBars.Empty(DayRange.Single(DateOnly.FromDateTime(DateTime.Now)));

    /// <summary>The display currency and its rate in use (METRICS 9.8).</summary>
    public HistoryMoney Money { get; }

    public HistoryColumn SortColumn => _sortColumn;

    public bool SortDescending => _sortDescending;

    public ObservableCollection<SourceOption> SourceOptions { get; } = new();

    public IReadOnlyList<SummaryCardView> Cards { get; }

    public RelayCommand RetryCommand { get; }

    public SourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value is null || _syncingSources || ReferenceEquals(value, _selectedSource))
            {
                return;
            }

            _selectedSource = value;
            OnPropertyChanged();
            if (value.Kind != _source)
            {
                _source = value.Kind;
                Recompute(HistoryUpdate.Filter);
                FollowTraffic();
            }
        }
    }

    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    public IReadOnlyList<LegendItem> Legend { get => _legend; private set => Set(ref _legend, value); }

    /// <summary>The bars of a single day by source (all sources) or by model (one source), largest first.</summary>
    public ObservableCollection<BreakdownRowView> Breakdown { get; } = new();

    /// <summary>One day is shown: the breakdown replaces the daily chart.</summary>
    public bool IsSingleDay { get => _isSingleDay; private set => Set(ref _isSingleDay, value); }

    /// <summary>The single day shown is today, whose figures are still growing.</summary>
    public bool IsInProgress { get => _isInProgress; private set => Set(ref _isInProgress, value); }

    /// <summary><c>每日輸出</c>, or <c>來源分布</c> / <c>模型分布</c> for one day.</summary>
    public string ChartTitle { get => _chartTitle; private set => Set(ref _chartTitle, value); }

    /// <summary>The single day has bars to show (a day with input but no output has none).</summary>
    public bool HasBreakdown { get => _hasBreakdown; private set => Set(ref _hasBreakdown, value); }

    /// <summary>What the chart and the breakdown measure (DESIGN 9: <c>history.chart.output</c> | <c>history.chart.cost</c>).</summary>
    public HistoryChartMetric Metric => _metric;

    /// <summary><c>來源分布</c> / <c>模型分布</c> beside the metric control for one day; empty for several days.</summary>
    public string BreakdownTitle { get => _breakdownTitle; private set => Set(ref _breakdownTitle, value); }

    /// <summary>The chart's accessible name: the metric it draws.</summary>
    public string ChartName { get => _chartName; private set => Set(ref _chartName, value); }

    /// <summary>The speed line's key: only for the daily output chart (the cost chart has no speed line).</summary>
    public bool ShowsSpeedKey { get => _showsSpeedKey; private set => Set(ref _showsSpeedKey, value); }

    /// <summary>At least one source is marked as a subscription: the toolbar offers <c>history.includeSubscriptions</c>.</summary>
    public bool OffersSubscriptions { get => _offersSubscriptions; private set => Set(ref _offersSubscriptions, value); }

    /// <summary>Subscription sources count at their API-equivalent cost in this window; the window remembers the choice.</summary>
    public bool IncludeSubscriptions
    {
        get => _includeSubscriptions;
        set
        {
            if (!Set(ref _includeSubscriptions, value))
            {
                return;
            }

            Recompute(HistoryUpdate.Filter);
            _includeChanged?.Invoke(value);
        }
    }

    /// <summary>The unknown-prices popover's groups (DESIGN 9): sources with requests that still have a reason.</summary>
    public IReadOnlyList<UnpricedGroupItem> UnpricedGroups { get => _unpricedGroups; private set => Set(ref _unpricedGroups, value); }

    /// <summary>The popover header's count: Σ unpriced requests of the view.</summary>
    public string UnpricedCount { get => _unpricedCount; private set => Set(ref _unpricedCount, value); }

    /// <summary><c>history.unpricedModels.pricedNow</c> when the current table prices some of them, else empty.</summary>
    public string UnpricedPricedNow
    {
        get => _unpricedPricedNow;
        private set
        {
            if (Set(ref _unpricedPricedNow, value))
            {
                OnPropertyChanged(nameof(HasUnpricedPricedNow));
            }
        }
    }

    public bool HasUnpricedPricedNow => _unpricedPricedNow.Length > 0;

    /// <summary>The range has requests without a known price (METRICS 9.10 Σ <c>unpriced</c> of the view).</summary>
    public bool HasUnpriced { get => _hasUnpriced; private set => Set(ref _hasUnpriced, value); }

    /// <summary><c>1,234 筆請求的模型價格未知，未計入費用</c>.</summary>
    public string UnpricedText { get => _unpricedText; private set => Set(ref _unpricedText, value); }

    /// <summary><c>以 2026/10/22 的匯率換算</c> for a non-USD currency, else empty.</summary>
    public string RateNote
    {
        get => _rateNote;
        private set
        {
            if (Set(ref _rateNote, value))
            {
                OnPropertyChanged(nameof(HasRateNote));
            }
        }
    }

    public bool HasRateNote => !string.IsNullOrEmpty(_rateNote);

    public bool IsAnalyzing { get => _isAnalyzing; private set => Set(ref _isAnalyzing, value); }

    public bool IsFailed { get => _isFailed; private set => Set(ref _isFailed, value); }

    /// <summary>Analysis progress, 0..1.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    /// <summary>Analyzing / updated / failed line of the footer; empty before the first analysis.</summary>
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }

    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }

    /// <summary>A short-lived notice (export failure) shown in the footer instead of the state.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    /// <summary>The window is on screen (visible and not minimized); only then is today re-read.</summary>
    public void SetShown(bool shown)
    {
        if (_disposed || shown == _shown)
        {
            return;
        }

        _shown = shown;
        UpdateLiveDayTimer();
        RefreshLiveDay();
    }

    /// <summary>Re-reads today's logs quietly when today is shown on its own and the window is on screen.</summary>
    public void RefreshLiveDay()
    {
        if (_disposed || !_shown || !LiveDay.Shows(Range, Today) || _hub.RefreshHandler is null || !LiveDay.CanRefreshQuietly(_hub.State))
        {
            return;
        }

        _quietSince = DateTimeOffset.Now;
        _hub.Refresh();
    }

    /// <summary>Also when the source is already shown: opening the window applies the traffic rule again.</summary>
    public void SetSource(SourceKind? source)
    {
        if (source != _source || _selectedSource is null)
        {
            _source = source;
            Recompute(HistoryUpdate.Filter);
        }

        FollowTraffic();
    }

    public void SetRange(HistoryRangeKind kind)
    {
        if (kind == HistoryRangeKind.Custom && _custom is null)
        {
            return;
        }

        if (kind == _rangeKind)
        {
            return;
        }

        _rangeKind = kind;
        Recompute(HistoryUpdate.Filter);
        FollowTraffic();
        RefreshLiveDay();
    }

    public void ApplyCustom(DateOnly from, DateOnly to)
    {
        var bounds = DataBounds ?? DayRange.Single(Today);
        _custom = HistoryRanges.Clamp(from, to, bounds.Start < Today ? bounds.Start : Today, Today);
        _rangeKind = HistoryRangeKind.Custom;
        Recompute(HistoryUpdate.Filter);
        FollowTraffic();
        RefreshLiveDay();
    }

    public void SetMetric(HistoryChartMetric metric)
    {
        if (metric == _metric)
        {
            return;
        }

        _metric = metric;
        Recompute(HistoryUpdate.Filter);
    }

    public void SortBy(HistoryColumn column)
    {
        if (column == _sortColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = HistoryTable.DefaultDescending(column);
        }

        Rows = HistoryTable.Sort(Days, _sortColumn, _sortDescending);
        Updated?.Invoke(this, HistoryUpdate.Sort);
    }

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        RelocalizePerformance();
        Recompute(HistoryUpdate.Text);
    }

    /// <summary>The rows as they are shown, as CSV bytes for Excel, with a suggested file name.</summary>
    public (string FileName, byte[] Bytes) ExportCsv() =>
        (HistoryCsv.FileName(Range, _source), HistoryCsv.Encode(HistoryCsv.Build(Rows, _source, Money, ShowsTraffic)));

    public (string FileName, byte[] Bytes) ExportModesCsv() =>
        (ExecutionModeCsv.FileName(Range, _source), HistoryCsv.Encode(ExecutionModeCsv.Build(Rows)));

    public void ShowMessage(string text)
    {
        Message = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    /// <summary>The day under index <paramref name="index"/> of the range, when it has data.</summary>
    public DaySummary? DayAt(int index)
    {
        if (index < 0 || index >= Range.DayCount)
        {
            return null;
        }

        var day = Range.DayAt(index);
        var lo = 0;
        var hi = Days.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var cmp = Days[mid].Day.CompareTo(day);
            if (cmp == 0)
            {
                return Days[mid];
            }

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.Changed -= OnHubChanged;
        _cost.Changed -= OnCostChanged;
        DetachTraffic();
        _pending.Stop();
        _clock.Stop();
        _messageTimer.Stop();
        _liveDay.Stop();
        DisposePerformance();
    }

    private void OnHubChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            ScheduleRecompute();
        }
    }

    /// <summary>Only what the window shows counts: the currency, its rate and date, the subscription marks and the language.</summary>
    private void OnCostChanged(object? sender, EventArgs e)
    {
        var key = CostKey();
        if (_disposed || key == _costKey)
        {
            return;
        }

        _costKey = key;
        ScheduleRecompute();
    }

    private (string, decimal, long, SourceSet, string) CostKey() =>
        (_cost.Currency.Code, _cost.Rate, _cost.Rates.DateMs, _cost.Settings.Subscriptions, _cost.Language);

    private void ScheduleRecompute()
    {
        if (Environment.TickCount64 - _lastRecompute >= CoalesceInterval.TotalMilliseconds)
        {
            _pending.Stop();
            Recompute(HistoryUpdate.Live);
        }
        else if (!_pending.IsEnabled)
        {
            _pending.Start();
        }
    }

    private void OnClock()
    {
        if (DateOnly.FromDateTime(DateTime.Now) != Today)
        {
            Recompute(HistoryUpdate.Live);
        }
        else
        {
            UpdateState();
        }
    }

    private void Recompute(HistoryUpdate kind)
    {
        _lastRecompute = Environment.TickCount64;
        Today = DateOnly.FromDateTime(DateTime.Now);
        var usage = _hub.Days;
        var options = new HistoryCostOptions(_cost.Settings.Subscriptions, _includeSubscriptions, _metric);
        var excluded = options.Excluded(_source);
        // Recorded agent traffic is history too (DESIGN 10): its days count for the bounds, the rows and the picker.
        var agentDays = CurrentTraffic().AgentDays(_source);
        DataBounds = Union(HistoryAggregator.Bounds(usage, _source), agentDays.Count > 0 ? new DayRange(agentDays[0], agentDays[^1]) : null)
            ?? HistoryAggregator.Bounds(usage);
        Range = HistoryRanges.Resolve(_rangeKind, Today, DataBounds?.Start, _custom);
        var tokenDays = WithoutSourcePeaks(HistoryAggregator.Daily(usage, _source, Range, excluded));
        _hasTokenUsage = tokenDays.Count > 0;
        _hasAgentTraffic = agentDays.Any(Range.Contains);
        Days = ApplyTraffic(WithTrafficOnlyDays(tokenDays, agentDays));
        var previous = HistoryAggregator.Daily(usage, _source, HistoryRanges.Previous(Range), excluded);
        Bars = _metric == HistoryChartMetric.Traffic
            ? HistoryBars.Traffic(Days, Range, _source, TrafficDays, _traffic.Since)
            : HistoryBars.From(Days, Range, _source, _metric, Money);
        Rows = HistoryTable.Sort(Days, _sortColumn, _sortDescending);
        IsEmpty = Days.Count == 0;
        Subtitle = HistoryText.RangeSubtitle(Range, Today, _loc);
        IsSingleDay = Range.IsSingleDay;
        IsInProgress = Range.IsSingleDay && Range.Start == Today;
        OffersSubscriptions = options.OffersSubscriptionToggle;
        ShowsSpeedKey = !Range.IsSingleDay && _metric == HistoryChartMetric.Output;
        ChartName = _loc[HistoryChartMetrics.TitleKey(_metric)];
        UpdateBreakdown(DayBreakdown.For(usage, Range, _source, _metric, excluded, TrafficAt(Range.Start)), rebuild: kind == HistoryUpdate.Filter);
        UpdateModes(rebuild: false);

        var sparkBrush = _source is { } one ? HeatPalette.SourceColor(one).ToBrush() : null;
        var sparkDays = Range.IsSingleDay ? HistoryAggregator.Daily(usage, _source, HistoryRanges.SparkRange(Range), excluded) : null;
        var cards = HistorySummary.Build(Days, previous, Range, Today, _loc, Money, options.CostLabelKey(_source), sparkDays);
        for (var i = 0; i < Cards.Count && i < cards.Count; i++)
        {
            var card = cards[i];
            var value = card.Spoken ?? (string.IsNullOrEmpty(card.Unit) ? card.Value : $"{card.Value} {card.Unit}");
            var accessible = HistoryText.JoinParts([HistoryText.Labelled(card.Label, value, _loc), card.Secondary], _loc);
            var tooltip = HistoryText.Labelled(card.Label, card.FullValue, _loc);
            Cards[i].Apply(card, sparkBrush, card.Note is { } note ? tooltip + Environment.NewLine + Environment.NewLine + note : tooltip,
                accessible);
        }

        UpdateUnpriced(usage, excluded);
        RateNote = _cost.HistoryNote ?? string.Empty;

        Legend = Bars.Order.Count == 0 && _source is { } empty
            ? [new LegendItem(_loc[DisplayText.SourceKey(empty)], HeatPalette.SourceColor(empty).ToBrush())]
            : Bars.Order.Select(s => new LegendItem(_loc[DisplayText.SourceKey(s)], HeatPalette.SourceColor(s).ToBrush())).ToArray();
        UpdateSources(usage);
        UpdateState();
        UpdatePerformance();
        UpdateLiveDayTimer();
        if (!_clock.IsEnabled)
        {
            _clock.Start();
        }

        Updated?.Invoke(this, kind);
    }

    /// <summary>
    /// Updates the breakdown bars in place (matched by source or model) so they grow to new lengths; a new filter or range
    /// rebuilds them so they grow from nothing.
    /// </summary>
    private void UpdateBreakdown(DayBreakdown? breakdown, bool rebuild)
    {
        ChartTitle = _loc[breakdown?.TitleKey ?? HistoryChartMetrics.TitleKey(_metric)];
        BreakdownTitle = breakdown is null ? string.Empty : _loc[breakdown.TitleKey];
        HasBreakdown = breakdown is { Rows.Count: > 0 };
        if (rebuild || breakdown is null)
        {
            Breakdown.Clear();
        }

        if (breakdown is null)
        {
            return;
        }

        var existing = Breakdown.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var inProgress = breakdown.Day == Today;
        for (var i = 0; i < breakdown.Rows.Count; i++)
        {
            var row = breakdown.Rows[i];
            if (!existing.Remove(row.Key, out var view))
            {
                view = new BreakdownRowView(row.Key);
                Breakdown.Insert(Math.Min(i, Breakdown.Count), view);
            }
            else if (Breakdown.IndexOf(view) is var at && at != i)
            {
                Breakdown.Move(at, i);
            }

            view.Apply(row, breakdown.Kind, inProgress, _loc, Money, _metric == HistoryChartMetric.Traffic);
        }

        foreach (var stale in existing.Values)
        {
            Breakdown.Remove(stale);
        }
    }

    /// <summary>
    /// The copied diagnostics (METRICS 9.10 <i>Text</i>) of the popover's view: the same range, source, subscription
    /// choice and table. Model ids and counts only; path-like values are redacted.
    /// </summary>
    public string UnpricedDiagnostics() =>
        UnpricedPopover.CopyText(_unpriced, _app, _unpricedBasis.Table, _rangeKind, Range, _source, _unpricedBasis.Excluded);

    /// <summary>
    /// The footer's unknown-price notice and its popover (DESIGN 9): every stored day of the range, today included, read
    /// with the reasons of the current table, so requests a newer table prices move to <c>pricedNow</c>.
    /// </summary>
    private void UpdateUnpriced(IReadOnlyList<DailyUsage> usage, SourceSet excluded)
    {
        var table = _currentTable();
        _unpriced = UnpricedPopover.ViewOf(usage, Range, _source, excluded, table);
        _unpricedBasis = (table, excluded);
        var popover = UnpricedPopover.Build(_unpriced, _loc);
        UnpricedText = _unpriced.Unpriced > 0 ? _loc.Format("history.unpricedNotice", _unpriced.Unpriced) : string.Empty;
        UnpricedCount = popover.Count;
        UnpricedPricedNow = popover.PricedNow;
        var groups = popover.Groups.Select((g, i) => new UnpricedGroupItem(g, i == 0)).ToArray();
        if (!groups.SequenceEqual(_unpricedGroups, UnpricedGroupComparer.Instance))
        {
            UnpricedGroups = groups;
        }

        HasUnpriced = _unpriced.Unpriced > 0;
    }

    /// <summary>
    /// DESIGN 10, Opening on traffic: a range with agent traffic but no token usage would chart nothing as output or cost,
    /// so the chart switches to traffic; it never switches back by itself.
    /// </summary>
    private void FollowTraffic()
    {
        if (_metric != HistoryChartMetric.Traffic && _trafficHub is not null && !_hasTokenUsage && _hasAgentTraffic)
        {
            SetMetric(HistoryChartMetric.Traffic);
        }
    }

    /// <summary>The token days plus the range's days with agent traffic but no token usage, oldest first.</summary>
    private IReadOnlyList<DaySummary> WithTrafficOnlyDays(IReadOnlyList<DaySummary> tokenDays, IReadOnlyList<DateOnly> agentDays)
    {
        var known = tokenDays.Select(d => d.Day).ToHashSet();
        var extra = agentDays.Where(d => Range.Contains(d) && !known.Contains(d)).Select(DaySummary.TrafficOnly).ToArray();
        return extra.Length == 0 ? tokenDays : tokenDays.Concat(extra).OrderBy(d => d.Day).ToArray();
    }

    private static DayRange? Union(DayRange? a, DayRange? b) => (a, b) switch
    {
        ({ } x, { } y) => new DayRange(x.Start < y.Start ? x.Start : y.Start, x.End > y.End ? x.End : y.End),
        _ => a ?? b,
    };

    /// <summary>A stored peak is the day's overall peak, so a single source never shows it (METRICS 8).</summary>
    private IReadOnlyList<DaySummary> WithoutSourcePeaks(IReadOnlyList<DaySummary> days) =>
        _source is null ? days : days.Select(d => d.Peak is null ? d : d with { Peak = null }).ToArray();

    private void UpdateSources(IReadOnlyList<DailyUsage> usage)
    {
        var found = CurrentTraffic().SourcesWithTraffic().Union(HistoryAggregator.SourcesWithHistory(usage)).ToHashSet();
        IReadOnlyList<SourceKind> withHistory = Enum.GetValues<SourceKind>().Where(found.Contains).ToArray();
        var kinds = new List<SourceKind?> { null };
        kinds.AddRange(Enum.GetValues<SourceKind>()
            .Where(k => withHistory.Contains(k) || k == _source)
            .Select(k => (SourceKind?)k));

        _syncingSources = true;
        try
        {
            if (!SourceOptions.Select(o => o.Kind).SequenceEqual(kinds))
            {
                SourceOptions.Clear();
                foreach (var kind in kinds)
                {
                    SourceOptions.Add(new SourceOption(kind));
                }
            }

            var allColors = (withHistory.Count > 0 ? withHistory : Enum.GetValues<SourceKind>())
                .Select(k => HeatPalette.SourceColor(k).ToColor())
                .ToArray();
            var marked = _cost.Settings.Subscriptions;
            var tag = _loc["cost.subscription.tag"];
            foreach (var option in SourceOptions)
            {
                option.Name = option.Kind is { } k ? _loc[DisplayText.SourceKey(k)] : _loc["history.allSources"];
                option.Colors = option.Kind is { } c ? [HeatPalette.SourceColor(c).ToColor()] : allColors;
                option.Tag = option.Kind is { } s && marked.Contains(s) ? tag : string.Empty;
                option.AccessibleName = option.HasTag ? HistoryText.JoinParts([option.Name, option.Tag], _loc) : option.Name;
            }
        }
        finally
        {
            _syncingSources = false;
        }

        var selected = SourceOptions.FirstOrDefault(o => o.Kind == _source) ?? SourceOptions[0];
        if (!ReferenceEquals(selected, _selectedSource))
        {
            _selectedSource = selected;
            OnPropertyChanged(nameof(SelectedSource));
        }
    }

    private void UpdateLiveDayTimer()
    {
        var run = _shown && !_disposed && LiveDay.Shows(Range, Today);
        if (run != _liveDay.IsEnabled)
        {
            _liveDay.IsEnabled = run;
        }
    }

    /// <summary>The footer line; a quiet refresh of today keeps the last line instead of showing its progress.</summary>
    private void UpdateState()
    {
        var state = _hub.State;
        if (_quietSince is { } since && LiveDay.EndsQuiet(state, since, DateTimeOffset.Now))
        {
            _quietSince = null;
        }

        if (LiveDay.HidesState(state, _quietSince is not null))
        {
            return;
        }

        IsAnalyzing = state.Kind == HistoryStateKind.Analyzing;
        IsFailed = state.Kind == HistoryStateKind.Failed;
        Progress = Math.Clamp(state.Progress, 0, 1);
        StateText = state.Kind switch
        {
            HistoryStateKind.Analyzing => HistoryText.Analyzing(state.Progress, _loc),
            HistoryStateKind.Ready => HistoryText.Updated(state.UpdatedAt ?? DateTimeOffset.Now, DateTimeOffset.Now, _loc),
            HistoryStateKind.Failed => _loc["win.history.failed"],
            _ => string.Empty,
        };
        FilesText = state.Kind == HistoryStateKind.Analyzing && state.FilesTotal > 0
            ? HistoryText.AnalyzingFiles(state.FilesDone, state.FilesTotal, _loc)
            : string.Empty;
    }
}
