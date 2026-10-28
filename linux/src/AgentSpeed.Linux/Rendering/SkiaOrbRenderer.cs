using System.Numerics;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Gpu;
using SkiaSharp;

namespace AgentSpeed.Linux.Rendering;

/// <summary>Consumes the same ordered draw list as Direct3D, with Skia geometry, glows and procedural layers.</summary>
internal sealed class SkiaOrbRenderer(SkiaGlyphs glyphs) : IDisposable
{
    private SkiaEffects? _effects;
    private SkiaEffects Effects => _effects ??= new();
    private readonly SkiaRasterFallback _raster = new();
    private readonly SKFont _font = new() { Edging = SKFontEdging.Antialias };
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private readonly Dictionary<int, SKMaskFilter> _blurs = [];
    private SKMaskFilter Blur(float sigma)
    {
        var key = Math.Max(1, (int)Math.Round(sigma * 4));
        if (!_blurs.TryGetValue(key, out var blur)) _blurs[key] = blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, key / 4f);
        return blur;
    }
    internal static SKColor Color(Vector4 v, float alpha = 1) => new((byte)Math.Clamp(v.X * 255, 0, 255),
        (byte)Math.Clamp(v.Y * 255, 0, 255), (byte)Math.Clamp(v.Z * 255, 0, 255), (byte)Math.Clamp(v.W * alpha * 255, 0, 255));
    public void Draw(SKCanvas canvas, OrbDrawList list, bool software = false)
    {
        canvas.Save();
        canvas.Translate((float)list.CenterX, (float)list.CenterY);
        canvas.Scale((float)list.ContentScale);
        canvas.Translate(-(float)list.CenterX, -(float)list.CenterY);
        var overlayAt = list.OverlayBatchStart >= 0 ? list.OverlayBatchStart : list.BatchCount;
        for (var i = 0; i <= list.BatchCount; i++)
        {
            if (i == overlayAt)
                for (var t = list.OverlayTextStart; t < list.TextCount; t++) Text(canvas, list.Texts[t], list);
            if (i == list.BatchCount) break;
            var batch = list.Batches[i];
            canvas.Save();
            if (batch.Clip != 0)
            {
                using var clip = new SKPath();
                clip.AddCircle((float)list.CenterX, (float)list.CenterY, Math.Abs(batch.Clip));
                canvas.ClipPath(clip, batch.Clip > 0 ? SKClipOperation.Intersect : SKClipOperation.Difference, true);
            }
            _paint.BlendMode = batch.Blend == OrbBlend.Additive ? SKBlendMode.Plus : SKBlendMode.SrcOver;
            if (batch.Pass == OrbPass.Text)
                for (var t = batch.Start; t < batch.Start + batch.Count; t++) Text(canvas, list.Texts[t], list);
            else if (batch.Pass == OrbPass.Shapes)
                for (var s = batch.Start; s < batch.Start + batch.Count; s++)
                { if (software) _raster.Shape(canvas, list.Instances[s], list, batch); else Effects.Shape(canvas, list.Instances[s], list, batch); }
            else if (batch.Pass == OrbPass.Liquid) { if (software) _raster.Liquid(canvas, list, batch); else Effects.Liquid(canvas, list, batch); }
            else if (batch.Pass == OrbPass.Led) { if (software) _raster.Led(canvas, list, batch); else Effects.Led(canvas, list, batch); }
            canvas.Restore();
        }
        canvas.Restore();
    }
    private void Reset() { _paint.Shader = null; _paint.MaskFilter = null; _paint.Style = SKPaintStyle.Fill; _paint.StrokeWidth = 1; }

    private void Text(SKCanvas c, in OrbTextInstance t, OrbDrawList list)
    {
        Reset(); _paint.BlendMode = SKBlendMode.SrcOver;
        var e = glyphs.At(t.Uv.X);
        c.Save();
        var mode = (OrbTextClipMode)(int)t.Params.W;
        if (mode != OrbTextClipMode.None && mode != OrbTextClipMode.Band)
        {
            c.ClipRect(new(t.Clip.X, t.Clip.Y, t.Clip.Z, t.Clip.W), antialias: true);
            if (mode is OrbTextClipMode.BelowLiquid or OrbTextClipMode.AboveLiquid)
            {
                using var liquid = LiquidPath(list.Liquid);
                c.ClipPath(liquid, mode == OrbTextClipMode.BelowLiquid ? SKClipOperation.Intersect : SKClipOperation.Difference, true);
            }
        }
        if (mode == OrbTextClipMode.Band)
        {
            using var band = new SKPath();
            var y0 = 0f; var y1 = (float)list.Canvas;
            var x0 = t.Clip.X + (y0 - t.Clip.Y) * t.Clip.W;
            var x1 = t.Clip.X + (y1 - t.Clip.Y) * t.Clip.W;
            band.MoveTo(x0 - t.Clip.Z, y0); band.LineTo(x0 + t.Clip.Z, y0);
            band.LineTo(x1 + t.Clip.Z, y1); band.LineTo(x1 - t.Clip.Z, y1); band.Close();
            c.ClipPath(band, antialias: true);
        }
        if (t.Params.X != 0)
        { c.Translate(0, t.Params.Y); c.Skew(-t.Params.X, 0); c.Translate(0, -t.Params.Y); }
        var sx = t.Rect.Z / (e.Width + 2 * e.Pad);
        var sy = t.Rect.W / (e.Key.Size * 1.4f + 2 * e.Pad);
        c.Translate(t.Rect.X, t.Rect.Y); c.Scale(sx, sy);
        _font.Typeface = e.Face; _font.Size = e.Key.Size;
        var x = e.Pad; var y = e.Pad - e.Ascent;
        if (t.Glow.W > 0 && e.Key.GlowSigma > 0)
        { _paint.MaskFilter = Blur(e.Key.GlowSigma); _paint.Color = Color(t.Glow, (float)list.Opacity); DrawText(c, e, x, y); }
        _paint.MaskFilter = null; _paint.Color = Color(t.Color, (float)list.Opacity);
        using var gradient = t.Gradient.Z > .5f ? SKShader.CreateLinearGradient(new(0, (t.Gradient.X - t.Rect.Y) / sy), new(0, (t.Gradient.Y - t.Rect.Y) / sy),
            [Color(t.Color, (float)list.Opacity), Color(t.Fill2, (float)list.Opacity)], null, SKShaderTileMode.Clamp) : null;
        if (gradient is not null) { _paint.Shader = gradient; _paint.Color = SKColors.White; }
        DrawText(c, e, x, y); _paint.Shader = null; c.Restore();
    }
    private void DrawText(SKCanvas c, SkiaGlyphs.Entry e, float x, float y)
    {
        foreach (var rune in e.Key.Text.EnumerateRunes())
        { var text = rune.ToString(); c.DrawText(text, x, y, SKTextAlign.Left, _font, _paint); x += _font.MeasureText(text) + e.Key.Tracking; }
    }
    internal static float Surface(in OrbLiquidUniforms u, float x)
    {
        var s = u.Frame.Z; var xr = x - u.Frame.X;
        var y = u.Surface.X + u.Surface.Y * (.55f * MathF.Sin(xr / (.9f * s) * MathF.Tau + u.Phases.X)
            + .30f * MathF.Sin(xr / (.47f * s) * MathF.Tau - u.Phases.Y) + .15f * MathF.Sin(xr / (.23f * s) * MathF.Tau + u.Phases.Z))
            + u.Surface.Z * MathF.Sin(xr / (.11f * s) * MathF.Tau + u.Phases.W) * (.7f + .3f * MathF.Sin(xr / (.05f * s) + u.Phases.W * 1.7f));
        ReadOnlySpan<Vector4> ripples = [u.Ripple0, u.Ripple1, u.Ripple2, u.Ripple3];
        foreach (var ripple in ripples)
        {
            if (ripple.W <= 0) continue;
            var delta = MathF.Abs(x - ripple.X) - ripple.Y; var k = delta / (.075f * s);
            y -= ripple.Z * MathF.Exp(-k * k) * MathF.Cos(MathF.Tau * delta / (.1f * s));
        }
        return y;
    }
    private static SKPath LiquidPath(in OrbLiquidUniforms u)
    {
        var p = new SKPath(); var r = u.Frame.W;
        p.MoveTo(u.Frame.X - r, u.Frame.Y + r);
        for (var x = -r; x <= r; x += Math.Max(1, r / 64)) p.LineTo(u.Frame.X + x, Surface(u, u.Frame.X + x));
        p.LineTo(u.Frame.X + r, u.Frame.Y + r); p.Close(); return p;
    }


    public void Dispose() { _raster.Dispose(); _effects?.Dispose(); _paint.Dispose(); _font.Dispose(); foreach (var filter in _blurs.Values) filter.Dispose(); }
}
