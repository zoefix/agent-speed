using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Modes;
using AgentSpeed.Interop;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Theme;

namespace AgentSpeed.History;

/// <summary>
/// The Usage History window (DESIGN 4b, DESIGN 9): summary cards, the daily chart of output or cost (or one day's
/// breakdown) and the daily table of one range.
/// </summary>
internal sealed partial class HistoryWindow : Window
{
    public static readonly DependencyProperty CardRowsProperty = DependencyProperty.Register(
        nameof(CardRows), typeof(IReadOnlyList<int>), typeof(HistoryWindow), new PropertyMetadata(null));

    private const string SortDescendingGlyph = "";
    private const string SortAscendingGlyph = "";

    /// <summary>The Fluent CheckBox's box (20 DIP) and the space before its content (8 DIP).</summary>
    private const double CheckBoxChrome = 28;

    private readonly HistoryViewModel _model;
    private readonly SmoothScroller _scroller;
    private readonly Dictionary<HistoryColumn, (Button Button, TextBlock Label, TextBlock Glyph)> _headers = new();
    private readonly MenuItem _copyItem = new();
    private bool _syncingRange;
    private bool _syncingDates;
    private bool _entered;
    private bool _syncingMetric;
    private bool _syncingModeKind;
    private SpringStackPanel? _modeRowsPanel;
    private ToolbarDensity _density = ToolbarDensity.Full;
    private bool? _subscriptionsOffered;
    private double _subscriptionsWidth;
    private double _pickerWidth = HistoryToolbar.PickerMinWidth;
    private int? _hoverIndex;
    private double _dividerTarget;

    public HistoryWindow(HistoryViewModel model, CapsuleAppearance appearance)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        _scroller = new SmoothScroller(PageScroller);
        if (AppIconSource.Frame(32) is { } icon)
        {
            Icon = icon;
        }

        BuildHeader();
        _copyItem.Click += (_, _) => Table.CopyCurrent();
        _copyItem.InputGestureText = "Ctrl+C";
        var menu = new ContextMenu { Items = { _copyItem } };
        menu.SetResourceReference(FontFamilyProperty, AppFonts.UiFontKey);
        Table.ContextMenu = menu;
        CustomFlyout.CustomPopupPlacementCallback = PlaceFlyout;

        model.Updated += OnModelUpdated;
        Chart.HoverChanged += OnChartHover;
        Chart.DayActivated += (_, day) => Table.Reveal(day, _scroller);
        Table.HoverDayChanged += (_, day) => Chart.LinkedDay = day;
        Table.SpansChanged += (_, _) =>
        {
            TableHeader.Spans = Table.Spans;
            var padding = new Thickness(Table.Padding, 0, Table.Padding, 0);
            foreach (var (column, header) in _headers)
            {
                header.Button.Visibility = Table.IsHidden(column) ? Visibility.Collapsed : Visibility.Visible;
                header.Button.Padding = padding;
            }
        };
        PageContent.SizeChanged += (_, _) => UpdateCardRows();
        Toolbar.SizeChanged += (_, _) => UpdateToolbar();
        ExportButton.SizeChanged += (_, _) => UpdateToolbar();
        RangeGroup.SizeChanged += (_, _) => UpdateToolbar();
        SourcePicker.SizeChanged += (_, _) => UpdateToolbar();
        SubscriptionsGroup.IsVisibleChanged += (_, _) => UpdateToolbar();
        SubscriptionsCheck.SizeChanged += (_, _) => UpdateToolbar();
        ContentRendered += OnContentRendered;
        PageScroller.ScrollChanged += (_, _) => UpdateScrollDivider();
        Chart.IsVisibleChanged += (_, _) =>
        {
            if (Chart.IsVisible && _entered)
            {
                // After the new data and the first layout at full width, so the bars grow from the baseline.
                Dispatcher.BeginInvoke(Chart.PlayEntrance, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
        IsVisibleChanged += (_, _) => UpdateShown();
        StateChanged += (_, _) => UpdateShown();
        BreakdownList.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnBreakdownClick), handledEventsToo: true);
        BreakdownList.PreviewKeyDown += OnBreakdownKeyDown;

        ApplyTheme(appearance);
        Relocalize();
        Apply(HistoryUpdate.Filter, ChartTransition.Snap);
    }

