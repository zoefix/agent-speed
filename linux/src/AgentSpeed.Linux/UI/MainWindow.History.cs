using System.Globalization;
using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AgentSpeed.Linux.UI;

internal sealed partial class MainWindow
{
    private HistoryRangeKind _historyRangeKind = HistoryRangeKind.Days30;
    private bool _updatingHistoryDates;
    private readonly Grid _historySummaryGrid = new() { ColumnSpacing = 9, RowSpacing = 9 };
    private readonly List<(Border Root, TextBlock Label, TextBlock Value, TextBlock Note, LineChart Spark)> _summaryViews = [];
    private readonly WrapPanel _historyLegend = new() { ItemSpacing = 12, LineSpacing = 6 };
    private readonly Dictionary<DateOnly, ThemeScope> _historyRowScopes = [];
    private readonly Dictionary<HistoryRangeKind, DesktopButton> _rangeButtons = [];
    private ThemeScope? _historyLegendScope;
    private readonly ProgressBar _historyProgress = new() { Height = 3, Minimum = 0, Maximum = 1 };
    private string? _historyFailure;
    private TextBlock _historyPrivacy = null!;

    private void AddHistoryButton(WrapPanel row, string english, Action action)
    {
        var button = Button(T(english), action);
        _historyButtons.Add((button, english));
        row.Children.Add(button);
    }

    private void HistoryChanged(object? sender, EventArgs e)
    {
        // The history worker publishes partial results. Do not rebuild an invisible page for every publication.
        if (IsVisible && _page == "history") RefreshHistory();
    }

