using System.Numerics;
using AgentSpeed.Presentation.Colors;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;

namespace AgentSpeed.Boards.Layers;

/// <summary>Direct2D drawing for the cached board layers: brushes from palette colors, rounded boxes, gradients and glows.</summary>
internal static class BoardPaint
{
    public static Color4 Color(Rgba color) => new((float)color.R, (float)color.G, (float)color.B, (float)Math.Clamp(color.A, 0, 1));

    public static ID2D1SolidColorBrush Brush(ID2D1DeviceContext context, Rgba color) => context.CreateSolidColorBrush(Color(color));

    public static void FillRound(ID2D1DeviceContext context, float x, float y, float width, float height, float radius, Rgba color)
    {
        if (width <= 0 || height <= 0 || color.A <= 0)
        {
            return;
        }

        using var brush = Brush(context, color);
        context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, y, x + width, y + height), radius, radius), brush);
    }

    /// <summary>A rounded rectangle's stroke in round-capped dashes of <paramref name="dash"/> and <paramref name="gap"/> points.</summary>
    public static void DashRound(ID2D1DeviceContext context, float x, float y, float width, float height, float radius, Rgba color,
        float thickness, float dash, float gap)
    {
        if (width <= 0 || height <= 0 || color.A <= 0)
        {
            return;
        }

        using var brush = Brush(context, color);
        using var factory = context.Factory;
        using var style = factory.CreateStrokeStyle(
            new StrokeStyleProperties { DashStyle = DashStyle.Custom, DashCap = CapStyle.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round },
            [dash / thickness, gap / thickness]);
        var inset = thickness / 2;
        context.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x + inset, y + inset, x + width - inset, y + height - inset),
            Math.Max(0, radius - inset), Math.Max(0, radius - inset)), brush, thickness, style);
    }

    public static void StrokeRound(ID2D1DeviceContext context, float x, float y, float width, float height, float radius, Rgba color,
        float thickness)
    {
        if (width <= 0 || height <= 0 || color.A <= 0)
        {
            return;
        }

        using var brush = Brush(context, color);
        var inset = thickness / 2;
        context.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x + inset, y + inset, x + width - inset, y + height - inset),
            Math.Max(0, radius - inset), Math.Max(0, radius - inset)), brush, thickness);
    }

    /// <summary>A horizontal (or, with <paramref name="vertical"/>, top-to-bottom) gradient across a box.</summary>
    public static ID2D1LinearGradientBrush Gradient(ID2D1DeviceContext context, float x, float y, float width, float height,
        ReadOnlySpan<(float At, Rgba Color)> stops, bool vertical = false)
    {
        var gradient = new GradientStop[stops.Length];
        for (var i = 0; i < stops.Length; i++)
        {
            gradient[i] = new GradientStop { Position = stops[i].At, Color = Color(stops[i].Color) };
        }

        using var collection = context.CreateGradientStopCollection(gradient);
        var end = vertical ? new Vector2(x, y + height) : new Vector2(x + width, y);
        return context.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = new Vector2(x, y), EndPoint = end }, collection);
    }

    /// <summary>A CSS <c>linear-gradient(<paramref name="degrees"/>deg, …)</c> across a box (0 to the top, 90 to the right).</summary>
    public static ID2D1LinearGradientBrush Gradient(ID2D1DeviceContext context, float x, float y, float width, float height,
        ReadOnlySpan<(float At, Rgba Color)> stops, float degrees)
    {
        var angle = degrees * MathF.PI / 180;
        var direction = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
        var half = (MathF.Abs(width * direction.X) + MathF.Abs(height * direction.Y)) / 2;
        var center = new Vector2(x + width / 2, y + height / 2);
        var gradient = new GradientStop[stops.Length];
        for (var i = 0; i < stops.Length; i++)
        {
            gradient[i] = new GradientStop { Position = stops[i].At, Color = Color(stops[i].Color) };
        }

        using var collection = context.CreateGradientStopCollection(gradient);
        return context.CreateLinearGradientBrush(new LinearGradientBrushProperties
        {
            StartPoint = center - direction * half,
            EndPoint = center + direction * half,
        }, collection);
    }

    /// <summary>
    /// A CSS drop shadow or text shadow: <paramref name="draw"/> recorded once, blurred to <paramref name="blur"/> points (the
    /// CSS blur radius, two standard deviations) and tinted <paramref name="color"/>, behind what is drawn next.
    /// </summary>
    public static void Glow(ID2D1DeviceContext context, Rgba color, float blur, Action<ID2D1DeviceContext> draw)
    {
        if (blur <= 0 || color.A <= 0)
        {
            return;
        }

        var transform = context.Transform;
        using var previous = context.Target;
        using var list = context.CreateCommandList();
        context.Target = list;
        context.Transform = Matrix3x2.Identity;
        draw(context);
        context.Target = previous;
        list.Close();
        context.Transform = transform;
        using var shadow = new Shadow(context);
        shadow.SetInput(0, list, true);
        shadow.BlurStandardDeviation = blur / 2;
        shadow.Color = new Vector4((float)color.R, (float)color.G, (float)color.B, (float)Math.Clamp(color.A, 0, 1));
        context.DrawImage(shadow, InterpolationMode.Linear, CompositeMode.SourceOver);
    }

    /// <summary>
    /// A CSS box shadow in the content's own colors: <paramref name="draw"/> recorded once and blurred to
    /// <paramref name="blur"/> points, behind what is drawn next (neon's LED segments).
    /// </summary>
    public static void Bloom(ID2D1DeviceContext context, float blur, Action<ID2D1DeviceContext> draw)
    {
        if (blur <= 0)
        {
            return;
        }

        var transform = context.Transform;
        using var previous = context.Target;
        using var list = context.CreateCommandList();
        context.Target = list;
        context.Transform = Matrix3x2.Identity;
        draw(context);
        context.Target = previous;
        list.Close();
        context.Transform = transform;
        using var effect = new GaussianBlur(context);
        effect.SetInput(0, list, true);
        effect.StandardDeviation = blur / 2;
        effect.BorderMode = BorderMode.Soft;
        context.DrawImage(effect, InterpolationMode.Linear, CompositeMode.SourceOver);
    }

    /// <summary>The prototypes' speed bolt (<c>M4.8 0 0.6 5.6h2.7L2.6 10 7.4 4.1H4.6z</c>, 8 × 10) at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void Bolt(ID2D1DeviceContext context, float x, float y, float scale, ID2D1Brush brush)
    {
        using var factory = context.Factory;
        using var geometry = factory.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            Vector2 P(float px, float py) => new(x + px * scale, y + py * scale);
            sink.BeginFigure(P(4.8f, 0), FigureBegin.Filled);
            sink.AddLines([P(0.6f, 5.6f), P(3.3f, 5.6f), P(2.6f, 10), P(7.4f, 4.1f), P(4.6f, 4.1f)]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        context.FillGeometry(geometry, brush);
    }
}
