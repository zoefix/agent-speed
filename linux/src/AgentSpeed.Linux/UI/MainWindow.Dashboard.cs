using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Settings;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentSpeed.Linux.UI;

internal sealed record SourceRowView(
    DesktopButton Button,
    TextBlock Name,
    TextBlock Status,
    TextBlock Rate,
    TextBlock Today,
    Border Dot,
    ThemeScope ThemeScope)
{
    public bool Active { get; set; }
}

internal sealed class AgentRowView
{
    public required Grid Root { get; init; }
    public required TextBlock Project { get; init; }
    public required TextBlock Detail { get; init; }
    public required TextBlock Phase { get; init; }
    public required TextBlock Rate { get; init; }
    public required Border PhasePill { get; init; }
    public required Border Dot { get; init; }
    public ThemeScope? ThemeScope { get; set; }
}

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, AgentRowView> _agentRows = [];
    private readonly List<string> _agentOrder = [];
    private TextBlock _chartTitle = null!;
    private TextBlock _chartEstimate = null!;
    private TextBlock _chartUnit = null!;
    private TextBlock _speedEyebrow = null!;
    private TextBlock _outputLabel = null!;
    private TextBlock _inputLabel = null!;
    private TextBlock _requestsLabel = null!;
    private TextBlock _costLabel = null!;
    private TextBlock _peakLabel = null!;
    private TextBlock _trafficLabel = null!;
    private Border _warningCard = null!;
    private Border _outputTile = null!;
    private Border _inputTile = null!;
    private Border _requestsTile = null!;
    private Border _costTile = null!;
    private WrapPanel _auxPanel = null!;

    private void BuildDashboard()
    {
        _dashboardTitle = ThemedText(T("Dashboard"), 24, TextRole.Primary);
        _dashboardTitle.FontWeight = FontWeight.SemiBold;
        _dashboardSubtitle = ThemedText(T("Live speed, usage and sources from local logs."), 11.5, TextRole.Secondary);
        var titleBlock = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        titleBlock.Children.Add(_dashboardTitle);
        titleBlock.Children.Add(_dashboardSubtitle);

        var activeDot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        _theme.On(() => activeDot.Background = _theme.Palette.Brush(_theme.Palette.Success));
        _activeBadgeText = ThemedText("", 11.5, TextRole.Secondary);
        var activeContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        activeContent.Children.Add(activeDot);
        activeContent.Children.Add(_activeBadgeText);
        _activeBadge = new Border
        {
            Child = activeContent,
            Padding = new Thickness(9, 4),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _theme.On(() =>
        {
            _activeBadge.Background = _theme.Palette.Brush(DesktopStyles.Alpha(_theme.Palette.Accent, (byte)(_theme.Palette.Dark ? 26 : 18)));
            _activeBadge.BorderBrush = _theme.Palette.Brush(DesktopStyles.Alpha(_theme.Palette.Accent, (byte)(_theme.Palette.Dark ? 70 : 48)));
        });

        _dashboardHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        _dashboardHeader.Children.Add(titleBlock);
        Grid.SetColumn(_activeBadge, 1);
        _dashboardHeader.Children.Add(_activeBadge);

        _warningCard = BuildWarningCard();
        var hero = BuildHeroCard();
        var chart = BuildChartCard();
        _sectionsGrid = BuildSections();

        _dashboard = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 28) };
        _dashboard.Children.Add(_dashboardHeader);
        _dashboard.Children.Add(_warningCard);
        _dashboard.Children.Add(hero);
        _dashboard.Children.Add(chart);
        _dashboard.Children.Add(_sectionsGrid);
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();
    }

    private Border BuildWarningCard()
    {
        var diamond = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(1.5),
            RenderTransform = new RotateTransform(45),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _theme.On(() => diamond.Background = _theme.Palette.WarningBrush);
        _warningSummary = ThemedText("", 11.5, TextRole.Warning);
        _estimateTag = ThemedText(T("Estimated"), 10, TextRole.Warning);
        _estimatePill = new Border
        {
            Child = _estimateTag,
            Padding = new Thickness(6, 0),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        _theme.On(() =>
        {
            _estimatePill.Background = _theme.Palette.Brush(DesktopStyles.Alpha(_theme.Palette.WarningText, (byte)(_theme.Palette.Dark ? 22 : 16)));
            _estimatePill.BorderBrush = _theme.Palette.Brush(DesktopStyles.Alpha(_theme.Palette.WarningText, 60));
        });
        _warningChevron = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L4,4 L8,0"),
            Width = 9,
            Height = 6,
            Stretch = Stretch.None,
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        };
        _theme.On(() => _warningChevron.Stroke = _theme.Palette.SecondaryBrush);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        header.Children.Add(diamond);
        Grid.SetColumn(_warningSummary, 1);
        header.Children.Add(_warningSummary);
        Grid.SetColumn(_estimatePill, 2);
        header.Children.Add(_estimatePill);
        Grid.SetColumn(_warningChevron, 3);
        header.Children.Add(_warningChevron);

        _warningDetails = ThemedText("", 11.5, TextRole.Secondary);
        _warningDetails.Margin = new Thickness(13, 0, 13, 9);
        _warningDetails.IsVisible = false;
        _warningToggle = new DesktopButton(_theme.Palette, DesktopButtonKind.Ghost, header)
        {
            Height = 32,
            Padding = new Thickness(9, 0),
            CornerRadius = new CornerRadius(9),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _warningToggle.Click += (_, _) =>
        {
            _warningExpanded = !_warningExpanded;
            _warningDetails.IsVisible = _warningExpanded;
            _warningChevron.RenderTransform = new RotateTransform(_warningExpanded ? 180 : 0);
        };
        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(_warningToggle);
        content.Children.Add(_warningDetails);
        var card = new Border
        {
            Child = content,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };
        _theme.On(() =>
        {
            card.Background = _theme.Palette.Brush(DesktopStyles.Alpha(_theme.Palette.WarningBackground, (byte)(_theme.Palette.Dark ? 230 : 220)));
            card.BorderBrush = _theme.Palette.Brush(DesktopStyles.Alpha(_theme.Palette.WarningBorder, 180));
        });
        return card;
    }

    private Border BuildHeroCard()
    {
        _speedEyebrow = ThemedText(T("Live speed"), 10.5, TextRole.Muted);
        _speed = ThemedText("0", 46, TextRole.Primary);
        _speed.FontWeight = FontWeight.Bold;
        _speed.LineHeight = 48;
        _speedUnit = ThemedText(L("unit.tokS"), 15, TextRole.Secondary);
        _speedUnit.Margin = new Thickness(0, 0, 0, 6);
        var valueRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        valueRow.Children.Add(_speed);
        valueRow.Children.Add(_speedUnit);
        _title = ThemedText("", 14.5, TextRole.Accent);
        _title.FontWeight = FontWeight.SemiBold;
        _context = ThemedText("", 10.5, TextRole.Secondary);
        var reading = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        reading.Children.Add(_speedEyebrow);
        reading.Children.Add(valueRow);
        reading.Children.Add(_title);
        reading.Children.Add(_context);

        _hero = new HeroControl(_app)
        {
            Width = 128,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var heroGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
        heroGrid.Children.Add(reading);
        Grid.SetColumn(_hero, 1);
        heroGrid.Children.Add(_hero);

        _metricGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        _outputTile = MetricTile(T("Today's output"), null, out _outputLabel, out _outputMetric);
        _inputTile = MetricTile(T("Today's input"), null, out _inputLabel, out _inputMetric);
        _requestsTile = MetricTile(T("Requests"), null, out _requestsLabel, out _requestsMetric);
        _costTile = MetricTile(L("today.cost"), L("cost.apiEquivalent"), out _costLabel, out _costMetric);
        _costTag = (_costTile.Tag as TextBlock)!;
        _metricGrid.Children.Add(_outputTile);
        _metricGrid.Children.Add(_inputTile);
        _metricGrid.Children.Add(_requestsTile);
        _metricGrid.Children.Add(_costTile);

        _peakLabel = ThemedText(L("today.peak"), 11.5, TextRole.Muted);
        _peakText = ThemedText("", 11.5, TextRole.Secondary);
        var peakLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        peakLine.Children.Add(_peakLabel);
        peakLine.Children.Add(_peakText);
        _trafficLabel = ThemedText(T("Machine network"), 11.5, TextRole.Muted);
        _trafficText = ThemedText("", 11.5, TextRole.Secondary);
        var trafficLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        trafficLine.Children.Add(_trafficLabel);
        trafficLine.Children.Add(_trafficText);
        _auxPanel = new WrapPanel
        {
            ItemSpacing = 18,
            LineSpacing = 2,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _auxPanel.Children.Add(peakLine);
        _auxPanel.Children.Add(trafficLine);

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(heroGrid);
        content.Children.Add(_metricGrid);
        content.Children.Add(_auxPanel);
        return Card(content, new Thickness(15));
    }

    private Border MetricTile(string label, string? tag, out TextBlock labelBlock, out TextBlock valueBlock)
    {
        labelBlock = ThemedText(label, 11.5, TextRole.Muted);
        valueBlock = ThemedText("0", 18, TextRole.Primary);
        valueBlock.FontWeight = FontWeight.SemiBold;
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        labelRow.Children.Add(labelBlock);
        TextBlock? tagBlock = null;
        if (tag is not null)
        {
            tagBlock = ThemedText(tag, 10.5, TextRole.Muted);
            labelRow.Children.Add(tagBlock);
        }
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(labelRow);
        content.Children.Add(valueBlock);
        var tile = Inset(content, new Thickness(8, 6));
        tile.Tag = tagBlock ?? labelBlock;
        return tile;
    }

    private Border BuildChartCard()
    {
        _chartTitle = ThemedText(T("Live trend"), 14.5, TextRole.Primary);
        _chartTitle.FontWeight = FontWeight.SemiBold;
        _chartUnit = ThemedText(T("per second"), 10.5, TextRole.Muted);
        _chartRange = ThemedText(T("Last 2 minutes"), 10.5, TextRole.Secondary);
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        sub.Children.Add(_chartRange);
        sub.Children.Add(_chartUnit);
        var titleStack = new StackPanel { Spacing = 2 };
        titleStack.Children.Add(_chartTitle);
        titleStack.Children.Add(sub);
        _chartLatest = ThemedText("—", 13, TextRole.Primary);
        _chartLatest.FontWeight = FontWeight.SemiBold;
        _chartEstimate = ThemedText(L("chart.estimated"), 10, TextRole.Warning);
        var latest = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        latest.Children.Add(_chartEstimate);
        latest.Children.Add(_chartLatest);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(titleStack);
        Grid.SetColumn(latest, 1);
        header.Children.Add(latest);
        _chart = new LineChart
        {
            Height = 112,
            Margin = new Thickness(0, 5, 0, 0),
            EmptyText = T("Waiting for data"),
            Unit = "",
        };
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(header);
        content.Children.Add(_chart);
        return Card(content);
    }

    private Grid BuildSections()
    {
        _sourcesHeading = ThemedText(L("dashboard.sources"), 14.5, TextRole.Primary);
        _sourcesHeading.FontWeight = FontWeight.SemiBold;
        _sourcesHint = ThemedText(T("Filter history by source"), 10, TextRole.Muted);
        var sourceHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        sourceHeader.Children.Add(_sourcesHeading);
        Grid.SetColumn(_sourcesHint, 1);
        sourceHeader.Children.Add(_sourcesHint);
        _sourcesEmpty = ThemedText(T("No source activity yet"), 11.5, TextRole.Muted);
        _sourcesEmpty.Margin = new Thickness(2, 6, 0, 6);
        _otherSourcesExpander = new Expander
        {
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 0),
        };
        _otherSourcesHeader = ThemedText(T("Other sources"), 11.5, TextRole.Secondary);
        _otherSourcesExpander.Header = _otherSourcesHeader;
        _otherSourcesExpander.Content = _otherSources;
        var sourceContent = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        sourceContent.Children.Add(sourceHeader);
        sourceContent.Children.Add(_sources);
        sourceContent.Children.Add(_sourcesEmpty);
        sourceContent.Children.Add(_otherSourcesExpander);
        _sourcesCard = Card(sourceContent);

        _agentsHeading = ThemedText(T("Working agents"), 14.5, TextRole.Primary);
        _agentsHeading.FontWeight = FontWeight.SemiBold;
        _agentsEmpty = ThemedText(L("agents.empty"), 11.5, TextRole.Muted);
        _agentsEmpty.Margin = new Thickness(2, 6, 0, 6);
        var agentContent = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        agentContent.Children.Add(_agentsHeading);
        agentContent.Children.Add(_agents);
        agentContent.Children.Add(_agentsEmpty);
        _agentsCard = Card(agentContent);

        var grid = new Grid();
        grid.Children.Add(_sourcesCard);
        grid.Children.Add(_agentsCard);
        return grid;
    }

    private void UpdateResponsiveLayout()
    {
        if (_sectionsGrid is null || _metricGrid is null) return;
        var width = Bounds.Width > 0 && double.IsFinite(Bounds.Width) ? Bounds.Width : Width;
        var wide = width >= 980;
        _sectionsGrid.ColumnDefinitions = wide ? new ColumnDefinitions("*,*") : new ColumnDefinitions("*");
        _sectionsGrid.RowDefinitions = wide ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto");
        _sectionsGrid.ColumnSpacing = wide ? 14 : 0;
        _sectionsGrid.RowSpacing = wide ? 0 : 14;
        Grid.SetColumn(_sourcesCard, 0);
        Grid.SetRow(_sourcesCard, 0);
        Grid.SetColumn(_agentsCard, wide ? 1 : 0);
        Grid.SetRow(_agentsCard, wide ? 0 : 1);

        var four = width >= 960;
        _metricGrid.ColumnDefinitions = four
            ? new ColumnDefinitions("*,*,*,*")
            : new ColumnDefinitions("*,*");
        _metricGrid.RowDefinitions = four ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto");
        var tiles = new[] { _outputTile, _inputTile, _requestsTile, _costTile };
        for (var i = 0; i < tiles.Length; i++)
        {
            Grid.SetColumn(tiles[i], four ? i : i % 2);
            Grid.SetRow(tiles[i], four ? 0 : i / 2);
        }

    }

    private void RefreshLive()
    {
        RefreshNetworkStatus();
        if (_dashboardTitle is null || _chart is null) return;
        var snapshot = _app.Speed.Snapshot;
        var settings = _app.Settings.Current;
        var metric = settings.Metric;
        var value = snapshot.Live.ValueFor(metric);
        var redline = settings.Redline;
        var heat = HeatMath.Heat(value, redline);
        var tier = SpeedTitles.Raw(value, redline);
        var title = settings.TitleThemes.General.Label(tier, _app.Loc);
        var line = HeatPalette.Sample(Math.Max(heat, .12));
        var lineColor = Color.FromRgb(line.R8, line.G8, line.B8);

        if (settings.Cost.LiveUnit == LiveUnit.Cost)
        {
            var money = _app.Cost.Display.Live(snapshot);
            Set(_speed, money.NumberText);
            Set(_speedUnit, money.Unit);
            _speed.Foreground = money.Unknown ? _theme.Palette.SecondaryBrush : _theme.Palette.PrimaryBrush;
        }
        else
        {
            Set(_speed, SpeedFormat.Speed(value));
            Set(_speedUnit, L("unit.tokS"));
            _speed.Foreground = _theme.Palette.PrimaryBrush;
        }
        Set(_title, title);
        _title.Foreground = _theme.Palette.Brush(settings.Cost.LiveUnit == LiveUnit.Cost && _app.Cost.Display.Live(snapshot).Unknown
            ? _theme.Palette.TextMuted
            : IsDarkTheme ? lineColor : ToColor(OrbTokens.Light.TierLabel(heat)));
        Set(_context, _app.Loc.Format("agents.context", snapshot.GeneratingCount, snapshot.ToolsCount));

        Set(_activeBadgeText, _app.Loc.Format("agents.context", snapshot.GeneratingCount, snapshot.ToolsCount));
        _activeBadge.Opacity = snapshot.GeneratingCount + snapshot.ToolsCount > 0 ? 1 : .72;

        UpdateWarnings(snapshot);
        UpdateMetrics(snapshot);
        UpdateAuxiliary(snapshot);
        UpdateChart(snapshot, metric, value, lineColor);
        if (_page != "dashboard") return;
        UpdateSources(snapshot, settings);
        UpdateAgents(snapshot, settings);
    }

    private void UpdateWarnings(SpeedSnapshot snapshot)
    {
        var warnings = snapshot.Warnings.Select(w => DisplayText.Warning(w, _app.Loc))
            .Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        _warningCard.IsVisible = _page == "dashboard" && warnings.Length > 0;
        _warningToggle.IsVisible = warnings.Length > 0;
        _warningDetails.Text = string.Join("\n\n", warnings);
        if (warnings.Length == 0)
        {
            _warningExpanded = false;
            _warningDetails.IsVisible = false;
            _warningChevron.RenderTransform = new RotateTransform(0);
        }
        Set(_warningSummary, $"{T("Usage notice")} · {warnings.Length}");
        Set(_estimateTag, T("Estimated"));
        var estimated = snapshot.LiveEstimatedShare > .001 || snapshot.Warnings.Any(w => w is MonitorWarning.UnfinalizedUsage);
        _estimatePill.IsVisible = estimated && warnings.Length > 0;
        _chartEstimate.IsVisible = estimated;
    }

    private void UpdateMetrics(SpeedSnapshot snapshot)
    {
        Set(_outputMetric, SpeedFormat.Tokens(snapshot.Today.Output));
        Set(_inputMetric, SpeedFormat.Tokens(snapshot.Today.Input));
        Set(_requestsMetric, snapshot.Today.Requests.ToString("N0", _app.Loc.Culture));
        var summary = TodayCostSummary.From(snapshot, _app.Cost.Display);
        Set(_costMetric, summary.AmountText);
        _costMetric.Foreground = summary.AmountText == CostDisplay.Dash
            ? _theme.Palette.MutedBrush
            : _theme.Palette.PrimaryBrush;
        Set(_outputLabel, T("Today's output"));
        Set(_inputLabel, T("Today's input"));
        Set(_requestsLabel, T("Requests"));
        Set(_costLabel, L("today.cost"));
        _costTag!.Text = L("cost.apiEquivalent");
        ToolTip.SetTip(_outputTile, $"{T("Today's output")}: {snapshot.Today.Output.ToString("N0", _app.Loc.Culture)}");
        ToolTip.SetTip(_inputTile, $"{T("Today's input")}: {snapshot.Today.Input.ToString("N0", _app.Loc.Culture)}");
        ToolTip.SetTip(_requestsTile, $"{L("today.requests")}: {snapshot.Today.Requests.ToString("N0", _app.Loc.Culture)}");
        var costTip = $"{L("cost.apiEquivalent")}: {summary.AmountText}\n{L("cost.apiEquivalent.hint")}";
        if (summary.UnpricedText is { } unpriced) costTip += "\n" + unpriced;
        if (summary.ExcludedText is { } excluded) costTip += "\n" + excluded;
        ToolTip.SetTip(_costTile, costTip);
    }

    private void UpdateAuxiliary(SpeedSnapshot snapshot)
    {
        var settings = _app.Settings.Current;
        var peak = snapshot.PeakToday.ValueFor(settings.Metric);
        var peakText = $"{SpeedFormat.Speed(peak)} {L("unit.tokS")}";
        if (snapshot.PeakTodayAt is { } at)
            peakText += $"  ·  {at.ToLocalTime().ToString("t", _app.Loc.Culture)}";
        Set(_peakText, peakText);
        Set(_peakLabel, L("today.peak"));
        Set(_trafficLabel, T("Machine network"));
        Set(_speedEyebrow, T("Live speed"));
        var traffic = _app.Traffic.Hub.Snapshot;
        Set(_trafficText, traffic.MachineLive is { } rate
            ? $"↓ {TrafficFormat.Rate(rate.Rx, settings.Traffic.Bits)}   ↑ {TrafficFormat.Rate(rate.Tx, settings.Traffic.Bits)}"
            : T("Network: waiting for samples"));
    }

    private void UpdateChart(SpeedSnapshot snapshot, SpeedMetric metric, double value, Color lineColor)
    {
        _chart.Values = snapshot.History
            .Select(point => point.Measured.ValueFor(metric) + point.Estimated.ValueFor(metric))
            .ToArray();
        _chart.LineBrush = new SolidColorBrush(lineColor);
        _chart.GridBrush = _theme.Palette.Brush(_theme.Palette.ChartGrid);
        _chart.MutedBrush = _theme.Palette.SecondaryBrush;
        _chart.FillAlpha = _theme.Palette.HighContrast ? .35 : _theme.Palette.Dark ? .24 : .18;
        _chart.Unit = "";
        _chart.StartLabel = "−" + T("2 minutes");
        _chart.EndLabel = T("Now");
        _chart.EmptyText = T("Waiting for data");
        _chart.InvalidateVisual();
        Set(_chartRange, T("Last 2 minutes"));
        Set(_chartUnit, T("per second"));
        Set(_chartLatest, DisplayText.IsNegligibleSpeed(value) ? "—" : $"{SpeedFormat.Speed(value)} {L("unit.tokS")}");
        _chartLatest.Foreground = _theme.Palette.Brush(lineColor);
        Set(_chartTitle, T("Live trend"));
        _chartEstimate.Text = L("chart.estimated");
    }

    private void UpdateSources(SpeedSnapshot snapshot, AppSettings settings)
    {
        var statuses = snapshot.Sources;
        foreach (var removed in _sourceRows.Keys.Except(statuses.Select(x => x.Kind)).ToArray())
        {
            var row = _sourceRows[removed];
            if (row.Button.Parent is Panel parent) parent.Children.Remove(row.Button);
            row.ThemeScope.Dispose();
            _sourceRows.Remove(removed);
        }

        var main = new List<DesktopButton>();
        var other = new List<DesktopButton>();
        foreach (var status in statuses.OrderBy(s => s.Kind))
        {
            if (!_sourceRows.TryGetValue(status.Kind, out var row))
            {
                row = CreateSourceRow(status.Kind);
                _sourceRows.Add(status.Kind, row);
            }
            UpdateSourceRow(row, status, settings);
            if (SourceDetection.ProducedToday(status) || status.ActiveAgents > 0) main.Add(row.Button);
            else other.Add(row.Button);
        }
        ArrangeSourcePanel(_sources, main);
        ArrangeSourcePanel(_otherSources, other);
        _sourcesEmpty.IsVisible = main.Count == 0;
        _otherSourcesExpander.IsVisible = other.Count > 0;
        _otherSourcesHeader.Text = $"{T("Other sources")} · {other.Count}";
    }

    private SourceRowView CreateSourceRow(SourceKind kind)
    {
        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        };
        var color = HeatPalette.SourceColor(kind);
        dot.Background = new SolidColorBrush(Color.FromRgb(color.R8, color.G8, color.B8));
        var name = Text("", 13);
        var status = Text("", 11);
        var rate = Text("", 13);
        rate.TextAlignment = TextAlignment.Right;
        rate.HorizontalAlignment = HorizontalAlignment.Right;
        var today = Text("", 11);
        today.TextAlignment = TextAlignment.Right;
        today.HorizontalAlignment = HorizontalAlignment.Right;
        var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(name);
        left.Children.Add(status);
        var right = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, MinWidth = 96 };
        right.Children.Add(rate);
        right.Children.Add(today);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(dot);
        Grid.SetColumn(left, 1);
        grid.Children.Add(left);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        var button = new DesktopButton(_theme.Palette, DesktopButtonKind.Source, grid)
        {
            Padding = new Thickness(9, 5),
            Margin = new Thickness(0, 1),
            MinHeight = 45,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.Click += (_, _) =>
        {
            SelectPage("history");
            _historySource.SelectedIndex = (int)kind + 1;
        };
        var scope = _theme.BeginScope();
        var row = new SourceRowView(button, name, status, rate, today, dot, scope);
        scope.Add(() => { button.SetPalette(_theme.Palette); ApplySourceTheme(row); });
        return row;
    }

    private void UpdateSourceRow(SourceRowView row, SourceStatus status, AppSettings settings)
    {
        var live = status.Live.ValueFor(settings.Metric);
        row.Active = status.Enabled && status.Available && (status.ActiveAgents > 0 || live >= DisplayText.ZeroSpeedThreshold);
        var name = L(DisplayText.SourceKey(status.Kind));
        Set(row.Name, name);
        Set(row.Status, !status.Enabled ? L("source.disabled") : !status.Available ? L("source.notFound") : row.Active ? T("Active") : T("Available"));
        Set(row.Rate, row.Active && !DisplayText.IsNegligibleSpeed(live) ? $"{SpeedFormat.Speed(live)} {L("unit.tokS")}" : "—");
        Set(row.Today, $"{SpeedFormat.Tokens(status.Today.Output)} · {L("today.output")}");
        ApplySourceTheme(row);
        var tip = $"{name}\n{row.Status.Text} · {row.Rate.Text}\n"
            + $"{L("today.output")}: {status.Today.Output.ToString("N0", _app.Loc.Culture)}\n"
            + $"{L("today.input")}: {status.Today.Input.ToString("N0", _app.Loc.Culture)}\n"
            + $"{L("today.requests")}: {status.Today.Requests.ToString("N0", _app.Loc.Culture)}";
        ToolTip.SetTip(row.Button, tip);
        AutomationProperties.SetName(row.Button, tip.Replace("\n", ", ", StringComparison.Ordinal));
    }

    private void ApplySourceTheme(SourceRowView row)
    {
        row.Name.Foreground = _theme.Palette.PrimaryBrush;
        row.Status.Foreground = row.Active ? _theme.Palette.Brush(_theme.Palette.Success) : _theme.Palette.MutedBrush;
        row.Rate.Foreground = row.Active ? _theme.Palette.PrimaryBrush : _theme.Palette.SecondaryBrush;
        row.Today.Foreground = _theme.Palette.MutedBrush;
    }

    /// <summary>
    /// Reorders one source panel without clearing unchanged rows. Any row that lived in the other panel is detached before
    /// insertion so a category move (other → main or main → other) never adds a control that still has a parent.
    /// </summary>
    private static void ArrangeSourcePanel(Panel panel, IReadOnlyList<DesktopButton> desired)
    {
        // Remove rows that no longer belong here; rows that stay keep their instance and focus state.
        for (var index = panel.Children.Count - 1; index >= 0; index--)
        {
            var child = panel.Children[index];
            if (!desired.Contains(child)) panel.Children.RemoveAt(index);
        }

        // A row moving between panels must leave its old parent before it can be inserted here.
        foreach (var control in desired)
            if (control.Parent is Panel oldParent && oldParent != panel)
                oldParent.Children.Remove(control);

        for (var index = 0; index < desired.Count; index++)
        {
            var control = desired[index];
            var current = panel.Children.IndexOf(control);
            if (current == index) continue;
            if (current >= 0) panel.Children.RemoveAt(current);
            panel.Children.Insert(index, control);
        }

        while (panel.Children.Count > desired.Count && panel.Children.Count > 0)
            panel.Children.RemoveAt(panel.Children.Count - 1);
    }

    private void UpdateAgents(SpeedSnapshot snapshot, AppSettings settings)
    {
        var agents = snapshot.Agents;
        var wanted = agents.Select(a => a.Id).ToArray();
        foreach (var removed in _agentRows.Keys.Except(wanted).ToArray())
        {
            var row = _agentRows[removed];
            if (row.Root.Parent is Panel parent) parent.Children.Remove(row.Root);
            row.ThemeScope?.Dispose();
            _agentRows.Remove(removed);
        }
        foreach (var agent in agents)
        {
            if (!_agentRows.TryGetValue(agent.Id, out var row))
            {
                row = CreateAgentRow();
                _agentRows.Add(agent.Id, row);
            }
            UpdateAgentRow(row, agent, settings);
        }
        if (!_agentOrder.SequenceEqual(wanted))
        {
            _agentOrder.Clear();
            _agentOrder.AddRange(wanted);
            _agents.Children.Clear();
            foreach (var id in _agentOrder)
                if (_agentRows.TryGetValue(id, out var row)) _agents.Children.Add(row.Root);
        }
        _agentsEmpty.IsVisible = agents.Count == 0;
    }

    private AgentRowView CreateAgentRow()
    {
        var project = Text("", 13);
        project.FontWeight = FontWeight.SemiBold;
        project.TextWrapping = TextWrapping.Wrap;
        project.MaxLines = 2;
        var detail = Text("", 11);
        detail.TextWrapping = TextWrapping.Wrap;
        detail.MaxLines = 2;
        var phase = Text("", 10.5);
        var phaseDot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        var phaseContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        phaseContent.Children.Add(phaseDot);
        phaseContent.Children.Add(phase);
        var phasePill = new Border
        {
            Child = phaseContent,
            Padding = new Thickness(6, 3),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var rate = Text("", 12);
        rate.HorizontalAlignment = HorizontalAlignment.Right;
        rate.TextAlignment = TextAlignment.Right;
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
        text.Children.Add(project);
        text.Children.Add(detail);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,64"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 4, 0, 4),
            MinHeight = 40,
        };
        grid.Children.Add(text);
        Grid.SetColumn(phasePill, 1);
        grid.Children.Add(phasePill);
        Grid.SetColumn(rate, 2);
        grid.Children.Add(rate);
        var row = new AgentRowView
        {
            Root = grid,
            Project = project,
            Detail = detail,
            Phase = phase,
            Rate = rate,
            PhasePill = phasePill,
            Dot = phaseDot,
        };
        var scope = _theme.BeginScope();
        scope.Add(() => ApplyAgentTheme(row));
        row.ThemeScope = scope;
        return row;
    }

    private void UpdateAgentRow(AgentRowView row, AgentStatus agent, AppSettings settings)
    {
        var project = string.IsNullOrWhiteSpace(agent.Project) ? L(DisplayText.SourceKey(agent.Source)) : agent.Project;
        Set(row.Project, project);
        var source = L(DisplayText.SourceKey(agent.Source));
        var detail = string.IsNullOrWhiteSpace(agent.Model) ? source : $"{source} · {agent.Model}";
        if (agent.IsSubagent) detail = $"{L("agents.subagent")} · {detail}";
        Set(row.Detail, detail);
        var phase = L(DisplayText.PhaseKey(agent.Phase));
        Set(row.Phase, phase);
        var live = agent.Live.ValueFor(settings.Metric);
        Set(row.Rate, DisplayText.IsNegligibleSpeed(live) ? "—" : $"{SpeedFormat.Speed(live)} {L("unit.tokS")}");
        row.Root.Margin = new Thickness(agent.IsSubagent ? 14 : 0, 5, 0, 5);
        ApplyAgentTheme(row, agent);
        var tip = $"{project}\n{detail}\n{phase} · {row.Rate.Text}";
        if (!agent.Modes.IsEmpty) tip += "\n" + ExecutionModeText.Detail(agent.Modes, _app.Loc);
        ToolTip.SetTip(row.Root, tip);
        AutomationProperties.SetName(row.Root, tip.Replace("\n", ", ", StringComparison.Ordinal));
    }

    private void ApplyAgentTheme(AgentRowView row, AgentStatus agent)
    {
        var color = HeatPalette.SourceColor(agent.Source);
        row.Project.Foreground = _theme.Palette.PrimaryBrush;
        row.Detail.Foreground = _theme.Palette.SecondaryBrush;
        row.Phase.Foreground = _theme.Palette.SecondaryBrush;
        row.Rate.Foreground = _theme.Palette.PrimaryBrush;
        row.Dot.Background = new SolidColorBrush(Color.FromRgb(color.R8, color.G8, color.B8));
        row.PhasePill.Background = _theme.Palette.Brush(_theme.Palette.CardInset);
        row.PhasePill.BorderBrush = _theme.Palette.Brush(_theme.Palette.Border);
    }

    private void ApplyAgentTheme(AgentRowView row)
    {
        row.Project.Foreground = _theme.Palette.PrimaryBrush;
        row.Detail.Foreground = _theme.Palette.SecondaryBrush;
        row.Phase.Foreground = _theme.Palette.SecondaryBrush;
        row.Rate.Foreground = _theme.Palette.PrimaryBrush;
        row.PhasePill.Background = _theme.Palette.Brush(_theme.Palette.CardInset);
        row.PhasePill.BorderBrush = _theme.Palette.Brush(_theme.Palette.Border);
    }

    private static Color ToColor(Rgba color) => Color.FromRgb(color.R8, color.G8, color.B8);

    internal bool DashboardIsTwoColumn => _sectionsGrid.ColumnDefinitions.Count > 1;

    internal bool DashboardHasHorizontalOverflow =>
        _pageScroll.Extent.Width > _pageScroll.Viewport.Width + 1;

    private void RefreshDashboardText()
    {
        _dashboardTitle.Text = T("Dashboard");
        _dashboardSubtitle.Text = T("Live speed, usage and sources from local logs.");
        _sourcesHeading.Text = L("dashboard.sources");
        _sourcesHint.Text = T("Filter history by source");
        _agentsHeading.Text = T("Working agents");
        _chartTitle.Text = T("Live trend");
        _chartRange.Text = T("Last 2 minutes");
        _chartUnit.Text = T("per second");
        _outputLabel.Text = T("Today's output");
        _inputLabel.Text = T("Today's input");
        _requestsLabel.Text = T("Requests");
        _costLabel.Text = L("today.cost");
        _costTag.Text = L("cost.apiEquivalent");
        _peakLabel.Text = L("today.peak");
        _trafficLabel.Text = T("Machine network");
        _speedEyebrow.Text = T("Live speed");
        _otherSourcesHeader.Text = T("Other sources");
    }
}
