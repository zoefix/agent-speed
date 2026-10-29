using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Boards;

namespace AgentSpeed.Settings.BoardsPane;

/// <summary>
/// A still miniature of a board style for the Boards list and the style cards (DESIGN 2c Styles; colors from the
/// prototypes in spec/prototypes/board): glass's frosted panel with its aurora and sparkline, timing's slab with the heat
/// rail and position blocks, neon's deck with its tube edge and LED bar; light or dark with the app; with the live metrics on,
/// the hero's sub-gauge (glass's pearl arc, timing's data box, neon's LED digits and meter) and the rows' 性能 column. Drawn
/// once per change.
/// </summary>
internal sealed class BoardThumbnail : FrameworkElement
{
    public static readonly DependencyProperty StyleKindProperty = DependencyProperty.Register(nameof(StyleKind), typeof(BoardStyle), typeof(BoardThumbnail),
        new FrameworkPropertyMetadata(BoardStyle.Neon, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DarkProperty = DependencyProperty.Register(nameof(Dark), typeof(bool), typeof(BoardThumbnail),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReadoutsProperty = DependencyProperty.Register(nameof(Readouts), typeof(BoardReadouts), typeof(BoardThumbnail),
        new FrameworkPropertyMetadata(BoardReadouts.All, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SideProperty = DependencyProperty.Register(nameof(Side), typeof(double), typeof(BoardThumbnail),
        new FrameworkPropertyMetadata(38.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Color Gold = Rgb(0xFA, 0xCC, 0x15);
    private static readonly Color Mint = Rgb(0x34, 0xD3, 0x99);
    private static readonly Color Amber = Rgb(0xF5, 0xA5, 0x24);
    private static readonly Color Red = Rgb(0xEF, 0x44, 0x44);
    private static readonly Color Cyan = Rgb(0x22, 0xD3, 0xEE);
    private static readonly Color Magenta = Rgb(0xE8, 0x5D, 0xF5);

    public BoardStyle StyleKind { get => (BoardStyle)GetValue(StyleKindProperty); set => SetValue(StyleKindProperty, value); }

    public bool Dark { get => (bool)GetValue(DarkProperty); set => SetValue(DarkProperty, value); }

    public double Side { get => (double)GetValue(SideProperty); set => SetValue(SideProperty, value); }

    /// <summary>The live metrics switches (DESIGN 2c): the hero's sub-gauge and the rows' 性能 column show as switched.</summary>
    public BoardReadouts Readouts { get => (BoardReadouts)GetValue(ReadoutsProperty); set => SetValue(ReadoutsProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    protected override void OnRender(DrawingContext dc)
    {
        var side = Side;
        var radius = BoardMetrics.For(StyleKind, BoardSize.M).Radius / BoardSizes.Side(BoardSize.M) * side;
        var box = new Rect(0.5, 0.5, side - 1, side - 1);
        var u = side / 64;
        switch (StyleKind)
        {
            case BoardStyle.Glass:
                DrawGlass(dc, box, radius, u);
                break;
            case BoardStyle.Timing:
                DrawTiming(dc, box, radius, u);
                break;
            default:
                DrawNeon(dc, box, radius, u);
                break;
        }
    }

    private void DrawGlass(DrawingContext dc, Rect box, double radius, double u)
    {
        var stage = Dark
            ? Gradient(Rgb(0x0D, 0x10, 0x20), Rgb(0x1B, 0x10, 0x30), Rgb(0x08, 0x15, 0x1A))
            : Gradient(Rgb(0xDF, 0xE6, 0xF3), Rgb(0xF3, 0xE2, 0xEE), Rgb(0xDC, 0xEF, 0xE9));
        var glass = Dark ? Argb(0.58, 20, 22, 33) : Argb(0.62, 252, 252, 255);
        var edge = Dark ? Argb(0.13, 255, 255, 255) : Argb(0.08, 0, 0, 0);
        dc.DrawRoundedRectangle(stage, null, box, radius, radius);
        dc.PushClip(new RectangleGeometry(box, radius, radius));
        var aurora = new RadialGradientBrush(Argb(Dark ? 0.55 : 0.35, 0x8B, 0x5C, 0xF6), Colors.Transparent)
        {
            Center = new Point(0.3, 0.2), GradientOrigin = new Point(0.3, 0.2), RadiusX = 0.6, RadiusY = 0.45,
        };
        aurora.Freeze();
        dc.DrawRectangle(aurora, null, box);
        var teal = new RadialGradientBrush(Argb(Dark ? 0.4 : 0.3, 0x2D, 0xD4, 0xBF), Colors.Transparent)
        {
            Center = new Point(0.78, 0.25), GradientOrigin = new Point(0.78, 0.25), RadiusX = 0.45, RadiusY = 0.35,
        };
        teal.Freeze();
        dc.DrawRectangle(teal, null, box);
        dc.DrawRoundedRectangle(Frozen(glass), null, box, radius, radius);
        var readouts = Readouts;
        var spark = new StreamGeometry();
        using (var g = spark.Open())
        {
            double[] ys = [0.62, 0.55, 0.58, 0.44, 0.48, 0.36, 0.40, 0.30];
            // With the sub-gauge the sparkline becomes a flat strip across the board under the hero.
            var (top, height, step) = readouts.Hero ? (21.0, 4.5, 6.857) : (4.0, 26.0, 6.8);
            g.BeginFigure(new Point(box.X + 8 * u, box.Y + height * u * ys[0] + top * u), false, false);
            for (var i = 1; i < ys.Length; i++)
            {
                g.LineTo(new Point(box.X + (8 + i * step) * u, box.Y + height * u * ys[i] + top * u), true, true);
            }
        }

        spark.Freeze();
        dc.DrawGeometry(null, Pen(Amber, (readouts.Hero ? 1.1 : 1.4) * u), spark);
        var ink = Dark ? Argb(0.9, 244, 246, 252) : Argb(0.85, 17, 20, 30);
        if (readouts.Hero)
        {
            GlassDial(dc, new Point(box.Right - 13 * u, box.Y + 9.5 * u), u, ink, readouts.Intelligence);
        }

        Rows(dc, box, u, ink, Mint, 0.09, readouts.Intelligence);
        dc.Pop();
        dc.DrawRoundedRectangle(null, Pen(edge, 1), box, radius, radius);
    }

    private void DrawTiming(DrawingContext dc, Rect box, double radius, double u)
    {
        var slab = Dark ? Rgb(0x0C, 0x0E, 0x14) : Rgb(0xFB, 0xFB, 0xFD);
        var block = Dark ? Rgb(0x1B, 0x1F, 0x2A) : Rgb(0xE6, 0xE9, 0xF0);
        var ink = Dark ? Argb(0.9, 245, 247, 251) : Argb(0.88, 14, 17, 24);
        var edge = Dark ? Argb(0.09, 255, 255, 255) : Argb(0.08, 0, 0, 0);
        dc.DrawRoundedRectangle(Frozen(slab), null, box, radius, radius);
        dc.PushClip(new RectangleGeometry(box, radius, radius));
        var rail = new LinearGradientBrush(new GradientStopCollection
        {
            new(Rgb(0x22, 0xC5, 0x5E), 0), new(Amber, 0.55), new(Red, 1),
        }, 0);
        rail.Freeze();
        dc.DrawRectangle(rail, null, new Rect(box.X, box.Y, box.Width * 0.72, 2.2 * u));
        var readouts = Readouts;
        if (readouts.Hero)
        {
            TimingBox(dc, new Rect(box.Right - 25 * u, box.Y + 6 * u, 18 * u, 14 * u), u, ink, block, Dark ? Rgb(0x12, 0x15, 0x1D) : Rgb(0xF1, 0xF3, 0xF7),
                readouts);
        }
        else
        {
            dc.DrawEllipse(Frozen(Red), null, new Point(box.Right - 9 * u, box.Y + 9 * u), 2 * u, 2 * u);
        }

        dc.DrawRoundedRectangle(Frozen(ink), null, new Rect(box.X + 8 * u, box.Y + 9 * u, readouts.Hero ? 20 * u : 24 * u, 7 * u), 1.5 * u, 1.5 * u);
        for (var i = 0; i < 3; i++)
        {
            var y = box.Y + (28 + i * 11) * u;
            dc.DrawRoundedRectangle(Frozen(i == 0 ? Gold : block), null, new Rect(box.X + 7 * u, y, 7 * u, 7 * u), 1.2 * u, 1.2 * u);
            dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.7 - i * 0.15)), null, new Rect(box.X + 17 * u, y + 1.5 * u, (26 - i * 4) * u, 4 * u), 2 * u, 2 * u);
            dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.45)), null, new Rect(box.Right - 15 * u, y + 1.5 * u, 8 * u, 4 * u), 2 * u, 2 * u);
            if (readouts.Intelligence)
            {
                dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.25)), null, new Rect(box.Right - 22 * u, y + 2 * u, 5 * u, 3 * u), 1.5 * u, 1.5 * u);
            }
        }

        dc.Pop();
        dc.DrawRoundedRectangle(null, Pen(edge, 1), box, radius, radius);
    }

