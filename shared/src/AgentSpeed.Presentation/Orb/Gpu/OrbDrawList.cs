using System.Numerics;
using System.Runtime.InteropServices;
using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>Blending of a run of shapes: light adds up on dark glass and blends normally on porcelain.</summary>
public enum OrbBlend
{
    Over,
    Additive,
}

/// <summary>A pass of the draw list.</summary>
public enum OrbPass
{
    Shapes,

    /// <summary>The liquid volume shader (<see cref="OrbBatch.Mode"/> 0 the volume, 1 the meniscus).</summary>
    Liquid,

    /// <summary>
    /// The <c>led</c> style's per-pixel layers (DESIGN 2.1, <see cref="OrbLedUniforms"/>): <see cref="OrbBatch.Mode"/> 0 the
    /// corona and warp streaks, 1 the silk core, 2 the tip flare and new-peak flash, ring and rays.
    /// </summary>
    Led,

    /// <summary>Text quads drawn in sequence with the shapes (under what follows), <see cref="OrbBatch.Start"/> into the texts.</summary>
    Text,
}

/// <summary>A run of instances drawn with one pipeline state.</summary>
/// <param name="Clip">Clip radius around the canvas center in points (0: none).</param>
/// <param name="Feather">Width of a soft clip edge inside <paramref name="Clip"/> (0: an anti-aliased hard edge).</param>
/// <param name="Mode">The pass's mode (<see cref="OrbPass.Liquid"/>, <see cref="OrbPass.Led"/>).</param>
public readonly record struct OrbBatch(OrbPass Pass, OrbBlend Blend, float Clip, float Feather, int Start, int Count, int Mode = 0);

/// <summary>
/// One frame of GPU work for one orb, back to front: shape batches, then every text quad on top (DESIGN 2a: the readout is
/// always drawn above every effect), then the batches added after <see cref="BeginOverlay"/> (the digit lightning, DESIGN
/// 2.1). The storage is reused frame after frame, so a warm list never allocates.
/// </summary>
public sealed class OrbDrawList
{
    private OrbGpuInstance[] _instances;
    private OrbBatch[] _batches = new OrbBatch[32];
    private OrbTextInstance[] _texts = new OrbTextInstance[48];

    public OrbDrawList(int capacity = 512)
    {
        _instances = new OrbGpuInstance[Math.Max(16, capacity)];
    }

    public int Count { get; private set; }

    public int BatchCount { get; private set; }

    public int TextCount { get; private set; }

    public ReadOnlySpan<OrbGpuInstance> Instances => _instances.AsSpan(0, Count);

    public ReadOnlySpan<OrbBatch> Batches => _batches.AsSpan(0, BatchCount);

    public ReadOnlySpan<OrbTextInstance> Texts => _texts.AsSpan(0, TextCount);

    /// <summary>The first text quad of the final overlay pass; the ones before it are drawn by <see cref="OrbPass.Text"/> batches.</summary>
    public int OverlayTextStart { get; private set; }

    /// <summary>Parameters of the liquid volume when a liquid pass is present.</summary>
    public OrbLiquidUniforms Liquid;

    public bool HasLiquid { get; private set; }

    /// <summary>Parameters of the <c>led</c> passes when one is present.</summary>
    public OrbLedUniforms Led;

    public bool HasLed { get; private set; }

    /// <summary>The first batch drawn after the overlay text, or −1 when every batch is drawn before it.</summary>
    public int OverlayBatchStart { get; private set; } = -1;

    /// <summary>Canvas side (points) and pixels per point of the target.</summary>
    public double Canvas { get; set; }

    public double Scale { get; set; } = 1;

    public double CenterX { get; set; }

    public double CenterY { get; set; }

    /// <summary>Scale of everything around the canvas center (the 1.03 hover scale, the appear and vanish motion).</summary>
    public double ContentScale { get; set; } = 1;

    /// <summary>Opacity of the whole orb (the unavailable dim, the idle fade, appear and vanish).</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Multiplies the alpha of everything added while set (style crossfades).</summary>
    public float AlphaScale { get; set; } = 1;

