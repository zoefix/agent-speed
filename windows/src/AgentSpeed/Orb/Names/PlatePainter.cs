using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Theme;

namespace AgentSpeed.Orb.Names;

/// <summary>Light, dark or Windows high contrast (DESIGN 2b, Name plate, Material).</summary>
internal readonly record struct PlateLook(bool Dark, bool HighContrast);

/// <summary>
/// Frozen brushes and pens of one plate look. On Windows the fill is solid (layered windows have no acrylic); high
/// contrast uses <c>ButtonFace</c>, <c>ButtonText</c> and a 1 px <c>WindowText</c> border.
/// </summary>
internal sealed class PlateInk
{
    private readonly Dictionary<(NamePlateDotKind Kind, Presentation.Colors.Rgba Color), Brush> _dots = new();

    public PlateInk(PlateLook look, Rgba? accent = null)
    {
        Look = look;
        if (look.HighContrast)
        {
            var face = SystemColors.ControlColor;
            Fill = MediaColors.Frozen(face);
            Text = MediaColors.Frozen(SystemColors.ControlTextColor);
            Secondary = Text;
            Accent = MediaColors.Frozen(SystemColors.HotTrackColor);
            Separator = Fill;
            Border = MediaColors.FrozenPen(MediaColors.Frozen(SystemColors.WindowTextColor), 1);
            DotRing = Border;
            return;
        }

        var material = NamePlateMaterial.For(look.Dark);
        Fill = Vertical(material.FillTop, material.FillBottom);
        Rim = MediaColors.FrozenPen(Vertical(material.RimTop, material.RimBottom), NamePlateMaterial.RimWidth);
        Border = MediaColors.FrozenPen(material.Outer.ToBrush(), NamePlateMaterial.OuterWidth);
        Text = material.Text.ToBrush();
        Secondary = material.Secondary.ToBrush();
        Accent = (accent ?? material.Accent).ToBrush();
        Separator = material.Separator.ToBrush();
        ShadowOpacity = material.Shadow.A;
    }

    public PlateLook Look { get; }

    public Brush Fill { get; }

    /// <summary>The 1 pt inner rim (null in high contrast).</summary>
    public Pen? Rim { get; }

    /// <summary>The outer stroke: 0.5 pt, or the 1 px <c>WindowText</c> border in high contrast.</summary>
    public Pen Border { get; }

    public Brush Text { get; }

    public Brush Secondary { get; }

    public Brush Accent { get; }

    /// <summary>The plate colour at its center: the 1 pt ring between overlapping dots.</summary>
    public Brush Separator { get; }

    /// <summary>High contrast: the 1 px <c>WindowText</c> ring around every dot.</summary>
    public Pen? DotRing { get; }

    /// <summary>Opacity of the black drop shadow (none in high contrast).</summary>
    public double ShadowOpacity { get; }

    public Brush InkOf(NamePlateInk ink) => ink switch
    {
        NamePlateInk.Secondary => Secondary,
        NamePlateInk.Accent => Accent,
        _ => Text,
    };

    public Brush SourceBrush(Core.SourceKind source)
    {
        var color = NamePlateMaterial.SourceColor(source, Look.Dark);
        if (!_dots.TryGetValue((NamePlateDotKind.Source, color), out var brush))
        {
            brush = color.ToBrush();
            _dots[(NamePlateDotKind.Source, color)] = brush;
        }

        return brush;
    }