    private void DrawNeon(DrawingContext dc, Rect box, double radius, double u)
    {
        var deck = Dark ? Gradient(Rgb(0x05, 0x06, 0x0C), Rgb(0x0A, 0x0D, 0x18), Rgb(0x05, 0x06, 0x0C))
            : Gradient(Rgb(0xF7, 0xF8, 0xFC), Rgb(0xEC, 0xEF, 0xF7), Rgb(0xF7, 0xF8, 0xFC));
        var unlit = Dark ? Argb(0.16, 140, 170, 255) : Argb(0.14, 20, 30, 80);
        var ink = Dark ? Argb(0.9, 243, 246, 255) : Argb(0.88, 10, 13, 24);
        dc.DrawRoundedRectangle(deck, null, box, radius, radius);
        dc.PushClip(new RectangleGeometry(box, radius, radius));
        for (var i = 0; i < 12; i++)
        {
            var lit = i < 8;
            var color = lit ? Lerp(Cyan, Magenta, i / 11.0) : unlit;
            dc.DrawRoundedRectangle(Frozen(color), null, new Rect(box.X + (8 + i * 4) * u, box.Y + 20 * u, 3 * u, 3.2 * u), 0.6 * u, 0.6 * u);
        }

        dc.DrawRoundedRectangle(Frozen(ink), null, new Rect(box.X + 8 * u, box.Y + 9 * u, 22 * u, 7 * u), 1.5 * u, 1.5 * u);
        var readouts = Readouts;
        if (readouts.Hero)
        {
            NeonGauge(dc, new Point(box.Right - 13 * u, box.Y + 6 * u), u, ink, unlit, readouts.Intelligence);
        }

        Rows(dc, box, u, ink, Mint, 0.06, readouts.Intelligence);
        dc.Pop();
        var tube = new LinearGradientBrush(Cyan, Magenta, new Point(0, 0), new Point(1, 1));
        tube.Freeze();
        if (Dark)
        {
            dc.DrawRoundedRectangle(null, new Pen(Frozen(Fade(Cyan, 0.25)), 3.2 * u), box, radius, radius);
        }

        dc.DrawRoundedRectangle(null, new Pen(tube, 1.3 * u), box, radius, radius);
    }