    private void BuildHistory()
    {
        _historySource.ItemsSource = new[] { T("All sources") }
            .Concat(Enum.GetValues<SourceKind>().Select(k => L(DisplayText.SourceKey(k)))).ToArray();
        _historySource.SelectedIndex = 0;
        _historySource.SelectionChanged += (_, _) =>
        {
            if (_updatingHistorySources) return;
            _selectedSource = _historySource.SelectedIndex > 0 ? (SourceKind)(_historySource.SelectedIndex - 1) : null;
            RefreshHistory();
        };
        _from.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty && !_updatingHistoryDates) _historyRangeKind = HistoryRangeKind.Custom; };
        _to.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty && !_updatingHistoryDates) _historyRangeKind = HistoryRangeKind.Custom; };
        _from.KeyDown += (_, e) => { if (e.Key == Key.Enter) { RefreshHistory(); e.Handled = true; } };
        _to.KeyDown += (_, e) => { if (e.Key == Key.Enter) { RefreshHistory(); e.Handled = true; } };
        StyledInput(_from); StyledInput(_to);

        _historyTitle = ThemedText(L("history.title"), 26);
        _historyTitle.FontWeight = FontWeight.SemiBold;
        _historySubtitle = ThemedText("", 12, TextRole.Secondary);
        var toolbar = Row(_historySource, _from, _to, HistoryMetricPicker());
        var filters = Row();
        foreach (var kind in HistoryRanges.Presets)
        {
            var button = DesktopButton(L(HistoryRanges.LabelKey(kind)), () => SelectHistoryRange(kind), DesktopButtonKind.Tool);
            _rangeButtons.Add(kind, button);
            filters.Children.Add(button);
        }
        var actions = Row();
        AddHistoryButton(actions, "Apply dates", RefreshHistory);
        AddHistoryButton(actions, "CSV", () => _ = Export(false));
        AddHistoryButton(actions, "Modes CSV", () => _ = Export(true));
        AddHistoryButton(actions, "Refresh", _app.History.Refresh);
        _historyStatus = ThemedText("", 12, TextRole.Secondary);
        _historyChartTitle = ThemedText("", 15);
        _historyChartTitle.FontWeight = FontWeight.SemiBold;
        _historyChartUnit = ThemedText("", 11, TextRole.Secondary);
        var chartHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        chartHeader.Children.Add(_historyChartTitle);
        Grid.SetColumn(_historyChartUnit, 1); chartHeader.Children.Add(_historyChartUnit);

        foreach (var _ in Enum.GetValues<SummaryMetric>())
        {
            var label = ThemedText("", 11.5, TextRole.Secondary);
            var value = ThemedText("", 23); value.FontWeight = FontWeight.SemiBold;
            value.TextWrapping = TextWrapping.NoWrap;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            var note = ThemedText("", 11, TextRole.Secondary);
            note.TextWrapping = TextWrapping.NoWrap;
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            var spark = new LineChart { Height = 30, Sparkline = true };
            var body = Stack(label, value, spark, note); body.Spacing = 6;
            var card = Inset(body, new Thickness(12));
            _summaryViews.Add((card, label, value, note, spark));
            _historySummaryGrid.Children.Add(card);
            card.SizeChanged += (_, _) => FitHistorySummaryValues();
            _theme.On(() =>
            {
                spark.LineBrush = _theme.Palette.AccentBrush;
                spark.GridBrush = Brushes.Transparent;
                spark.MutedBrush = Brushes.Transparent;
            });
        }
        _historySummaryGrid.SizeChanged += (_, _) => ArrangeHistorySummary();
        _historyPrivacy = ThemedText(L("history.privacy"), 11, TextRole.Muted);
        _historyPage = Stack(Stack(_historyTitle, _historySubtitle), Card(Stack(toolbar, filters, actions)),
            _historyStatus, _historyProgress, _historySummaryGrid,
            Card(Stack(chartHeader, _historyLegend, _historyChart)), _historyRows, _historyPrivacy);
        _historyPage.Margin = new Thickness(0, 0, 0, 28);
        _theme.On(ApplyChartTheme);
    }

    private void ArrangeHistorySummary()
    {
        var counts = HistoryLayout.CardRows(_summaryViews.Count, _historySummaryGrid.Bounds.Width);
        var columns = counts.Count > 0 ? counts.Max() : 1;
        if (_historySummaryGrid.ColumnDefinitions.Count != columns)
            _historySummaryGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", columns)));
        if (_historySummaryGrid.RowDefinitions.Count != counts.Count)
            _historySummaryGrid.RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", counts.Count)));
        var index = 0;
        for (var row = 0; row < counts.Count; row++)
            for (var col = 0; col < counts[row]; col++, index++)
            { Grid.SetRow(_summaryViews[index].Root, row); Grid.SetColumn(_summaryViews[index].Root, col); }
        FitHistorySummaryValues();
    }

    private void FitHistorySummaryValues()
    {
        foreach (var view in _summaryViews)
        {
            var available = view.Root.Bounds.Width - 26;
            if (available <= 0) continue;
            var measured = new FormattedText(view.Value.Text ?? "", _app.Loc.Culture, FlowDirection.LeftToRight,
                new Typeface(view.Value.FontFamily, FontStyle.Normal, FontWeight.SemiBold), 23, _theme.Palette.PrimaryBrush);
            view.Value.FontSize = Math.Clamp(23 * available / Math.Max(1, measured.Width), 14, 23);
        }
    }

    private ComboBox HistoryMetricPicker()
    {
        var combo = new ComboBox { ItemsSource = Enum.GetValues<HistoryChartMetric>(), SelectedItem = _historyMetric, MinWidth = 155 };
        combo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((value, _) => Text(Choice(value), 14));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is HistoryChartMetric metric) { _historyMetric = metric; RefreshHistory(); }
        };
        ToolTip.SetTip(combo, T("Chart"));
        _historyMetricPicker = combo;
        return combo;
    }

    private void SelectHistoryRange(HistoryRangeKind kind)
    {
        _historyRangeKind = kind;
        RefreshHistory();
    }

    private void Dates(int days) => SelectHistoryRange(days == 0 ? HistoryRangeKind.Today : HistoryRangeKind.Yesterday);

    private void RefreshHistory() => RefreshHistoryForDay(DateOnly.FromDateTime(DateTime.Today));

    private void RefreshHistoryForDay(DateOnly today)
    {
        _historyRangeValid = false;
        Set(_historyPrivacy, L("history.privacy"));
        var usage = _app.History.Days;
        var traffic = new HistoryTraffic(_app.Traffic.Hub.Days, _app.Traffic.Hub.Since, _app.Settings.Current.Traffic.IncludeTools);
        if (_historyRangeKind != HistoryRangeKind.Custom)
        {
            var dataDays = usage.Where(d => _selectedSource is null || d.Source == _selectedSource)
                .Select(d => HistoryRanges.TryParseDay(d.Day, out var date) ? (DateOnly?)date : null)
                .Where(d => d.HasValue).Select(d => d!.Value).Concat(traffic.AgentDays(_selectedSource)).ToArray();
            var resolved = HistoryRanges.Resolve(_historyRangeKind, today, dataDays.Length == 0 ? null : dataDays.Min());
            _updatingHistoryDates = true;
            try { _from.Text = HistoryRanges.FormatDay(resolved.Start); _to.Text = HistoryRanges.FormatDay(resolved.End); }
            finally { _updatingHistoryDates = false; }
        }
        foreach (var (kind, button) in _rangeButtons)
        { button.Content = L(HistoryRanges.LabelKey(kind)); button.Selected = kind == _historyRangeKind; button.SetPalette(_theme.Palette); }
        if (!HistoryRanges.TryParseDay(_from.Text, out var start) || !HistoryRanges.TryParseDay(_to.Text, out var end))
        { InvalidHistoryRange(T("Use YYYY-MM-DD dates.")); return; }
        if (end < start) { InvalidHistoryRange(T("End date must be on or after start date.")); return; }
        // Avoid allocating millions of daily slots after an accidental year edit; the normal presets use data bounds.
        if (end.DayNumber - start.DayNumber > 36_525)
        { InvalidHistoryRange(T("Choose a date range of 100 years or less.")); return; }
        _historyRangeValid = true;
        _historyChart.IsVisible = _historySummaryGrid.IsVisible = _historyRows.IsVisible = true;
        var range = new DayRange(start, end);
        Set(_historySubtitle, HistoryText.RangeSubtitle(range, today, _app.Loc));
        var cost = _app.Settings.Current.Cost;
        var options = new HistoryCostOptions(cost.Subscriptions, cost.HistoryIncludeSubscriptions ?? cost.IncludeSubscriptions);
        var rows = HistoryAggregator.Daily(usage, _selectedSource, range, options.Excluded(_selectedSource)).ToDictionary(d => d.Day);
        var networkDays = traffic.Days(range, _selectedSource, today, DateTimeOffset.Now);
        foreach (var (day, value) in networkDays)
            if (rows.ContainsKey(day) || !value.Shown.IsZero || value.Machine is { IsZero: false })
                rows[day] = (rows.GetValueOrDefault(day) ?? DaySummary.TrafficOnly(day)) with { Traffic = value };
        _days = rows.Values.OrderBy(d => d.Day).ToArray();
        RefreshHistoryState();
        var money = new HistoryMoney(_app.Cost.Display);
        _historyChart.Bars = _historyMetric == HistoryChartMetric.Traffic
            ? HistoryBars.Traffic(_days, range, _selectedSource, networkDays, traffic.Since)
            : HistoryBars.From(_days, range, _selectedSource, _historyMetric, money);
        _historyChart.TickFormat = _historyMetric switch
        {
            HistoryChartMetric.Traffic => v => TrafficFormat.Total((long)Math.Max(0, v)),
            HistoryChartMetric.Cost => v => v.ToString("0.##", _app.Loc.Culture),
            _ => v => SpeedFormat.Tokens((long)Math.Max(0, v)),
        };
        _historyChart.EmptyText = _app.History.State.Kind switch
        {
            HistoryStateKind.Analyzing => HistoryText.Analyzing(_app.History.State.Progress, _app.Loc),
            HistoryStateKind.NotLoaded => T("Loading history…"),
            HistoryStateKind.Failed => T("History could not be loaded. Use Refresh to retry."),
            _ => T("No data in this range"),
        };
        Set(_historyChartTitle, L(HistoryChartMetrics.TitleKey(_historyMetric)));
        Set(_historyChartUnit, _historyMetric switch
        { HistoryChartMetric.Cost => money.Symbol, HistoryChartMetric.Traffic => "↓ / ↑", _ => L("history.summary.output") });
        _historyChart.TooltipForDay = date => rows.TryGetValue(date, out var day)
            ? HistoryText.LongDate(date, _app.Loc) + "\n" + $"{day.Output:N0} {L("today.output")} · {day.Input:N0} {L("today.input")}\n"
                + $"{L("today.requests")}: {day.Requests:N0} · {HistoryText.Speed(day.AverageSpeed, _app.Loc)}\n"
                + money.Text(day.Cost, L("cost.subscription.tag"))
                + (day.Traffic is { } bytes ? $"\n↓ {bytes.Shown.Rx:N0} B · ↑ {bytes.Shown.Tx:N0} B" : "")
            : HistoryText.LongDate(date, _app.Loc);
        _historyChart.InvalidateVisual();
        RefreshHistoryLegend();
        var previous = range.Start.DayNumber >= range.DayCount
            ? HistoryAggregator.Daily(usage, _selectedSource, HistoryRanges.Previous(range), options.Excluded(_selectedSource)) : [];
        var sparkDays = range.Start.DayNumber >= 6
            ? HistoryAggregator.Daily(usage, _selectedSource, HistoryRanges.SparkRange(range), options.Excluded(_selectedSource)) : _days;
        var summary = HistorySummary.Build(_days, previous, range, today, _app.Loc, money, "history.summary.cost", sparkDays);
        for (var i = 0; i < summary.Count; i++)
        {
            var item = summary[i]; var view = _summaryViews[i];
            Set(view.Label, item.Label); Set(view.Value, item.Value + (item.Unit.Length > 0 ? " " + item.Unit : ""));
            Set(view.Note, item.Secondary);
            ToolTip.SetTip(view.Root, item.FullValue + (item.Note is { } note ? "\n" + note : ""));
            view.Spark.Values = item.Spark.Select(v => v ?? double.NaN).ToArray(); view.Spark.InvalidateVisual();
        }
        ArrangeHistorySummary();
        RefreshHistoryRows(usage, money, today);
    }

    private void InvalidHistoryRange(string message)
    {
        Set(_historyStatus, message);
        _historyChart.IsVisible = _historySummaryGrid.IsVisible = _historyRows.IsVisible = _historyProgress.IsVisible = false;
    }

    private void RefreshHistoryState()
    {
        var state = _app.History.State;
        var status = state.Kind switch
        {
            HistoryStateKind.NotLoaded => T("Loading history…"),
            HistoryStateKind.Analyzing => HistoryText.Analyzing(state.Progress, _app.Loc) + " · " + HistoryText.AnalyzingFiles(state.FilesDone, state.FilesTotal, _app.Loc),
            HistoryStateKind.Failed => _days.Count > 0
                ? T("History could not be refreshed. Showing the last available data; use Refresh to retry.")
                : T("History could not be loaded. Use Refresh to retry."),
            _ => state.UpdatedAt is { } at ? HistoryText.Updated(at, DateTimeOffset.Now, _app.Loc) : T("Ready"),
        };
        Set(_historyStatus, status + "  ·  " + HistoryText.Days(_days.Count, _app.Loc));
        _historyProgress.IsVisible = state.Kind is HistoryStateKind.Analyzing or HistoryStateKind.NotLoaded;
        _historyProgress.IsIndeterminate = state.Kind == HistoryStateKind.NotLoaded;
        _historyProgress.Value = double.IsFinite(state.Progress) ? Math.Clamp(state.Progress, 0, 1) : 0;
        // Keep diagnostics out of labels; the failure remains available for troubleshooting.
        ToolTip.SetTip(_historyStatus, state.Error);
        if (state.Kind == HistoryStateKind.Failed && _historyFailure != state.Error)
            AgentSpeed.App.AppLog.Warn("History refresh failed: " + state.Error);
        _historyFailure = state.Kind == HistoryStateKind.Failed ? state.Error : null;
    }

    private void RefreshHistoryLegend()
    {
        _historyLegendScope?.Dispose(); _historyLegendScope = _theme.BeginScope();
        using var activation = _theme.UseScope(_historyLegendScope);
        _historyLegend.Children.Clear();
        foreach (var source in _historyChart.Bars.Order)
        {
            var color = HeatPalette.SourceColor(source);
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(color.R8, color.G8, color.B8)) };
            var row = Row(dot, ThemedText(L(DisplayText.SourceKey(source)), 11, TextRole.Secondary));
            row.ItemSpacing = 5; _historyLegend.Children.Add(row);
        }
        if (_historyMetric == HistoryChartMetric.Output && _historyChart.Bars.HasSpeed)
            _historyLegend.Children.Add(ThemedText("— " + L("history.summary.speed") + " (tok/s)", 11, TextRole.Secondary));
    }

    private void RefreshHistoryRows(IReadOnlyList<DailyUsage> usage, HistoryMoney money, DateOnly today)
    {
        var details = usage.Where(d => _selectedSource is null || d.Source == _selectedSource).ToLookup(d => d.Day);
        foreach (var removed in _dayRows.Keys.Except(_days.Select(d => d.Day)).ToArray())
        {
            _historyRows.Children.Remove(_dayRows[removed]); _dayRows.Remove(removed);
            _historyRowScopes.Remove(removed, out var scope); scope?.Dispose();
        }
        foreach (var day in _days.Reverse())
        {
            if (!_dayRows.TryGetValue(day.Day, out var row))
            {
                row = new Expander { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                _dayRows.Add(day.Day, row);
            }
            if (_historyRowScopes.Remove(day.Day, out var oldScope)) oldScope.Dispose();
            var scope = _theme.BeginScope(); _historyRowScopes[day.Day] = scope;
            using var activation = _theme.UseScope(scope);
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            var date = ThemedText(HistoryText.TableDate(day.Day, today, _app.Loc), 13);
            date.FontWeight = FontWeight.SemiBold;
            ToolTip.SetTip(date, HistoryText.LongDate(day.Day, _app.Loc));
            var counts = ThemedText($"{SpeedFormat.Tokens(day.Output)} {L("today.output")}  ·  {SpeedFormat.Tokens(day.Input)} {L("today.input")}  ·  {SpeedFormat.Tokens(day.Requests)} {L("today.requests")}", 11, TextRole.Secondary);
            header.Children.Add(Stack(date, counts));
            var amount = ThemedText(money.Text(day.Cost, L("cost.subscription.tag")), 13);
            ToolTip.SetTip(amount, HistoryTable.CostTooltip(day.Cost, money, _app.Loc));
            Grid.SetColumn(amount, 1); header.Children.Add(amount);
            row.Header = header;
            var content = Stack(ThemedText($"{L("today.average")}: {HistoryText.Speed(day.AverageSpeed, _app.Loc)}  ·  {L("today.peak")}: {HistoryText.Speed(day.Peak, _app.Loc)}", 12, TextRole.Secondary));
            if (day.Traffic is { } net)
                content.Children.Add(ThemedText($"↓ {TrafficFormat.Total(net.Shown.Rx)}   ↑ {TrafficFormat.Total(net.Shown.Tx)}", 12, TextRole.Secondary));
            foreach (var source in details[HistoryRanges.FormatDay(day.Day)])
                foreach (var model in source.Models)
                    content.Children.Add(ThemedText($"{L(DisplayText.SourceKey(source.Source))} · {model.Model}  ·  {SpeedFormat.Tokens(model.Output)} {L("today.output")}  ·  {HistoryText.Speed(model.AverageSpeed, _app.Loc)}", 12, TextRole.Secondary));
            foreach (var mode in day.Modes)
                content.Children.Add(ThemedText($"{mode.Model} · {mode.Mode} · {mode.Requests:N0} {L("today.requests")}", 12, TextRole.Secondary));
            row.Content = content;
            _theme.On(() => { row.Background = _theme.Palette.Brush(_theme.Palette.Card); row.BorderBrush = _theme.Palette.BorderBrush; row.Foreground = _theme.Palette.PrimaryBrush; });
        }
        var ordered = _days.Reverse().Select(d => _dayRows[d.Day]).ToArray();
        if (!_historyRows.Children.SequenceEqual(ordered))
        { _historyRows.Children.Clear(); foreach (var row in ordered) _historyRows.Children.Add(row); }
    }
    private async Task Export(bool modes)
        => await ExportToAsync(modes, async () =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = modes ? "agent-speed-modes.csv" : "agent-speed-history.csv",
                FileTypeChoices = [new("CSV") { Patterns = ["*.csv"] }],
            });
            return file is null ? null : await file.OpenWriteAsync();
        });

    private async Task ExportToAsync(bool modes, Func<Task<Stream?>> open)
    {
        RefreshHistory();
        if (!_historyRangeValid) return;
        // Capture the chosen range, filter and currency before the asynchronous file picker.
        var text = modes
            ? ExecutionModeCsv.Build(_days)
            : HistoryCsv.Build(_days, _selectedSource, new HistoryMoney(_app.Cost.Display), _app.Traffic.Hub.Since is not null);
        try
        {
            await using var output = await open();
            if (output is null) return;
            output.SetLength(0);
            await output.WriteAsync(Encoding.UTF8.GetBytes(text));
            _notice.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AgentSpeed.App.AppLog.Warn("History export failed: " + ex.Message);
            _notice.Text = T("Unable to export CSV. Choose a writable file and try again.");
        }
    }

}