    private static Brush Vertical(Presentation.Colors.Rgba top, Presentation.Colors.Rgba bottom)
    {
        var brush = new LinearGradientBrush(top.ToColor(), bottom.ToColor(), new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Draws a plate's capsule and its content (dots and tracked text) into drawing contexts.</summary>
internal static class PlatePainter
{
    /// <summary>Wedges of the all-sources dot: enough that the conic gradient reads smooth at 5–7 pt.</summary>
    private const int SpectrumWedges = 48;

    private static readonly Dictionary<bool, Drawing> Spectra = new();

    /// <summary>The capsule: solid fill, the 1 pt inner rim and the outer stroke (or the high-contrast border).</summary>
    public static void Body(DrawingContext dc, Rect capsule, PlateInk ink)
    {
        var radius = capsule.Height / 2;
        dc.DrawRoundedRectangle(ink.Fill, null, capsule, radius, radius);
        if (ink.Rim is { } rim)
        {
            var inner = Inset(capsule, rim.Thickness / 2);
            dc.DrawRoundedRectangle(null, rim, inner, inner.Height / 2, inner.Height / 2);
        }

        var outer = ink.Look.HighContrast ? Inset(capsule, ink.Border.Thickness / 2) : Inset(capsule, -ink.Border.Thickness / 2);
        dc.DrawRoundedRectangle(null, ink.Border, outer, outer.Height / 2, outer.Height / 2);
    }

    /// <summary>
    /// The content of a laid-out line, with the capsule's left edge at x = 0 and its top at y = 0: overlapping dots (the
    /// first in front, each later one behind a 1 pt ring in the plate colour), then each run's text.
    /// </summary>
    public static void Content(DrawingContext dc, NamePlateLayout layout, PlateInk ink, PlateTypography type, double pixelsPerDip)
    {
        var m = layout.Metrics;
        var centerY = m.Height / 2;
        for (var r = 0; r < layout.Line.Runs.Length; r++)
        {
            var run = layout.Line.Runs[r];
            var placed = layout.Runs[r];
            var dots = run.Dots;
            for (var i = dots.Length - 1; i >= 0; i--)
            {
                var center = new Point(placed.DotsX + m.Dot / 2 + i * m.DotPitch, centerY);
                if (dots.Length > 1)
                {
                    dc.DrawEllipse(ink.Separator, null, center, m.Dot / 2 + 1, m.Dot / 2 + 1);
                }

                Dot(dc, dots[i], center, m, ink);
            }

            var baseline = Snap(type.Baseline(centerY, m.FontSize, run.Cjk), pixelsPerDip);
            type.Draw(dc, run.Text, m.FontSize, m.Tracking(run.Cjk), Snap(placed.TextX, pixelsPerDip), baseline, ink.InkOf(run.Ink), pixelsPerDip);
        }
    }

    private static void Dot(DrawingContext dc, NamePlateDot dot, Point center, Presentation.Orb.Names.NamePlateMetrics m, PlateInk ink)
    {
        var radius = m.Dot / 2;
        switch (dot.Kind)
        {
            case NamePlateDotKind.Hollow:
                var stroke = m.HollowStroke;
                dc.DrawEllipse(null, MediaColors.FrozenPen(ink.Secondary, stroke), center, radius - stroke / 2, radius - stroke / 2);
                return;
            case NamePlateDotKind.Spectrum:
                dc.PushTransform(new MatrixTransform(m.Dot, 0, 0, m.Dot, center.X - radius, center.Y - radius));
                dc.DrawDrawing(Spectrum(ink.Look.Dark));
                dc.Pop();
                break;
            default:
                dc.DrawEllipse(ink.SourceBrush(dot.Source), null, center, radius, radius);
                break;
        }

        if (ink.DotRing is { } ring)
        {
            dc.DrawEllipse(null, ring, center, radius + ring.Thickness / 2, radius + ring.Thickness / 2);
        }
    }

    /// <summary>A unit-square conic gradient: wedges through every source colour in source order from 12 o'clock.</summary>
    private static Drawing Spectrum(bool dark)
    {
        if (Spectra.TryGetValue(dark, out var cached))
        {
            return cached;
        }

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var center = new Point(0.5, 0.5);
            const double Overlap = 0.004;
            for (var i = 0; i < SpectrumWedges; i++)
            {
                var a0 = (i / (double)SpectrumWedges - Overlap) * 2 * Math.PI;
                var a1 = ((i + 1) / (double)SpectrumWedges + Overlap) * 2 * Math.PI;
                var figure = new PathFigure { StartPoint = center, IsClosed = true, IsFilled = true };
                figure.Segments.Add(new LineSegment(OnCircle(a0), false));
                figure.Segments.Add(new ArcSegment(OnCircle(a1), new Size(0.5, 0.5), 0, false, SweepDirection.Clockwise, false));
                var geometry = new PathGeometry([figure]);
                geometry.Freeze();
                var color = NamePlateMaterial.SpectrumAt((i + 0.5) / SpectrumWedges, dark);
                dc.DrawGeometry(color.ToBrush(), null, geometry);
            }
        }

        group.Freeze();
        Spectra[dark] = group;
        return group;
    }

    /// <summary>A point on the unit dot's rim, clockwise from 12 o'clock.</summary>
    private static Point OnCircle(double angle) => new(0.5 + 0.5 * Math.Sin(angle), 0.5 - 0.5 * Math.Cos(angle));

    private static Rect Inset(Rect rect, double by) =>
        new(rect.X + by, rect.Y + by, Math.Max(0, rect.Width - 2 * by), Math.Max(0, rect.Height - 2 * by));

    /// <summary>Rounds a coordinate to whole device pixels (the capsule's origin is pixel-aligned).</summary>
    private static double Snap(double value, double pixelsPerDip) => Math.Round(value * pixelsPerDip) / pixelsPerDip;
}