    /// <summary>
    /// Clip radius around the canvas center for shapes added from now on (0: none; negative: keep only what lies outside the
    /// circle of that radius, e.g. the drop shadow that must never darken the glass).
    /// </summary>
    public double ClipRadius { get; set; }

    /// <summary>Soft edge of that clip: clear at <see cref="ClipRadius"/>, solid this far inside it.</summary>
    public double ClipFeather { get; set; }

    public void Reset()
    {
        Count = 0;
        BatchCount = 0;
        TextCount = 0;
        OverlayTextStart = 0;
        OverlayBatchStart = -1;
        HasLiquid = false;
        HasLed = false;
        AlphaScale = 1;
        ClipRadius = 0;
        ClipFeather = 0;
        ContentScale = 1;
        Opacity = 1;
    }

    public void Add(in OrbGpuInstance instance, OrbBlend blend = OrbBlend.Over)
    {
        if (Count == _instances.Length)
        {
            Array.Resize(ref _instances, _instances.Length * 2);
        }

        ref var slot = ref _instances[Count];
        slot = instance;
        if (AlphaScale < 1)
        {
            slot.ScaleAlpha(AlphaScale);
        }

        var clip = (float)ClipRadius;
        var feather = (float)ClipFeather;
        if (BatchCount > 0)
        {
            ref var last = ref _batches[BatchCount - 1];
            if (last.Pass == OrbPass.Shapes && last.Blend == blend && last.Clip == clip && last.Feather == feather
                && last.Start + last.Count == Count && BatchCount - 1 >= OverlayBatchStart)
            {
                last = last with { Count = last.Count + 1 };
                Count++;
                return;
            }
        }

        AddBatch(new OrbBatch(OrbPass.Shapes, blend, clip, feather, Count, 1));
        Count++;
    }

    /// <summary>Adds a pass of the liquid volume shader (mode 0 the volume, 1 the meniscus) using <see cref="Liquid"/>.</summary>
    public void AddLiquid(int mode)
    {
        HasLiquid = true;
        AddBatch(new OrbBatch(OrbPass.Liquid, OrbBlend.Over, 0, 0, Count, 0, mode));
    }

    /// <summary>Adds a pass of the <c>led</c> shader in <paramref name="mode"/> using <see cref="Led"/>.</summary>
    public void AddLed(int mode, OrbBlend blend)
    {
        HasLed = true;
        AddBatch(new OrbBatch(OrbPass.Led, blend, 0, 0, Count, 0, mode));
    }

    /// <summary>What is added from now on is drawn above the readout text (once per frame; later calls keep the first start).</summary>
    public void BeginOverlay()
    {
        if (OverlayBatchStart < 0)
        {
            OverlayBatchStart = BatchCount;
        }
    }

    /// <summary>
    /// Adds a rasterized string drawn in sequence with the shapes, so the shapes added after it cover it (the needle's dial
    /// legend under its graduations and needle). Only before any overlay text of the frame.
    /// </summary>
    public void AddInlineText(in OrbGlyph glyph, double x, double baseline, Rgba color)
    {
        if (TextCount != OverlayTextStart)
        {
            throw new InvalidOperationException("Inline text must be added before the overlay text.");
        }

        var before = TextCount;
        AddText(glyph, x, baseline, color);
        if (TextCount == before)
        {
            return;
        }

        OverlayTextStart = TextCount;
        if (BatchCount > 0)
        {
            ref var last = ref _batches[BatchCount - 1];
            if (last.Pass == OrbPass.Text && last.Start + last.Count == before)
            {
                last = last with { Count = last.Count + 1 };
                return;
            }
        }

        AddBatch(new OrbBatch(OrbPass.Text, OrbBlend.Over, 0, 0, before, 1));
    }

    public void AddText(in OrbTextInstance text)
    {
        if (TextCount == _texts.Length)
        {
            Array.Resize(ref _texts, _texts.Length * 2);
        }

        ref var slot = ref _texts[TextCount++];
        slot = text;
        if (AlphaScale < 1)
        {
            slot.Color.W *= AlphaScale;
            slot.Glow.W *= AlphaScale;
            slot.Fill2.W *= AlphaScale;
        }
    }

