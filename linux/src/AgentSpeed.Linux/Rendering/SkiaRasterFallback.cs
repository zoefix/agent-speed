using System.Numerics;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Gpu;
using SkiaSharp;

namespace AgentSpeed.Linux.Rendering;

/// <summary>Vector and blur equivalents for CPU-only compositors. Avoids evaluating an SDF for every primitive pixel.</summary>
internal sealed class SkiaRasterFallback : IDisposable
{
    private readonly SKPaint _paint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly Dictionary<int, SKMaskFilter> _filters = [];
    private SKMaskFilter Blur(float sigma)
    {
        var key = Math.Max(1, (int)Math.Round(sigma * 4));
        if (!_filters.TryGetValue(key, out var filter)) _filters[key] = filter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, key / 4f);
        return filter;
    }
    private static SKColor C(Vector4 c, float alpha = 1) => SkiaOrbRenderer.Color(c, alpha);
    private void Reset(OrbBatch batch)
    {
        _paint.Shader = null; _paint.MaskFilter = null; _paint.Style = SKPaintStyle.Fill;
        _paint.StrokeCap = SKStrokeCap.Round;
        _paint.BlendMode = batch.Blend == OrbBlend.Additive ? SKBlendMode.Plus : SKBlendMode.SrcOver;
    }
    private void Paint(SKCanvas c, SKPath path, in OrbGpuInstance s, float opacity, SKShader? gradient = null)
    {
        _paint.Shader = gradient;
        if (s.B.W > 0 && s.Extra.Y > 0)
        {
            _paint.Color = gradient is null ? C(s.Color, opacity * s.Extra.Y) : SKColors.White.WithAlpha((byte)Math.Clamp(opacity * s.Extra.Y * 255, 0, 255));
            _paint.MaskFilter = Blur(s.B.W / 1.414f); c.DrawPath(path, _paint); _paint.MaskFilter = null;
        }
        _paint.Color = gradient is null ? C(s.Color, opacity * s.Extra.X) : SKColors.White.WithAlpha((byte)Math.Clamp(opacity * s.Extra.X * 255, 0, 255));
        c.DrawPath(path, _paint); _paint.Shader = null;
    }
    public void Shape(SKCanvas c, in OrbGpuInstance s, OrbDrawList list, OrbBatch batch)
    {
        Reset(batch); c.Save(); c.Translate(s.A.X, s.A.Y); c.RotateRadians(s.A.Z);
        var opacity = (float)list.Opacity; var b = s.B;
        using var path = new SKPath();
        if (s.Shape is OrbShape.Arc or OrbShape.BandArc)
        {
            var segments = Math.Clamp((int)Math.Ceiling(s.Extra.W * 12), 1, 76);
            for (var i = 0; i < segments; i++)
            {
                var t = (i + .5f) / segments;
                var color = (OrbFill)(int)s.Extra2.X == OrbFill.Palette
                    ? OrbGpuInstance.ToVector(HeatPalette.Sample(s.Extra2.Y + (s.Extra2.Z - s.Extra2.Y) * t)) with { W = s.Color.W }
                    : Vector4.Lerp(s.Color2, s.Color, t);
                var tapered = s.Shape == OrbShape.Arc;
                var alpha = tapered ? MathF.Pow(t, 1.4f) : s.Extra2.W + (1 - s.Extra2.W) * t;
                path.Reset(); path.AddArc(new(-b.X, -b.X, b.X, b.X), (s.Extra.Z + s.Extra.W * i / segments) * 180 / MathF.PI,
                    s.Extra.W / segments * 180 / MathF.PI + .02f);
                _paint.Style = SKPaintStyle.Stroke; _paint.StrokeWidth = b.Y * (tapered ? .3f + .7f * t : 1);
                _paint.StrokeCap = b.Z > 0 ? SKStrokeCap.Round : SKStrokeCap.Butt;
                var segment = s; segment.Color = color; segment.Extra.X *= alpha; segment.Extra.Y *= alpha;
                Paint(c, path, segment, opacity);
            }
            c.Restore(); return;
        }
        if (s.Shape == OrbShape.Radial)
        {
            var outer = Math.Max(1, s.Extra.W); var ratio = Math.Clamp(s.Extra.Z / outer, 0, .98f);
            var colors = new List<SKColor>(); var positions = new List<float>();
            if (ratio > 0)
            {
                colors.Add(SKColors.Transparent); positions.Add(0);
                if (ratio > 1 / outer) { colors.Add(SKColors.Transparent); positions.Add(ratio - 1 / outer); }
            }
            // The original OrbShapes.hlsl radial falloff, sampled into a native
            // gradient instead of replacing it with an unrelated four-stop halo.
            for (var i = 0; i <= 32; i++)
            {
                var t = i / 32f;
                colors.Add(C(s.Color, opacity * s.Extra.X * MathF.Exp(-t * t * 4.2f) * (1 - t * t) * (i == 0 ? .5f : 1)));
                positions.Add(ratio + (1 - ratio) * t);
            }
            using var halo = SKShader.CreateRadialGradient(new(0, 0), outer,
                colors.ToArray(), positions.ToArray(), SKShaderTileMode.Clamp);
            _paint.Shader = halo; _paint.Color = SKColors.White; c.DrawCircle(0, 0, outer, _paint); _paint.Shader = null;
            c.Restore(); return;
        }
        if (s.Shape == OrbShape.Stroke)
        {
            path.MoveTo(-b.X, 0); path.LineTo(b.X, 0); _paint.Style = SKPaintStyle.Stroke;
            for (var layer = 0; layer < 3; layer++)
            {
                _paint.StrokeWidth = 2 * (layer == 0 ? b.Y : layer == 1 ? b.Z : b.W);
                _paint.Color = C(layer == 2 ? s.Color2 : s.Color, opacity * (layer == 0 ? s.Extra.X : layer == 1 ? s.Extra.Y : s.Extra.Z));
                c.DrawPath(path, _paint);
            }
            c.Restore(); return;
        }
        switch (s.Shape)
        {
            case OrbShape.RoundedRect: path.AddRoundRect(new(-b.X, -b.Y, b.X, b.Y), b.Z, b.Z); break;
            case OrbShape.Circle: path.AddCircle(0, 0, b.X); break;
            case OrbShape.Ring: path.AddCircle(0, 0, b.X); _paint.Style = SKPaintStyle.Stroke; _paint.StrokeWidth = b.Y; break;
            case OrbShape.Capsule: path.AddRoundRect(new(-b.X / 2 - b.Y / 2, -b.Y / 2, b.X / 2 + b.Y / 2, b.Y / 2), b.Y / 2, b.Y / 2); break;
            case OrbShape.Ellipse: path.AddOval(new(-b.X, -b.Y, b.X, b.Y)); break;
            case OrbShape.Blade:
                path.MoveTo(-b.Y, -b.Z); path.LineTo(0, -s.Extra.Z); path.LineTo(b.X, -s.Extra.W);
                path.LineTo(b.X, s.Extra.W); path.LineTo(0, s.Extra.Z); path.LineTo(-b.Y, b.Z); path.Close(); break;
            default: throw new InvalidOperationException($"Unsupported primitive {s.Shape}.");
        }
        SKShader? gradient = null;
        var fill = (OrbFill)(int)s.Extra2.X;
        if (s.Shape == OrbShape.Capsule)
            gradient = SKShader.CreateLinearGradient(new(-b.X / 2, 0), new(b.X / 2, 0), [C(s.Color2), C(s.Color)], null, SKShaderTileMode.Clamp);
        else if (fill == OrbFill.Linear)
        {
            var x = MathF.Cos(s.Extra2.Y - s.A.Z) * s.Extra2.Z; var y = MathF.Sin(s.Extra2.Y - s.A.Z) * s.Extra2.Z;
            gradient = SKShader.CreateLinearGradient(new(-x, -y), new(x, y), [C(s.Color), C(s.Color2)], null, SKShaderTileMode.Clamp);
        }
        else if (fill is OrbFill.Radial or OrbFill.RadialStops)
        {
            var stops = fill == OrbFill.RadialStops;
            gradient = SKShader.CreateRadialGradient(stops ? new(0, 0) : new(s.Extra2.Y, s.Extra2.Z), Math.Max(1, stops ? b.X : s.Extra2.W),
                stops ? [C(s.Color), C(s.Color2), C(s.Color2).WithAlpha(0)] : [C(s.Color), C(s.Color2)],
                stops ? [0, Math.Clamp(s.Extra2.Y, .01f, .99f), 1] : null, SKShaderTileMode.Clamp);
        }
        Paint(c, path, s, opacity, gradient); gradient?.Dispose(); c.Restore();
    }
    private SkiaOriginalLayers? _original;
    private SkiaOriginalLayers Original => _original ??= new();
    public void Liquid(SKCanvas canvas, OrbDrawList list, OrbBatch batch) => Original.Draw(canvas, list, batch);
    public void Led(SKCanvas canvas, OrbDrawList list, OrbBatch batch) => Original.Draw(canvas, list, batch);
    public void Dispose() { _original?.Dispose(); _paint.Dispose(); foreach (var filter in _filters.Values) filter.Dispose(); }
}
