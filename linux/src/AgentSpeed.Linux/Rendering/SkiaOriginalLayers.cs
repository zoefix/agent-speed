using System.Diagnostics;
using AgentSpeed.Presentation.Orb.Gpu;
using SkiaSharp;

namespace AgentSpeed.Linux.Rendering;

/// <summary>CPU textures from the original HLSL-derived effects, with bounded resolution and refresh rate.</summary>
internal sealed class SkiaOriginalLayers : IDisposable
{
    private sealed record Cached(SKImage Image, long RefreshAfter, double Canvas, double Opacity, float Clip, float Feather, float Appearance);
    private readonly Dictionary<(OrbPass Pass, int Mode), Cached> _images = [];
    private readonly SkiaEffects _effects = new();
    private readonly SKPaint _paint = new() { IsAntialias = true };

    public void Draw(SKCanvas canvas, OrbDrawList list, OrbBatch batch)
    {
        var key = (batch.Pass, batch.Mode);
        var now = Stopwatch.GetTimestamp();
        var appearance = batch.Pass == OrbPass.Led ? list.Led.Look.X : list.Liquid.Flags.Y;
        if (!_images.TryGetValue(key, out var cached) || now >= cached.RefreshAfter || cached.Canvas != list.Canvas
            || cached.Opacity != list.Opacity || cached.Clip != batch.Clip || cached.Feather != batch.Feather || cached.Appearance != appearance)
        {
            // The soft cloth/volume layers tolerate a small texture. Crisp body
            // geometry and text still render at the full display resolution.
            var side = Math.Clamp((int)Math.Ceiling(list.Canvas / 3), 48, 80);
            using var surface = SKSurface.Create(new SKImageInfo(side, side));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(side / (float)list.Canvas);
            if (batch.Pass == OrbPass.Led) _effects.Led(surface.Canvas, list, batch);
            else _effects.Liquid(surface.Canvas, list, batch);
            cached?.Image.Dispose();
            // Budget from completion, so a costly software refresh cannot trigger
            // another one immediately on the next frame.
            cached = new(surface.Snapshot(), Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2,
                list.Canvas, list.Opacity, batch.Clip, batch.Feather, appearance);
            _images[key] = cached;
        }
        _paint.BlendMode = batch.Blend == OrbBlend.Additive ? SKBlendMode.Plus : SKBlendMode.SrcOver;
        canvas.DrawImage(cached.Image, new SKRect(0, 0, (float)list.Canvas, (float)list.Canvas),
            new SKSamplingOptions(SKFilterMode.Linear), _paint);
    }

    public void Dispose()
    {
        foreach (var cached in _images.Values) cached.Image.Dispose();
        _images.Clear(); _effects.Dispose(); _paint.Dispose();
    }
}