    /// <summary>The space between the source picker and the include-subscriptions checkbox.</summary>
    public static Thickness SubscriptionsMargin { get; } = new(HistoryToolbar.SubscriptionsGap(ToolbarDensity.Full), 0, 0, 0);

    /// <summary>How many summary cards each row holds (DESIGN 9: five in one row when wide, else 3 + 2).</summary>
    public IReadOnlyList<int>? CardRows
    {
        get => (IReadOnlyList<int>?)GetValue(CardRowsProperty);
        set => SetValue(CardRowsProperty, value);
    }

    public bool ReduceMotion { get; private set; }

    /// <param name="appearance">Motion and contrast, and how the effort and speed glyphs draw.</param>
    public void ApplyTheme(CapsuleAppearance appearance)
    {
        var (reduceMotion, highContrast) = (appearance.ReduceMotion, appearance.HighContrast);
        ReduceMotion = reduceMotion;
        _model.SetModeAppearance(appearance);
        _model.SetPerfAppearance(appearance.Dark, appearance.HighContrast, appearance.ReduceMotion);
        if (_modeRowsPanel is { } rows)
        {
            rows.ReduceMotion = reduceMotion;
        }

        BreakdownBar.SetReduceMotion(ModeList, reduceMotion);
        BreakdownBar.SetHighContrast(ModeList, highContrast);
        Chart.ReduceMotion = reduceMotion;
        Chart.HighContrast = highContrast;
        Table.ReduceMotion = reduceMotion;
        BreakdownBar.SetReduceMotion(BreakdownList, reduceMotion);
        BreakdownBar.SetHighContrast(BreakdownList, highContrast);
    }

    /// <summary>Re-reads every text that is not bound to the view model (the view model relocalizes itself).</summary>
    public void Relocalize()
    {
        var loc = _model.Loc;
        _copyItem.Header = loc["win.history.copyRow"];
        _subscriptionsWidth = 0;
        UpdateHeader();
        Dispatcher.BeginInvoke(UpdateToolbar, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Moves the window, before its first show, to <paramref name="saved"/> or centres it on the cursor's display.</summary>
    public void RestoreFrame(WindowFrame? saved)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var offset = WorkspaceOffset();
        RECT rect;
        if (WindowFrame.ValidHistory(saved) is { } frame && IsOnScreen(frame, offset))
        {
            rect = new RECT((int)frame.Left, (int)frame.Top, (int)(frame.Left + frame.Width), (int)(frame.Top + frame.Height));
        }
        else
        {
            rect = Centered(offset);
        }

        WindowPlacement.TrySetHidden(hwnd, rect);
        if (saved is { Maximized: true })
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>The normal frame (physical pixels, workspace coordinates) and whether the window is maximized.</summary>
    public WindowFrame? CaptureFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return WindowPlacement.TryGet(hwnd, out var rect, out var maximized)
            ? WindowFrame.ValidHistory(new WindowFrame(rect.Left, rect.Top, rect.Width, rect.Height, maximized))
            : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.Updated -= OnModelUpdated;
        _scroller.Stop();
        base.OnClosed(e);
    }

    // ---- Data ----

    private void OnModelUpdated(object? sender, HistoryUpdate update) => Apply(update, update switch
    {
        HistoryUpdate.Filter => ChartTransition.Morph,
        HistoryUpdate.Live => ChartTransition.Live,
        _ => ChartTransition.Snap,
    });

    private void Apply(HistoryUpdate update, ChartTransition transition)
    {
        var loc = _model.Loc;
        var money = _model.Money;
        if (update != HistoryUpdate.Sort)
        {
            var today = _model.Today;
            Func<double, double, string> axis = _model.Bars.Metric switch
            {
                HistoryChartMetric.Cost => money.AxisLabel,
                HistoryChartMetric.Traffic => static (value, _) => HistoryTrafficText.AxisBytes(value),
                _ => ChartTexts.TokenAxis,
            };
            var range = _model.Range;
            var texts = new ChartTexts(loc.Culture, loc["unit.tokS"], loc["history.inProgress"],
                (day, withYear) => HistoryText.Month(day, withYear, loc), day => HistoryText.MonthDay(day, loc),
                day => HistoryText.Accessible(day, today, loc, money), axis)
            {
                NotRecorded = loc["history.traffic.notRecorded"],
                TrafficAccessible = index => HistoryTooltipText.AccessibleTraffic(range.DayAt(index), _model.TrafficAt(range.DayAt(index)), today, loc),
            };
            Chart.SetData(_model.Bars, _model.DayAt, _model.Source, today, texts, transition);
            SyncRange();
            SyncMetric();
            SyncModeKind();
        }

        if (update == HistoryUpdate.Text)
        {
            Relocalize();
        }

        Table.SetRows(_model.Rows, _model.Today, loc, money, _model.ShowsTraffic);
        if (!_model.HasUnpriced && UnpricedFlyout.IsOpen)
        {
            // The requests were priced meanwhile: the popover (and its link) is gone.
            UnpricedFlyout.IsOpen = false;
        }
        UpdateHeader();
        if (update == HistoryUpdate.Filter)
        {
            _hoverIndex = null;
            HoverCard.Hide();
        }
        else
        {
            ShowHoverCard();
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        if (_entered)
        {
            return;
        }

        _entered = true;
        UpdateCardRows();
        UpdateToolbar();
        Chart.PlayEntrance();
    }

    // ---- Metric ----

    private void SyncMetric()
    {
        _syncingMetric = true;
        try
        {
            MetricOutput.IsChecked = _model.Metric == HistoryChartMetric.Output;
            MetricCost.IsChecked = _model.Metric == HistoryChartMetric.Cost;
            MetricTraffic.IsChecked = _model.Metric == HistoryChartMetric.Traffic;
        }
        finally
        {
            _syncingMetric = false;
        }
    }

    private void OnMetricChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingMetric || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<HistoryChartMetric>(tag, out var metric))
        {
            return;
        }

        _model.SetMetric(metric);
    }

