using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.History.Perf;

/// <summary>
/// The medium layout's 構成 cell (DESIGN 12, <i>Widths</i>): seven bars, one per component in the card's order, 5 wide, 2
/// apart and 20 tall, each filled from the bottom to <c>f</c> of its sub-score in the score ink over a track, with a dashed
/// 1000 line across them at mid-height; an absent component is a 1 DIP dotted stub. Hovering a bar shows the component's
/// name, value and sub-score. Its words are the row's (<c>a11y.history.perf.parts</c>).
/// </summary>
internal sealed class PerfPartsBars : FrameworkElement
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(
        nameof(Parts), typeof(IReadOnlyList<PerfHistoryCell>), typeof(PerfPartsBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LocProperty = DependencyProperty.Register(
        nameof(Loc), typeof(Localizer), typeof(PerfPartsBars), new FrameworkPropertyMetadata(null));

    private int _hovered = -1;

    public PerfPartsBars()
    {
        SnapsToDevicePixels = true;
        Width = 7 * PerfHistoryLayout.PartWidth + 6 * PerfHistoryLayout.PartGap;
        Height = PerfHistoryLayout.PartHeight;
        VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTip = string.Empty;
        ToolTipOpening += (_, e) =>
        {
            if (TipText() is not { Length: > 0 } text)
            {
                e.Handled = true;
                return;
            }

            ToolTip = text;
        };
    }

    public IReadOnlyList<PerfHistoryCell>? Parts
    {
        get => (IReadOnlyList<PerfHistoryCell>?)GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    public Localizer? Loc
    {
        get => (Localizer?)GetValue(LocProperty);
        set => SetValue(LocProperty, value);
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var step = PerfHistoryLayout.PartWidth + PerfHistoryLayout.PartGap;
        var index = Math.Clamp((int)(e.GetPosition(this).X / step), 0, 6);
        if (index != _hovered)
        {
            _hovered = index;
            ToolTip = TipText();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Parts is not { Count: > 0 } parts)
        {
            return;
        }

        var height = PerfHistoryLayout.PartHeight;
        var width = PerfHistoryLayout.PartWidth;
        var track = PerfHistoryAppearance.Brush(PerfHistoryAppearance.Track(this));
        var ink = PerfHistoryAppearance.Brush(PerfHistoryAppearance.Ink(this));
        var dots = PerfHistoryAppearance.Brush(PerfHistoryAppearance.Label(this, PerfColors.TickOpacity));
        for (var i = 0; i < parts.Count && i < 7; i++)
        {
            var x = i * (width + PerfHistoryLayout.PartGap);
            var part = parts[i];
            if (part.Fill is not { } fill)
            {
                var stub = new Pen(dots, 1) { DashStyle = new DashStyle([0, 2], 0), DashCap = PenLineCap.Round };
                stub.Freeze();
                dc.DrawLine(stub, new Point(x + width / 2, height - 0.5), new Point(x + width / 2, height - 6));
                continue;
            }

            dc.DrawRoundedRectangle(track, null, new Rect(x, 0, width, height), 1.5, 1.5);
            var filled = Math.Max(1.5, Math.Clamp(fill, 0, 1) * height);
            dc.DrawRoundedRectangle(ink, null, new Rect(x, height - filled, width, filled), 1.5, 1.5);
        }

        var reference = new Pen(PerfHistoryAppearance.Brush(PerfHistoryAppearance.Label(this, 0.45)), 1) { DashStyle = new DashStyle([2, 2], 0) };
        reference.Freeze();
        var mid = Math.Round(height / 2) + 0.5;
        dc.DrawLine(reference, new Point(-1, mid), new Point(ActualWidth + 1, mid));
    }

    private string TipText()
    {
        if (Parts is not { } parts || Loc is not { } loc || _hovered < 0 || _hovered >= parts.Count)
        {
            return string.Empty;
        }

        var part = parts[_hovered];
        var name = loc[PerfText.ComponentKey(part.Id)];
        return part.Present ? $"{name}  {part.Text} · {part.SubScore}" : $"{name}  {part.Tooltip}";
    }
}
