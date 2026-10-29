using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Perf;

/// <summary>
/// Draws the provisional mark (DESIGN 12, <i>Provisional mark</i>) in WPF: the dotted underline under a number, dotted fills
/// and absent tracks, and the dotted outline of a pen. The geometry is <see cref="PerfProvisionalMark"/>'s, shared with the
/// GPU renderers.
/// </summary>
internal static class PerfMarkDrawing
{
    /// <summary>The dotted underline under a number whose advance width starts at <paramref name="left"/> on <paramref name="baseline"/>.</summary>
    /// <param name="opacity">Multiplies the mark's own 70 % (for its 0.25 s fade).</param>
    public static void Underline(DrawingContext dc, PerfUnderline mark, double left, double baseline, Brush brush, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(mark);
        var alpha = mark.Opacity * Math.Clamp(opacity, 0, 1);
        if (alpha <= 0)
        {
            return;
        }

        dc.PushOpacity(alpha);
        Dots(dc, mark.Centers, left, baseline + mark.Below, mark.Diameter, brush);
        dc.Pop();
    }

    /// <summary>Round dots of <paramref name="diameter"/> centred on <paramref name="y"/> at <paramref name="left"/> + each centre.</summary>
    public static void Dots(DrawingContext dc, IReadOnlyList<double> centers, double left, double y, double diameter, Brush brush)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(centers);
        var r = diameter / 2;
        foreach (var x in centers)
        {
            dc.DrawEllipse(brush, null, new Point(left + x, y), r, r);
        }
    }

    /// <summary>Round dots of the rect's height on a 2.2 × pitch filling its width (a dotted fill or an absent track).</summary>
    public static void DottedRow(DrawingContext dc, Rect rect, Brush brush)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var d = rect.Height;
        Dots(dc, PerfProvisionalMark.SpacedCenters(rect.Width, PerfProvisionalMark.ShapePitch * d, d), rect.X, rect.Y + d / 2, d, brush);
    }

    /// <summary>A pen that draws round dots along a path: dash 0, gap 2 × its thickness, round caps.</summary>
    public static Pen DottedPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            DashStyle = new DashStyle([0, 2], 0),
            DashCap = PenLineCap.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }
}
