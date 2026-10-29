using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Theme;

namespace AgentSpeed.History.Perf;

/// <summary>
/// The selected row's daily trend (DESIGN 12, <i>Trend</i>): one slot per day with the daily chart's month rules and labels,
/// a log y scale from 250 to the chart's top with gridlines at every doubling from 500 and the 1000 line stronger, the
/// pooled score of each day with enough data as a 2 DIP line over a soft area in the row's source color (gaps break it), a
/// dot per day with the latest larger, and the usual line dashed. Hover — or the arrow keys while it has focus — shows a
/// crosshair and the day's card. An image for screen readers, named with the series' lowest, highest and latest scores.
/// </summary>
internal sealed class PerfTrendChart : FrameworkElement
{
    public const double MonthBand = 16;
    public const double DayBand = 18;
    public const double AxisGap = 6;

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(PerfTrendChart),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty TertiaryProperty = DependencyProperty.Register(
        "Tertiary", typeof(Brush), typeof(PerfTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        "Secondary", typeof(Brush), typeof(PerfTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty GridProperty = DependencyProperty.Register(
        "Grid", typeof(Brush), typeof(PerfTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Popup _tip;
    private readonly TextBlock _tipDate = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _tipScore = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _tipLine = new() { FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
    private PerfHistoryChart? _chart;
    private Color _source = Colors.Gray;
    private Localizer? _loc;
    private DateOnly _today;
    private int? _focus;

    public PerfTrendChart()
    {
        Focusable = true;
        FocusVisualStyle = null;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        SnapsToDevicePixels = true;
        ClipToBounds = false;
        SetResourceReference(TertiaryProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(SecondaryProperty, "TextFillColorSecondaryBrush");
        SetResourceReference(GridProperty, "DividerStrokeColorDefaultBrush");
        _tipScore.SetResourceReference(TextBlock.FontFamilyProperty, AppFonts.DisplayFontKey);
        Typography.SetNumeralAlignment(_tipScore, FontNumeralAlignment.Tabular);
        _tipLine.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var card = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(12, 9, 12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { _tipDate, _tipScore, _tipLine } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.18 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorderBrush");
        card.SetResourceReference(TextElement.FontFamilyProperty, AppFonts.UiFontKey);
        card.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        _tip = new Popup
        {
            Child = card,
            AllowsTransparency = true,
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            IsHitTestVisible = false,
            Focusable = false,
        };
        Unloaded += (_, _) => _tip.IsOpen = false;
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>Shows <paramref name="chart"/> for a row of <paramref name="source"/>'s color; a new series fades in (0.2 s).</summary>
    public void Apply(PerfHistoryChart? chart, Color source, Localizer loc, DateOnly today, string spoken)
    {
        var changed = !ReferenceEquals(chart, _chart) && _chart is not null;
        _chart = chart;
        _source = source;
        _loc = loc;
        _today = today;
        AutomationProperties.SetName(this, spoken);
        if (_focus is { } focus && (chart is null || focus >= chart.Days.Count))
        {
            _focus = null;
            _tip.IsOpen = false;
        }

        InvalidateVisual();
        if (changed && IsLoaded && IsVisible)
        {
            BeginAnimation(OpacityProperty, PerfHistoryMotion.FadeTo(0.35, 1));
        }

        if (_focus is { } shown)
        {
            ShowTip(shown);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));
        if (width <= 0 || height <= 0 || _chart is not { } chart)
        {
            return;
        }

        var plot = Plot(chart);
        var slot = chart.Days.Count > 0 ? plot.Width / chart.Days.Count : 0;
        var highContrast = PerfHistoryAppearance.GetHighContrast(this);
        var grid = highContrast ? SystemColors.GrayTextBrush : (Brush?)GetValue(GridProperty) ?? Brushes.LightGray;
        var muted = highContrast ? SystemColors.GrayTextBrush : (Brush?)GetValue(TertiaryProperty) ?? Brushes.Gray;
        var gridPen = MediaColors.FrozenPen(grid, 1);
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (chart.Highlighted is { } day && Index(chart, day) is { } highlighted && slot > 0)
        {
            var tint = PerfHistoryAppearance.Brush(PerfHistoryAppearance.Label(this, PerfHistoryLayout.DayTint));
            dc.DrawRectangle(tint, null, new Rect(plot.Left + highlighted * slot, plot.Top, slot, plot.Height));
        }

        foreach (var value in chart.Gridlines)
        {
            var y = Snap(plot.Bottom - chart.Y(value) * plot.Height, ppd);
            var reference = value == (int)PerfScale.Reference;
            var pen = reference
                ? MediaColors.FrozenPen(PerfHistoryAppearance.Brush(PerfHistoryAppearance.Label(this, PerfHistoryLayout.ReferenceOpacity)), 1)
                : gridPen;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(PerfNumber.Full(value), muted, PerfHistoryLayout.AxisSize, FontWeights.Normal);
            dc.DrawText(label, new Point(plot.Left - AxisGap - label.WidthIncludingTrailingWhitespace, y - label.Height / 2));
        }

        DrawMonths(dc, chart, plot, slot, gridPen, ppd, muted);
        if (chart.IsEmpty)
        {
            var empty = Text(AutomationProperties.GetName(this), muted, 12, FontWeights.Normal);
            empty.MaxTextWidth = Math.Max(40, plot.Width - 24);
            empty.TextAlignment = TextAlignment.Center;
            dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.MaxTextWidth) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            return;
        }

        DrawSeries(dc, chart, plot, slot, highContrast);
        if (_focus is { } focus && focus < chart.Days.Count)
        {
            var x = Snap(plot.Left + (focus + 0.5) * slot, ppd);
            var cross = MediaColors.FrozenPen(PerfHistoryAppearance.Brush(PerfHistoryAppearance.Label(this, 0.28)), 1);
            dc.DrawLine(cross, new Point(x, plot.Top), new Point(x, plot.Bottom));
            DrawDots(dc, chart, plot, slot, highContrast, only: focus);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_chart is not { } chart || chart.Days.Count == 0)
        {
            return;
        }

        var plot = Plot(chart);
        var x = e.GetPosition(this).X;
        if (x < plot.Left - 4 || x > plot.Right + 4)
        {
            Unfocus();
            return;
        }

        Focus(Math.Clamp((int)((x - plot.Left) / (plot.Width / chart.Days.Count)), 0, chart.Days.Count - 1));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsKeyboardFocused)
        {
            Unfocus();
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Unfocus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_chart is not { } chart || chart.Days.Count == 0)
        {
            return;
        }

        var last = chart.Days.Count - 1;
        int? next = e.Key switch
        {
            Key.Left => Math.Max(0, (_focus ?? chart.LatestIndex + 1) - 1),
            Key.Right => Math.Min(last, (_focus ?? chart.LatestIndex - 1) + 1),
            Key.Home => 0,
            Key.End => last,
            _ => null,
        };
        if (e.Key == Key.Escape && _focus is not null)
        {
            Unfocus();
            e.Handled = true;
        }
        else if (next is { } index)
        {
            Focus(index);
            e.Handled = true;
        }
    }

    private static double Snap(double value, double ppd) => (Math.Round(value * ppd) + 0.5) / ppd;

    private static int? Index(PerfHistoryChart chart, DateOnly day)
    {
        for (var i = 0; i < chart.Days.Count; i++)
        {
            if (chart.Days[i].Day == day)
            {
                return i;
            }
        }

        return null;
    }

    private Rect Plot(PerfHistoryChart chart)
    {
        var axis = chart.Gridlines.Select(v => Text(PerfNumber.Full(v), Brushes.Black, PerfHistoryLayout.AxisSize, FontWeights.Normal).WidthIncludingTrailingWhitespace)
            .DefaultIfEmpty(0).Max();
        var left = Math.Ceiling(axis + AxisGap);
        return new Rect(left, MonthBand, Math.Max(0, ActualWidth - left - 4), Math.Max(0, ActualHeight - MonthBand - DayBand));
    }

    private void DrawMonths(DrawingContext dc, PerfHistoryChart chart, Rect plot, double slot, Pen gridPen, double ppd, Brush muted)
    {
        if (slot <= 0 || _loc is not { } loc)
        {
            return;
        }

        var range = new DayRange(chart.Days[0].Day, chart.Days[^1].Day);
        var marks = HistoryChartMath.Marks(range, slot);
        foreach (var index in marks.Rules)
        {
            var x = Snap(plot.Left + index * slot, ppd);
            dc.PushOpacity(0.75);
            dc.DrawLine(gridPen, new Point(x, plot.Top - 4), new Point(x, plot.Bottom));
            dc.Pop();
        }

        var label = PerfHistoryAppearance.GetHighContrast(this) ? SystemColors.WindowTextBrush : (Brush?)GetValue(SecondaryProperty) ?? Brushes.Gray;
        foreach (var mark in marks.Months)
        {
            var withYear = mark.Kind == AxisLabelKind.Year || (mark.Index == 0 && mark.Day.Year != _today.Year);
            var text = Text(HistoryText.Month(mark.Day, withYear, loc), label, PerfHistoryLayout.AxisSize, FontWeights.SemiBold);
            dc.DrawText(text, new Point(plot.Left + mark.Index * slot + (mark.Index == 0 ? 0 : 4), 0));
        }

        var lastRight = double.NegativeInfinity;
        foreach (var mark in marks.Days)
        {
            var isToday = mark.Day == _today;
            var wide = slot >= 44;
            var text = Text(wide ? HistoryText.MonthDay(mark.Day, loc) : mark.Day.Day.ToString(loc.Culture), isToday ? label : muted,
                PerfHistoryLayout.AxisSize, isToday ? FontWeights.SemiBold : FontWeights.Normal);
            var width = text.WidthIncludingTrailingWhitespace;
            var x = Math.Clamp(plot.Left + (mark.Index + 0.5) * slot - width / 2, 0, Math.Max(0, ActualWidth - width));
            if (x < lastRight + 4)
            {
                continue;
            }

            dc.DrawText(text, new Point(x, plot.Bottom + 4));
            lastRight = x + width;
        }
    }

    private Color SeriesColor(bool highContrast) => highContrast ? SystemColors.HighlightColor : _source;

    private void DrawSeries(DrawingContext dc, PerfHistoryChart chart, Rect plot, double slot, bool highContrast)
    {
        var color = SeriesColor(highContrast);
        Point At(int i) => new(plot.Left + (i + 0.5) * slot, plot.Bottom - chart.Y(chart.Days[i].Score ?? 0) * plot.Height);

        if (chart.Usual is { } usual)
        {
            var y = plot.Bottom - chart.Y(usual) * plot.Height;
            var pen = new Pen(PerfHistoryAppearance.Brush(PerfHistoryAppearance.WithOpacity(color, PerfHistoryLayout.UsualOpacity)), 1)
            {
                DashStyle = new DashStyle([4, 3], 0),
            };
            pen.Freeze();
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        // Runs of consecutive days with a score: a gap breaks the line, a day alone is its dot only.
        var runs = new List<List<int>>();
        for (var i = 0; i < chart.Days.Count; i++)
        {
            if (chart.Days[i].Score is null)
            {
                continue;
            }

            if (runs.Count == 0 || runs[^1][^1] != i - 1)
            {
                runs.Add([]);
            }

            runs[^1].Add(i);
        }

        var area = new LinearGradientBrush(PerfHistoryAppearance.WithOpacity(color, highContrast ? 0 : PerfHistoryLayout.AreaOpacity),
            PerfHistoryAppearance.WithOpacity(color, 0), 90);
        area.Freeze();
        var line = new Pen(PerfHistoryAppearance.Brush(color), PerfHistoryLayout.LineThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        line.Freeze();

        // The dots keep a ring of the chart's background: the line stops short of each dot.
        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var i = 0; i < chart.Days.Count; i++)
        {
            if (chart.Days[i].Score is not null)
            {
                var r = DotDiameter(chart, i) / 2 + PerfHistoryLayout.DotRing;
                holes.Children.Add(new EllipseGeometry(At(i), r, r));
            }
        }

        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-10, -10, ActualWidth + 20, ActualHeight + 20)), holes);
        clip.Freeze();
        foreach (var run in runs.Where(r => r.Count > 1))
        {
            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(At(run[0]).X, plot.Bottom), true, true);
                foreach (var i in run)
                {
                    ctx.LineTo(At(i), false, true);
                }

                ctx.LineTo(new Point(At(run[^1]).X, plot.Bottom), false, false);
            }

            fill.Freeze();
            dc.DrawGeometry(area, null, fill);

            var path = new StreamGeometry();
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(At(run[0]), false, false);
                foreach (var i in run.Skip(1))
                {
                    ctx.LineTo(At(i), true, true);
                }
            }

            path.Freeze();
            dc.PushClip(clip);
            dc.DrawGeometry(null, line, path);
            dc.Pop();
        }

        DrawDots(dc, chart, plot, slot, highContrast, only: null);
    }

    private void DrawDots(DrawingContext dc, PerfHistoryChart chart, Rect plot, double slot, bool highContrast, int? only)
    {
        var brush = PerfHistoryAppearance.Brush(SeriesColor(highContrast));
        for (var i = 0; i < chart.Days.Count; i++)
        {
            if (chart.Days[i].Score is not { } score || (only is { } o && o != i))
            {
                continue;
            }

            var center = new Point(plot.Left + (i + 0.5) * slot, plot.Bottom - chart.Y(score) * plot.Height);
            var r = DotDiameter(chart, i) / 2 + (only is null ? 0 : 1);
            dc.DrawEllipse(brush, null, center, r, r);
        }
    }

    private static double DotDiameter(PerfHistoryChart chart, int index) =>
        index == chart.LatestIndex || (chart.Highlighted is { } day && chart.Days[index].Day == day) ? PerfHistoryLayout.LatestDot : PerfHistoryLayout.Dot;

    private void Focus(int index)
    {
        if (_focus == index && _tip.IsOpen)
        {
            return;
        }

        _focus = index;
        InvalidateVisual();
        ShowTip(index);
    }

    private void Unfocus()
    {
        if (_focus is null)
        {
            return;
        }

        _focus = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    private void ShowTip(int index)
    {
        if (_chart is not { } chart || _loc is not { } loc || index >= chart.Days.Count)
        {
            return;
        }

        var day = chart.Days[index];
        _tipDate.Text = HistoryText.LongDate(day.Day, loc);
        if (day.Score is { } score)
        {
            _tipScore.Text = PerfNumber.Full(score);
            _tipScore.Visibility = Visibility.Visible;
            _tipLine.Text = loc.Format("history.perf.requests", day.Timed) + " · " + loc[PerfText.TierKey(score)];
        }
        else
        {
            _tipScore.Visibility = Visibility.Collapsed;
            _tipLine.Text = loc.Format("history.perf.sample.hint", day.Timed, Core.Perf.PerfFormula.PooledMinN);
        }

        var plot = Plot(chart);
        var slot = plot.Width / chart.Days.Count;
        var x = plot.Left + (index + 0.5) * slot;
        var flip = x > ActualWidth * 0.6;
        _tip.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipWidth = _tip.Child.DesiredSize.Width;
        _tip.HorizontalOffset = flip ? x - tipWidth + 4 : x - 4;
        _tip.VerticalOffset = plot.Top - 8;
        _tip.IsOpen = true;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged) && UIElementAutomationPeer.CreatePeerForElement(this) is { } peer)
        {
            AutomationProperties.SetItemStatus(this, _tipDate.Text + "，" + (day.Score is null ? string.Empty : _tipScore.Text + "，") + _tipLine.Text);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private FormattedText Text(string text, Brush brush, double size, FontWeight weight) =>
        new(text, _loc?.Culture ?? CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>The chart is one image named with its series; the day under the keyboard is its item status.</summary>
    private sealed class Peer(PerfTrendChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(PerfTrendChart);

        protected override bool IsKeyboardFocusableCore() => true;

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