    // ---- Effort and speed ----

    private void SyncModeKind()
    {
        _syncingModeKind = true;
        try
        {
            ModeEffort.IsChecked = _model.ModeKind == ExecutionModeKind.Effort;
            ModeSpeed.IsChecked = _model.ModeKind == ExecutionModeKind.Speed;
        }
        finally
        {
            _syncingModeKind = false;
        }
    }

    private void OnModeKindChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingModeKind || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<ExecutionModeKind>(tag, out var kind))
        {
            return;
        }

        _model.SetModeKind(kind);
    }

    private void OnModeRowsPanelLoaded(object sender, RoutedEventArgs e)
    {
        _modeRowsPanel = (SpringStackPanel)sender;
        _modeRowsPanel.ReduceMotion = ReduceMotion;
    }

    private void OnExportModes(object sender, RoutedEventArgs e) => SaveExport(_model.ExportModesCsv());

    // ---- Unknown prices ----

    private void OnShowUnpriced(object sender, RoutedEventArgs e)
    {
        if (UnpricedFlyout.IsOpen)
        {
            UnpricedFlyout.IsOpen = false;
            return;
        }

        UnpricedContent.ReduceMotion = ReduceMotion;
        UnpricedFlyout.IsOpen = true;
        Dispatcher.BeginInvoke(UnpricedContent.FocusFirst, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnUnpricedClosed(object? sender, EventArgs e)
    {
        UnpricedContent.Reset();
        if (IsActive && !IsKeyboardFocusWithin && UnpricedButton.IsVisible)
        {
            UnpricedButton.Focus();
        }
    }

    private void OnUnpricedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            UnpricedFlyout.IsOpen = false;
            UnpricedButton.Focus();
            e.Handled = true;
        }
    }

    // ---- Range ----

    private void SyncRange()
    {
        _syncingRange = true;
        try
        {
            var kind = _model.RangeKind;
            foreach (var radio in new[] { RangeToday, RangeYesterday, Range7, Range30, Range90, RangeYear, RangeAll })
            {
                radio.IsChecked = radio.Tag is string tag && Enum.TryParse<HistoryRangeKind>(tag, out var k) && k == kind;
            }

            CustomToggle.IsChecked = kind == HistoryRangeKind.Custom;
        }
        finally
        {
            _syncingRange = false;
        }
    }

    private void OnRangeChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingRange || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<HistoryRangeKind>(tag, out var kind))
        {
            return;
        }

        _model.SetRange(kind);
    }

    private void OnCustomClick(object sender, RoutedEventArgs e)
    {
        CustomToggle.IsChecked = _model.RangeKind == HistoryRangeKind.Custom;
        if (CustomFlyout.IsOpen)
        {
            CustomFlyout.IsOpen = false;
            return;
        }

        PrepareDates();
        CustomFlyout.IsOpen = true;
        Dispatcher.BeginInvoke(() => FromPicker.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PrepareDates()
    {
        var loc = _model.Loc;
        var today = _model.Today;
        var first = _model.DataBounds is { } bounds && bounds.Start < today ? bounds.Start : today;
        var range = _model.Range;
        var start = range.Start < first ? first : range.Start;
        var end = range.End > today ? today : range.End;
        _syncingDates = true;
        try
        {
            foreach (var picker in new[] { FromPicker, ToPicker })
            {
                picker.SelectedDate = null;
                picker.DisplayDateStart = first.ToDateTime(TimeOnly.MinValue);
                picker.DisplayDateEnd = today.ToDateTime(TimeOnly.MinValue);
                picker.FirstDayOfWeek = loc.Culture.DateTimeFormat.FirstDayOfWeek;
                picker.Language = Language;
            }

            FromPicker.SelectedDate = start.ToDateTime(TimeOnly.MinValue);
            ToPicker.SelectedDate = (end < start ? start : end).ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _syncingDates = false;
        }
    }

    private void OnFromChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingDates || FromPicker.SelectedDate is not { } from || ToPicker.SelectedDate is not { } to || to >= from)
        {
            return;
        }

        _syncingDates = true;
        ToPicker.SelectedDate = from;
        _syncingDates = false;
    }

    private void OnToChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingDates || FromPicker.SelectedDate is not { } from || ToPicker.SelectedDate is not { } to || from <= to)
        {
            return;
        }

        _syncingDates = true;
        FromPicker.SelectedDate = to;
        _syncingDates = false;
    }

    private void OnApplyCustom(object sender, RoutedEventArgs e)
    {
        var range = _model.Range;
        var from = FromPicker.SelectedDate is { } f ? DateOnly.FromDateTime(f) : range.Start;
        var to = ToPicker.SelectedDate is { } t ? DateOnly.FromDateTime(t) : range.End;
        CustomFlyout.IsOpen = false;
        _model.ApplyCustom(from, to);
    }

    private void OnCustomClosed(object? sender, EventArgs e)
    {
        CustomToggle.IsChecked = _model.RangeKind == HistoryRangeKind.Custom;
        if (IsActive && !IsKeyboardFocusWithin)
        {
            CustomToggle.Focus();
        }
    }

    private void OnFlyoutKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CustomFlyout.IsOpen = false;
            CustomToggle.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Centred under the custom item but kept inside the content column (sizes arrive in device pixels; the card has a
    /// 12 DIP shadow margin around it).
    /// </summary>
    private CustomPopupPlacement[] PlaceFlyout(Size popupSize, Size targetSize, Point offset)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var target = CustomToggle.TranslatePoint(new Point(0, 0), Root).X * scale;
        var shadow = 12 * scale;
        var min = 24 * scale - shadow - target;
        var max = (Root.ActualWidth - 24) * scale + shadow - popupSize.Width - target;
        var x = Math.Clamp((targetSize.Width - popupSize.Width) / 2, Math.Min(min, max), max);
        return [new CustomPopupPlacement(new Point(x, targetSize.Height - 4 * scale), PopupPrimaryAxis.Horizontal)];
    }

    /// <summary>Today is re-read only while the window is on screen.</summary>
    private void UpdateShown() => _model.SetShown(IsVisible && WindowState != WindowState.Minimized);

    // ---- Breakdown ----

    private void OnBreakdownClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(BreakdownList, source) is ListBoxItem { DataContext: BreakdownRowView row })
        {
            ShowModelsOf(row, keyboard: false);
        }
    }

    private void OnBreakdownKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && Keyboard.FocusedElement is ListBoxItem { DataContext: BreakdownRowView row } && row.IsSource)
        {
            ShowModelsOf(row, keyboard: true);
            e.Handled = true;
        }
    }

    /// <summary>A source bar opens that source's models for the same day; keyboard focus moves to the first model bar.</summary>
    private void ShowModelsOf(BreakdownRowView row, bool keyboard)
    {
        if (!row.IsSource)
        {
            return;
        }

        // Rebuilding the list inside its own input event would pull the item out from under the event route.
        var source = row.Source;
        Dispatcher.BeginInvoke(() =>
        {
            _model.SetSource(source);
            if (keyboard)
            {
                Dispatcher.BeginInvoke(FocusFirstBreakdownBar, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusFirstBreakdownBar()
    {
        if (BreakdownList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
        {
            first.Focus();
        }
        else
        {
            SourcePicker.Focus();
        }
    }

    // ---- Export ----

    private void OnExport(object sender, RoutedEventArgs e) => SaveExport(_model.ExportCsv());

    private void SaveExport((string FileName, byte[] Bytes) export)
    {
        var loc = _model.Loc;
        var (fileName, bytes) = export;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = fileName,
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = $"{loc["win.history.csvFilter"]} (*.csv)|*.csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Exporting the usage history failed.", ex);
            _model.ShowMessage(loc["win.history.export.failed"]);
        }
    }

    // ---- Table header ----

    private void BuildHeader()
    {
        foreach (var column in HistoryTable.Columns)
        {
            var numeric = HistoryTable.IsNumeric(column);
            var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var glyph = new TextBlock
            {
                FontSize = 9,
                Width = DailyTable.SortGlyphRoom - 4,
                TextAlignment = numeric ? TextAlignment.Left : TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
                Visibility = Visibility.Hidden,
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, AppFonts.SymbolFontKey);
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(glyph, numeric ? Dock.Left : Dock.Right);
            dock.Children.Add(glyph);
            dock.Children.Add(label);
            var button = new Button
            {
                Style = (Style)FindResource("HeaderButton"),
                Content = dock,
                Padding = new Thickness(DailyTable.CellPadding, 0, DailyTable.CellPadding, 0),
                HorizontalContentAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            button.Click += (_, _) => _model.SortBy(column);
            TableHeader.Children.Add(button);
            _headers[column] = (button, label, glyph);
        }
    }

    private void UpdateHeader()
    {
        var loc = _model.Loc;
        foreach (var (column, (button, label, glyph)) in _headers)
        {
            var text = loc[HistoryTable.HeaderKey(column)];
            label.Text = text;
            var active = column == _model.SortColumn;
            glyph.Text = _model.SortDescending ? SortDescendingGlyph : SortAscendingGlyph;
            glyph.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            if (active)
            {
                button.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
            }
            else
            {
                button.ClearValue(ForegroundProperty);
            }

            System.Windows.Automation.AutomationProperties.SetName(button, text);
            System.Windows.Automation.AutomationProperties.SetHelpText(button,
                active ? loc[_model.SortDescending ? "win.history.sorted.desc" : "win.history.sorted.asc"] : string.Empty);
            button.ToolTip = label.Text;
        }
    }

    // ---- Hover card ----

    private void OnChartHover(object? sender, ChartHover hover)
    {
        _hoverIndex = hover.Index;
        Table.LinkedDay = hover.Index is { } index && _model.DayAt(index) is { } day ? day.Day : null;
        ShowHoverCard();
    }

    private void ShowHoverCard()
    {
        var traffic = _model.Bars.Metric == HistoryChartMetric.Traffic;
        if (_hoverIndex is not { } index || _model.IsEmpty || !Chart.IsVisible || index >= _model.Range.DayCount
            || (traffic ? !_model.Bars.HasData(index) : _model.DayAt(index) is null))
        {
            HoverCard.Hide();
            return;
        }

        var slot = Chart.SlotBounds(index);
        if (slot.IsEmpty)
        {
            HoverCard.Hide();
            return;
        }

        var model = traffic ? TrafficTooltipFor(_model.Range.DayAt(index)) : TooltipFor(_model.DayAt(index)!);
        var size = HoverCard.MeasureCard(model);
        var chart = Chart.TranslatePoint(new Point(0, 0), Overlay);
        var card = ChartCard.TranslatePoint(new Point(0, 0), Overlay);
        var boundsLeft = card.X - 10;
        var boundsWidth = ChartCard.ActualWidth + 20;
        var plot = Chart.Plot;
        var (x, y) = HistoryLayout.TooltipPosition(chart.X + slot.Left - boundsLeft, chart.X + slot.Right - boundsLeft,
            chart.Y + plot.Top + plot.Height / 2, size.Width, size.Height, boundsWidth, Math.Max(Overlay.ActualHeight, size.Height), gap: 2);
        HoverCard.Show(model, x + boundsLeft, y, ReduceMotion);
    }

    private TooltipModel TooltipFor(DaySummary day) => ToTooltip(
        HistoryTooltipText.Build(day, _model.Bars.Order, _model.Source is null, _model.Bars.Metric, _model.Today, _model.Money, _model.Loc));

    private TooltipModel TrafficTooltipFor(DateOnly day) =>
        ToTooltip(HistoryTooltipText.BuildTraffic(day, _model.TrafficAt(day), _model.Today, _model.Loc));

    private static TooltipModel ToTooltip(TooltipContent content)
    {
        var lines = content.Rows
            .Select(r => new TooltipLine(r.Label, r.Value, r.Source is { } source ? HeatPalette.SourceColor(source).ToBrush() : null,
                r.Strong, Indented: r.Source is not null, r.IsNote))
            .ToArray();
        var models = content.Models.Select(m => new TooltipShare(m.Name, m.Share, m.Percent, m.Speed, m.Cost)).ToArray();
        return new TooltipModel(content.Title, content.Badge, lines, content.Note, content.ModelsTitle, models);
    }

    // ---- Responsive layout ----

    private void UpdateScrollDivider()
    {
        var target = PageScroller.VerticalOffset > 0.5 ? 1.0 : 0.0;
        if (Math.Abs(ScrollDivider.Opacity - target) < 0.01 && !ScrollDivider.HasAnimatedProperties)
        {
            return;
        }

        if (_dividerTarget == target)
        {
            return;
        }

        _dividerTarget = target;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(ReduceMotion ? 0 : 200))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        ScrollDivider.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateCardRows()
    {
        var rows = HistoryLayout.CardRows(_model.Cards.Count, PageContent.ActualWidth);
        if (CardRows is { } shown && shown.SequenceEqual(rows))
        {
            return;
        }

        CardRows = rows;
        var index = 0;
        foreach (var count in rows)
        {
            for (var i = 0; i < count && index < _model.Cards.Count; i++, index++)
            {
                _model.Cards[index].SideBySide = count < 3;
            }
        }
    }

    /// <summary>
    /// Narrows the range items, the export button, the subscriptions checkbox and the picker step by step so the toolbar
    /// never wraps.
    /// </summary>
    private void UpdateToolbar()
    {
        if (Toolbar.ActualWidth <= 0 || ExportButton.ActualWidth <= 0)
        {
            return;
        }

        var exportLabel = TextWidth(ExportLabel.Text, ExportLabel.FontFamily, ExportLabel.FontSize, ExportLabel.FontWeight);
        var exportIcon = ExportLabel.Visibility == Visibility.Visible
            ? ExportButton.ActualWidth - ExportLabel.ActualWidth - ExportLabel.Margin.Left
            : ExportButton.ActualWidth;
        if (!HistoryToolbar.NarrowsPicker(_density))
        {
            _pickerWidth = SourcePicker.ActualWidth;
        }

        var offered = _model.OffersSubscriptions;
        // The checkbox as last drawn (kept while it is an icon, so the fit never flips back and forth), else estimated.
        if (SubscriptionsCheck.IsVisible && SubscriptionsCheck.ActualWidth > 0)
        {
            _subscriptionsWidth = SubscriptionsCheck.ActualWidth;
        }

        var subscriptionsLabel = !offered ? 0
            : _subscriptionsWidth > 0 ? _subscriptionsWidth
            : TextWidth(SubscriptionsLabel.Text, SubscriptionsLabel.FontFamily, SubscriptionsLabel.FontSize, FontWeights.Normal)
              + SubscriptionsLabel.Margin.Left + CheckBoxChrome;
        var subscriptionsIcon = offered ? HistoryToolbar.SubscriptionsIconWidth : 0;
        var items = RangeItems.Children.OfType<ToggleButton>().ToArray();
        var labels = items.Select(item => TextWidth(item.Content as string ?? string.Empty, item.FontFamily, item.FontSize, FontWeights.SemiBold))
            .ToArray();
        var density = HistoryToolbar.Fit(Toolbar.ActualWidth, labels, _pickerWidth, exportIcon, exportLabel, subscriptionsLabel,
            subscriptionsIcon);
        if (density == _density && offered == _subscriptionsOffered)
        {
            return;
        }

        _density = density;
        _subscriptionsOffered = offered;
        var labelled = HistoryToolbar.ShowsSubscriptionsLabel(density);
        SubscriptionsCheck.Visibility = labelled ? Visibility.Visible : Visibility.Collapsed;
        SubscriptionsToggle.Visibility = labelled ? Visibility.Collapsed : Visibility.Visible;
        SubscriptionsToggle.Width = HistoryToolbar.SubscriptionsIcon(density, HistoryToolbar.SubscriptionsIconWidth);
        SubscriptionsGroup.Margin = new Thickness(HistoryToolbar.SubscriptionsGap(density), 0, 0, 0);
        ExportLabel.Visibility = HistoryToolbar.ShowsExportLabel(density) ? Visibility.Visible : Visibility.Collapsed;
        var padding = HistoryToolbar.ItemPadding(density);
        foreach (var item in items)
        {
            item.Padding = new Thickness(padding, 0, padding, 0);
            item.MinWidth = HistoryToolbar.ItemMinWidth(density);
        }

        var margin = HistoryToolbar.RangeMargin(density);
        RangeGroup.Margin = new Thickness(margin, 0, margin, 0);
        var narrow = HistoryToolbar.NarrowsPicker(density);
        SourcePicker.MinWidth = narrow ? HistoryToolbar.CompactPickerWidth : HistoryToolbar.PickerMinWidth;
        SourcePicker.MaxWidth = narrow ? HistoryToolbar.CompactPickerWidth : HistoryToolbar.PickerMaxWidth;
    }

    private double TextWidth(string text, FontFamily family, double size, FontWeight weight) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    // ---- Frame ----

    private static (int X, int Y) WorkspaceOffset() =>
        Monitors.Primary() is { } primary ? (primary.WorkArea.Left - primary.Bounds.Left, primary.WorkArea.Top - primary.Bounds.Top) : (0, 0);

    private static bool IsOnScreen(WindowFrame frame, (int X, int Y) offset)
    {
        var left = frame.Left + offset.X;
        var top = frame.Top + offset.Y;
        foreach (var monitor in Monitors.All())
        {
            var work = monitor.WorkArea;
            var overlapX = Math.Min(left + frame.Width, work.Right) - Math.Max(left, work.Left);
            var overlapY = Math.Min(top + frame.Height, work.Bottom) - Math.Max(top, work.Top);
            if (overlapX >= 160 && overlapY >= 80)
            {
                return true;
            }
        }

        return false;
    }

    private RECT Centered((int X, int Y) offset)
    {
        var monitor = (User32.GetCursorPos(out var cursor) ? Monitors.FromPoint(cursor) : null) ?? Monitors.Primary();
        if (monitor is null)
        {
            return new RECT(100, 100, 100 + (int)Width, 100 + (int)Height);
        }

        var work = monitor.WorkArea;
        var margin = (int)Math.Round(24 * monitor.Scale);
        var width = Math.Min((int)Math.Round(Width * monitor.Scale), work.Width - 2 * margin);
        var height = Math.Min((int)Math.Round(Height * monitor.Scale), work.Height - 2 * margin);
        width = Math.Max(width, (int)Math.Round(MinWidth * monitor.Scale));
        height = Math.Max(height, (int)Math.Round(MinHeight * monitor.Scale));
        var left = work.Left + (work.Width - width) / 2 - offset.X;
        var top = work.Top + (work.Height - height) / 2 - offset.Y;
        return new RECT(left, top, left + width, top + height);
    }
}