    /// <summary>
    /// Adds a rasterized string with its origin (left end of the baseline) at (<paramref name="x"/>, <paramref name="baseline"/>),
    /// scaled by <paramref name="scale"/> about (<paramref name="pivotX"/>, <paramref name="pivotY"/>) and sheared by
    /// <paramref name="shear"/> (dx per dy) about the line y = <paramref name="shearPivot"/>.
    /// </summary>
    public void AddText(in OrbGlyph glyph, double x, double baseline, Rgba color, Rgba? glow = null, OrbTextClip clip = default,
        double shear = 0, double shearPivot = 0, double scale = 1, double pivotX = 0, double pivotY = 0, Rgba? bottomColor = null,
        double gradientTop = 0, double gradientBottom = 0)
    {
        if (glyph.IsEmpty || color.A <= 0.004 && (glow is null || glow.Value.A <= 0.004))
        {
            return;
        }

        var left = x + glyph.QuadLeft;
        var top = baseline + glyph.QuadTop;
        var width = (double)glyph.QuadWidth;
        var height = (double)glyph.QuadHeight;
        if (scale != 1)
        {
            left = pivotX + (left - pivotX) * scale;
            top = pivotY + (top - pivotY) * scale;
            width *= scale;
            height *= scale;
        }

        var instance = new OrbTextInstance
        {
            Rect = new Vector4((float)left, (float)top, (float)width, (float)height),
            Uv = new Vector4(glyph.U0, glyph.V0, glyph.U1, glyph.V1),
            Color = OrbGpuInstance.ToVector(color),
            Glow = glow is { } g ? OrbGpuInstance.ToVector(g) : Vector4.Zero,
            Params = new Vector4((float)shear, (float)shearPivot, 0, (float)clip.Mode),
            Clip = new Vector4(clip.X0, clip.Y0, clip.X1, clip.Y1),
            Fill2 = bottomColor is { } bottom ? OrbGpuInstance.ToVector(bottom) : Vector4.Zero,
            Gradient = bottomColor is null ? Vector4.Zero : new Vector4((float)gradientTop, (float)gradientBottom, 1, 0),
        };
        AddText(instance);
    }

    /// <summary>A 64-bit fingerprint of everything the frame draws, so an identical frame is never presented twice.</summary>
    public ulong Fingerprint()
    {
        var hash = 14695981039346656037UL;
        hash = Mix(hash, MemoryMarshal.AsBytes(Instances));
        hash = Mix(hash, MemoryMarshal.AsBytes(Texts));
        hash = Mix(hash, MemoryMarshal.AsBytes(Batches));
        if (HasLiquid)
        {
            hash = Mix(hash, MemoryMarshal.AsBytes(new ReadOnlySpan<OrbLiquidUniforms>(in Liquid)));
        }

        if (HasLed)
        {
            hash = Mix(hash, MemoryMarshal.AsBytes(new ReadOnlySpan<OrbLedUniforms>(in Led)));
        }

        Span<double> globals = [Canvas, Scale, CenterX, CenterY, ContentScale, Opacity, OverlayBatchStart];
        return Mix(hash, MemoryMarshal.AsBytes(globals));
    }

    private static ulong Mix(ulong hash, ReadOnlySpan<byte> bytes)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(bytes);
        foreach (var word in words)
        {
            hash = (hash ^ word) * 1099511628211UL;
            hash ^= hash >> 29;
        }

        for (var i = words.Length * 8; i < bytes.Length; i++)
        {
            hash = (hash ^ bytes[i]) * 1099511628211UL;
        }

        return hash;
    }

    private void AddBatch(in OrbBatch batch)
    {
        if (BatchCount == _batches.Length)
        {
            Array.Resize(ref _batches, _batches.Length * 2);
        }

        _batches[BatchCount++] = batch;
    }
}
