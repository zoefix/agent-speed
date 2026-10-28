using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Gpu;
using SkiaSharp;

namespace AgentSpeed.Linux.Rendering;

internal sealed class SkiaEffects : IDisposable
{
    private readonly SKRuntimeEffect _shape = Load("Shapes");
    private readonly SKRuntimeEffect _liquid = Load("Liquid");
    private readonly SKRuntimeEffect _led = Load("Led");
    private readonly SKBitmap _palette;
    private readonly SKShader _paletteShader;
    private readonly SKPaint _paint = new() { IsAntialias = true };
    internal SkiaEffects()
    {
        _palette = new SKBitmap(256, 1);
        for (var i = 0; i < 256; i++) _palette.SetPixel(i, 0, SkiaOrbRenderer.Color(OrbGpuInstance.ToVector(HeatPalette.Sample(i / 255d))));
        _paletteShader = _palette.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
    }
    private static SKRuntimeEffect Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AgentSpeed.Linux.Shaders.{name}.sksl")!;
        using var reader = new StreamReader(stream);
        var effect = SKRuntimeEffect.CreateShader(reader.ReadToEnd(), out var error) ?? throw new InvalidOperationException($"{name}: {error}");
        return effect;
    }
    private static void Set(SKRuntimeEffectUniforms u, string name, Vector4 v)
    { if (u.Contains(name)) u[name] = new[] { v.X, v.Y, v.Z, v.W }; }
    private static void Set(SKRuntimeEffectUniforms u, string name, ReadOnlySpan<Vector4> values)
    { if (u.Contains(name)) u[name] = MemoryMarshal.Cast<Vector4, float>(values).ToArray(); }
    private SKRuntimeEffectUniforms Uniforms(SKRuntimeEffect effect, OrbDrawList list, OrbBatch batch)
    {
        var u = new SKRuntimeEffectUniforms(effect);
        Set(u, "viewport", new Vector4((float)list.Canvas, (float)list.Canvas, (float)list.Scale, batch.Clip));
        Set(u, "transform", new Vector4((float)list.ContentScale, (float)list.CenterX, (float)list.CenterY, batch.Feather));
        Set(u, "globals", new Vector4((float)list.Opacity, 0, 0, 0));
        if (u.Contains("mode")) u["mode"] = (float)batch.Mode;
        return u;
    }
    private void Draw(SKCanvas c, SKRuntimeEffect effect, SKRuntimeEffectUniforms u, OrbBatch batch, SKRect bounds)
    {
        var children = new SKRuntimeEffectChildren(effect);
        if (children.Names.Contains("palette")) children["palette"] = _paletteShader;
        using var shader = effect.ToShader(u, children);
        _paint.Shader = shader; _paint.BlendMode = batch.Blend == OrbBlend.Additive ? SKBlendMode.Plus : SKBlendMode.SrcOver;
        c.DrawRect(bounds, _paint); _paint.Shader = null;
    }
    public void Shape(SKCanvas c, in OrbGpuInstance s, OrbDrawList list, OrbBatch batch)
    {
        var u = Uniforms(_shape, list, batch);
        Set(u, "shape_a", s.A); Set(u, "shape_b", s.B); Set(u, "shape_color", s.Color); Set(u, "shape_color2", s.Color2);
        Set(u, "shape_extra", s.Extra); Set(u, "shape_extra2", s.Extra2);
        var glow = s.B.W * 3.2f;
        var extent = s.Shape switch
        {
            OrbShape.RoundedRect => new Vector2(s.B.X, s.B.Y).Length() + glow,
            OrbShape.Circle => s.B.X + glow,
            OrbShape.Ring or OrbShape.Arc or OrbShape.BandArc => s.B.X + s.B.Y + glow,
            OrbShape.Capsule => s.B.X * .5f + s.B.Y + glow,
            OrbShape.Radial => s.Extra.W,
            OrbShape.Ellipse => Math.Max(s.B.X, s.B.Y) + glow,
            OrbShape.Stroke => s.B.X + s.B.Y,
            _ => Math.Max(s.B.X, s.B.Y) + Math.Max(s.Extra.Z, s.B.Z) + glow,
        } + 2f;
        var bounds = new SKRect(s.A.X - extent, s.A.Y - extent, s.A.X + extent, s.A.Y + extent);
        if (s.Shape is OrbShape.Arc or OrbShape.BandArc)
        {
            var points = new List<SKPoint>();
            var start = s.Extra.Z; var sweep = s.Extra.W;
            var inner = Math.Max(0, s.B.X - s.B.Y / 2); var outer = s.B.X + s.B.Y / 2;
            foreach (var radius in new[] { inner, outer })
            {
                points.Add(new(MathF.Cos(start) * radius, MathF.Sin(start) * radius));
                points.Add(new(MathF.Cos(start + sweep) * radius, MathF.Sin(start + sweep) * radius));
                for (var axis = 0; axis < 4; axis++)
                {
                    var angle = axis * MathF.PI / 2; var relative = (angle - start) % MathF.Tau;
                    if (relative < 0) relative += MathF.Tau;
                    if (relative <= sweep) points.Add(new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius));
                }
            }
            var pad = s.B.Y / 2 + glow + 2;
            bounds = new(s.A.X + points.Min(p => p.X) - pad, s.A.Y + points.Min(p => p.Y) - pad,
                s.A.X + points.Max(p => p.X) + pad, s.A.Y + points.Max(p => p.Y) + pad);
        }
        Draw(c, _shape, u, batch, bounds);
    }
    public void Liquid(SKCanvas c, OrbDrawList list, OrbBatch batch)
    {
        var u = Uniforms(_liquid, list, batch); var p = list.Liquid;
        var values = MemoryMarshal.CreateReadOnlySpan(ref p.Frame, 21);
        string[] names = ["lq_frame", "lq_surface", "lq_phases", "", "", "", "", "lq_liquid", "lq_deep", "lq_back", "lq_spill", "lq_glow",
            "lq_glow_geom", "lq_shade", "lq_current", "lq_current_geom", "lq_meniscus", "lq_meniscus_glow", "lq_meniscus_geom", "lq_gold", "lq_flags"];
        for (var i = 0; i < names.Length; i++) if (names[i].Length > 0) Set(u, names[i], values[i]);
        Set(u, "lq_ripples", values.Slice(3, 4));
        var r = p.Frame.W + 2;
        Draw(c, _liquid, u, batch, new(p.Frame.X - r, p.Frame.Y - r, p.Frame.X + r, p.Frame.Y + r));
    }
    public void Led(SKCanvas c, OrbDrawList list, OrbBatch batch)
    {
        var u = Uniforms(_led, list, batch); var p = list.Led;
        var values = MemoryMarshal.CreateReadOnlySpan(ref p.Frame, 14);
        string[] names = ["led_frame", "led_heat", "led_look", "led_silk", "led_halo", "led_peak", "led_tip"];
        for (var i = 0; i < names.Length; i++) Set(u, names[i], values[i]);
        Set(u, "led_waves", values.Slice(7, 4)); Set(u, "led_quads", values.Slice(11, 3));
        var r = p.Quad0.X;
        var bounds = batch.Mode == 0 ? new SKRect(p.Frame.X - r, p.Frame.Y - r, p.Frame.X + r, p.Frame.Y + r)
            : batch.Mode == 1 ? new(p.Quad1.X, p.Quad1.Y, p.Quad1.Z, p.Quad1.W) : new(p.Quad2.X, p.Quad2.Y, p.Quad2.Z, p.Quad2.W);
        Draw(c, _led, u, batch, bounds);
    }
    public void Dispose() { _paint.Dispose(); _paletteShader.Dispose(); _palette.Dispose(); _shape.Dispose(); _liquid.Dispose(); _led.Dispose(); }
}