    /// <summary>
    /// Three ranking rows: the leader's gold rank dot, names fading down the list and a share underline; with 性能 on, a quiet
    /// 性能 cell before each row's value.
    /// </summary>
    private static void Rows(DrawingContext dc, Rect box, double u, Color ink, Color share, double fill, bool intelligence)
    {
        for (var i = 0; i < 3; i++)
        {
            var y = box.Y + (30 + i * 10.5) * u;
            dc.DrawRoundedRectangle(Frozen(Fade(ink, fill)), null, new Rect(box.X + 5 * u, y - 2.5 * u, box.Width - 10 * u, 8.5 * u), 2 * u, 2 * u);
            dc.DrawEllipse(Frozen(i == 0 ? Gold : Fade(ink, 0.35)), null, new Point(box.X + 10 * u, y + 1.7 * u), 1.8 * u, 1.8 * u);
            dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.75 - i * 0.18)), null, new Rect(box.X + 15 * u, y, (24 - i * 5) * u, 3.4 * u), 1.7 * u, 1.7 * u);
            dc.DrawRectangle(Frozen(Fade(share, 0.8 - i * 0.2)), null, new Rect(box.X + 15 * u, y + 4.6 * u, (30 - i * 9) * u, 0.9 * u));
            dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.55)), null, new Rect(box.Right - 12 * u, y, 6 * u, 3.4 * u), 1.7 * u, 1.7 * u);
            if (intelligence)
            {
                dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.28)), null, new Rect(box.Right - 20 * u, y + 0.4 * u, 5 * u, 2.6 * u), 1.3 * u, 1.3 * u);
            }
        }
    }

    // Glass's pearl arc around 性能 (240° from the lower left over the top), its number a soft bar.
    private void GlassDial(DrawingContext dc, Point center, double u, Color ink, bool intelligence)
    {
        var radius = 6.2 * u;
        Point At(double degrees) => new(center.X + radius * Math.Cos(degrees * Math.PI / 180), center.Y + radius * Math.Sin(degrees * Math.PI / 180));
        var track = new StreamGeometry();
        using (var g = track.Open())
        {
            g.BeginFigure(At(150), false, false);
            g.ArcTo(At(30), new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, true);
        }

        track.Freeze();
        dc.DrawGeometry(null, Pen(Fade(ink, 0.16), 1.1 * u), track);
        if (intelligence)
        {
            var value = new StreamGeometry();
            using (var g = value.Open())
            {
                g.BeginFigure(At(150), false, false);
                g.ArcTo(At(300), new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, true);
            }

            value.Freeze();
            var pearl = new LinearGradientBrush(Dark ? Rgb(0xA5, 0xF3, 0xFC) : Rgb(0x1A, 0xA3, 0xC4), Dark ? Rgb(0xF5, 0xD0, 0xFE) : Rgb(0xC0, 0x5B, 0xD9),
                new Point(0, 1), new Point(1, 0));
            pearl.Freeze();
            var pen = new Pen(pearl, 1.3 * u) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, value);
            dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.8)), null, new Rect(center.X - 3.4 * u, center.Y - 1.4 * u, 6.8 * u, 3 * u), 1.4 * u, 1.4 * u);
        }

        for (var i = 0; i < 2; i++)
        {
            dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.45)), null, new Rect(center.X - 5 * u, center.Y + (5.4 + i * 2.6) * u, 10 * u, 1.4 * u), 0.7 * u, 0.7 * u);
        }
    }

    // Timing's data box: label tabs on the left, values on one right edge.
    private static void TimingBox(DrawingContext dc, Rect rect, double u, Color ink, Color block, Color slab, BoardReadouts readouts)
    {
        dc.DrawRoundedRectangle(Frozen(slab), Pen(Fade(ink, 0.12), 0.6 * u), rect, 1.6 * u, 1.6 * u);
        dc.DrawRoundedRectangle(Frozen(block), null, new Rect(rect.X, rect.Y, 5 * u, rect.Height), 1.6 * u, 1.6 * u);
        var rows = (readouts.Intelligence ? 1 : 0) + (readouts.Thinking ? 1 : 0) + (readouts.Cost ? 1 : 0);
        var pitch = rect.Height / Math.Max(1, rows);
        for (var i = 0; i < rows; i++)
        {
            var main = i == 0 && readouts.Intelligence;
            var width = main ? 7 * u : 5 * u;
            var height = main ? 2.6 * u : 1.8 * u;
            dc.DrawRoundedRectangle(Frozen(Fade(ink, main ? 0.9 : 0.6)), null,
                new Rect(rect.Right - 1.6 * u - width, rect.Y + pitch * (i + 0.5) - height / 2, width, height), height / 2, height / 2);
        }
    }

    // Neon's LED sub-gauge: lit digits over a 12-segment meter, the table under it.
    private static void NeonGauge(DrawingContext dc, Point top, double u, Color ink, Color unlit, bool intelligence)
    {
        if (intelligence)
        {
            dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.9)), null, new Rect(top.X - 5 * u, top.Y, 10 * u, 3.6 * u), 1 * u, 1 * u);
            for (var i = 0; i < 6; i++)
            {
                dc.DrawRectangle(Frozen(i < 4 ? Fade(ink, 0.8) : unlit), null, new Rect(top.X - 5.5 * u + i * 1.9 * u, top.Y + 5 * u, 1.4 * u, 0.9 * u));
            }
        }

        for (var i = 0; i < 2; i++)
        {
            dc.DrawRoundedRectangle(Frozen(Fade(ink, 0.45)), null, new Rect(top.X - 5 * u, top.Y + (intelligence ? 7.4 : 2) * u + i * 2.8 * u, 10 * u, 1.5 * u),
                0.75 * u, 0.75 * u);
        }
    }

    private static LinearGradientBrush Gradient(Color a, Color b, Color c)
    {
        var brush = new LinearGradientBrush(new GradientStopCollection { new(a, 0), new(b, 0.55), new(c, 1) }, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Argb(double a, byte r, byte g, byte b) => Color.FromArgb((byte)Math.Round(a * 255), r, g, b);

    private static Color Fade(Color color, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * color.A), color.R, color.G, color.B);

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
