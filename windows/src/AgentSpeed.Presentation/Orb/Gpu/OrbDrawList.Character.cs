using System.Numerics;
using System.Runtime.InteropServices;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>
/// A soft light of the character style (48 bytes, the layout of <c>Dot</c> in CharacterMesh.hlsl): an ellipse with radii
/// (rx, ry) around (x, y); coverage at normalized radius r is (1 − r)² for a glow (softness ≥ 0.99), else
/// 1 − smoothstep(1 − softness, 1, r). Straight-alpha color.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OrbCharacterDot
{
    public Vector4 Geometry;
    public Vector4 Color;
    public Vector4 Extra;

    public const int Stride = 48;

    public OrbCharacterDot(float x, float y, float rx, float ry, float r, float g, float b, float alpha, float softness)
    {
        Geometry = new Vector4(x, y, rx, ry);
        Color = new Vector4(r, g, b, alpha);
        Extra = new Vector4(softness, 0, 0, 0);
    }

    public OrbCharacterDot(in HudDot dot)
        : this(dot.X, dot.Y, dot.Rx, dot.Ry, dot.R, dot.G, dot.B, dot.Alpha, dot.Softness)
    {
    }
}

/// <summary>An atlas sprite (48 bytes, <c>Sprite</c> in CharacterMesh.hlsl): center, half size, atlas rect, rotation and opacity.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OrbCharacterSprite
{
    public Vector4 Geometry;
    public Vector4 Uv;
    public Vector4 Extra;

    public const int Stride = 48;
}

/// <summary>
/// One character pose to draw this frame (DESIGN 2a.6, layer 4): the pose (whose geometry and atlas the renderer keeps), the
/// atlas resolution step it wants, its bones' world transforms (two float4 rows per bone: a b tx, c d ty), its parts'
/// opacities, and its placement (the idle feet on screen, points per pose pixel, opacity; the pose's feet in its canvas).
/// Reused frame after frame.
/// </summary>
public sealed class OrbCharacterMeshDraw
{
    public const int MaxBones = 128;
    public const int MaxParts = 128;

    public CharacterPose? Pose { get; internal set; }

    /// <summary>Atlas pixels kept per source pixel: <c>min(1, 2^(step / 2))</c> (CharacterAtlasScale).</summary>
    public int AtlasStep { get; internal set; }

    public float[] Bones { get; } = new float[MaxBones * 8];

    public int BoneCount { get; internal set; }

    public float[] Opacities { get; } = new float[MaxParts];

    public int PartCount { get; internal set; }

    public Vector4 Placement { get; internal set; }

    public Vector2 Feet { get; internal set; }
}

public sealed partial class OrbDrawList
{
    private OrbCharacterDot[] _dots = new OrbCharacterDot[256];
    private OrbCharacterSprite[] _sprites = new OrbCharacterSprite[64];
    private readonly OrbCharacterMeshDraw[] _meshes = [new(), new(), new(), new()];

    public int CharacterDotCount { get; private set; }

    public int CharacterSpriteCount { get; private set; }

    public int CharacterMeshCount { get; private set; }

    public ReadOnlySpan<OrbCharacterDot> CharacterDots => _dots.AsSpan(0, CharacterDotCount);

    public ReadOnlySpan<OrbCharacterSprite> CharacterSprites => _sprites.AsSpan(0, CharacterSpriteCount);

    public ReadOnlySpan<OrbCharacterMeshDraw> CharacterMeshes => _meshes.AsSpan(0, CharacterMeshCount);

    private void ResetCharacter()
    {
        CharacterDotCount = 0;
        CharacterSpriteCount = 0;
        CharacterMeshCount = 0;
    }

    /// <summary>Adds a soft light (merged into the previous run of dots with the same blend).</summary>
    public void AddCharacterDot(in OrbCharacterDot dot, OrbBlend blend)
    {
        if (dot.Color.W * AlphaScale <= 0.002f || dot.Geometry.Z <= 0 || dot.Geometry.W <= 0)
        {
            return;
        }

        if (CharacterDotCount == _dots.Length)
        {
            Array.Resize(ref _dots, _dots.Length * 2);
        }

        ref var slot = ref _dots[CharacterDotCount];
        slot = dot;
        slot.Color.W *= AlphaScale;
        if (BatchCount > 0)
        {
            ref var last = ref LastBatch;
            if (last.Pass == OrbPass.CharacterDots && last.Blend == blend && last.Start + last.Count == CharacterDotCount)
            {
                last = last with { Count = last.Count + 1 };
                CharacterDotCount++;
                return;
            }
        }

        AddBatch(new OrbBatch(OrbPass.CharacterDots, blend, 0, 0, CharacterDotCount, 1));
        CharacterDotCount++;
    }

    /// <summary>
    /// Adds a pose: copies its bone transforms and part opacities. Returns the mesh index (for sprites that sample its
    /// atlas), or −1 when the list holds no more meshes or the rig is too large.
    /// </summary>
    public int AddCharacterMesh(CharacterPose pose, int atlasStep, ReadOnlySpan<Affine2> world, ReadOnlySpan<double> opacities,
        Vector4 placement, Vector2 feet)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (CharacterMeshCount == _meshes.Length || world.Length > OrbCharacterMeshDraw.MaxBones || opacities.Length > OrbCharacterMeshDraw.MaxParts)
        {
            return -1;
        }

        var draw = _meshes[CharacterMeshCount];
        draw.Pose = pose;
        draw.AtlasStep = atlasStep;
        draw.BoneCount = world.Length;
        var bones = draw.Bones;
        for (var i = 0; i < world.Length; i++)
        {
            ref readonly var m = ref world[i];
            var o = 8 * i;
            bones[o] = (float)m.A;
            bones[o + 1] = (float)m.B;
            bones[o + 2] = (float)m.Tx;
            bones[o + 3] = 0;
            bones[o + 4] = (float)m.C;
            bones[o + 5] = (float)m.D;
            bones[o + 6] = (float)m.Ty;
            bones[o + 7] = 0;
        }

        draw.PartCount = opacities.Length;
        for (var i = 0; i < opacities.Length; i++)
        {
            draw.Opacities[i] = (float)opacities[i];
        }

        draw.Placement = placement with { W = placement.W * AlphaScale };
        draw.Feet = feet;
        AddBatch(new OrbBatch(OrbPass.CharacterMesh, OrbBlend.Over, 0, 0, 0, 1, CharacterMeshCount));
        return CharacterMeshCount++;
    }

    /// <summary>Registers a pose's atlas for sprites without drawing the pose; returns its mesh index or −1.</summary>
    public int AddCharacterAtlas(CharacterPose pose, int atlasStep)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (CharacterMeshCount == _meshes.Length)
        {
            return -1;
        }

        var draw = _meshes[CharacterMeshCount];
        draw.Pose = pose;
        draw.AtlasStep = atlasStep;
        draw.BoneCount = 0;
        draw.PartCount = 0;
        draw.Placement = Vector4.Zero;
        draw.Feet = Vector2.Zero;
        return CharacterMeshCount++;
    }

    /// <summary>Adds an atlas sprite sampling mesh <paramref name="mesh"/>'s atlas (merged into the previous run of the same atlas).</summary>
    public void AddCharacterSprite(int mesh, in OrbCharacterSprite sprite)
    {
        if (mesh < 0 || sprite.Extra.Y * AlphaScale <= 0.002f)
        {
            return;
        }

        if (CharacterSpriteCount == _sprites.Length)
        {
            Array.Resize(ref _sprites, _sprites.Length * 2);
        }

        ref var slot = ref _sprites[CharacterSpriteCount];
        slot = sprite;
        slot.Extra.Y *= AlphaScale;
        if (BatchCount > 0)
        {
            ref var last = ref LastBatch;
            if (last.Pass == OrbPass.CharacterSprites && last.Mode == mesh && last.Start + last.Count == CharacterSpriteCount)
            {
                last = last with { Count = last.Count + 1 };
                CharacterSpriteCount++;
                return;
            }
        }

        AddBatch(new OrbBatch(OrbPass.CharacterSprites, OrbBlend.Over, 0, 0, CharacterSpriteCount, 1, mesh));
        CharacterSpriteCount++;
    }

    /// <summary>
    /// Adds a text quad drawn in sequence with the shapes (inline), so what is added after it covers it: rotated by
    /// <paramref name="rotation"/> (radians) about (<paramref name="pivotX"/>, <paramref name="pivotY"/>) and scaled by
    /// <paramref name="scale"/> about the same pivot. Only before any overlay text of the frame.
    /// </summary>
    public void AddInlineText(in OrbGlyph glyph, double x, double baseline, Rgba color, Rgba? glow, OrbTextClip clip, double scale, double rotation,
        double pivotX, double pivotY)
    {
        if (glyph.IsEmpty || (color.A <= 0.004 && (glow is null || glow.Value.A <= 0.004)))
        {
            return;
        }

        if (TextCount != OverlayTextStart)
        {
            throw new InvalidOperationException("Inline text must be added before the overlay text.");
        }

        var left = pivotX + (x + glyph.QuadLeft - pivotX) * scale;
        var top = pivotY + (baseline + glyph.QuadTop - pivotY) * scale;
        var before = TextCount;
        AddText(new OrbTextInstance
        {
            Rect = new Vector4((float)left, (float)top, (float)(glyph.QuadWidth * scale), (float)(glyph.QuadHeight * scale)),
            Uv = new Vector4(glyph.U0, glyph.V0, glyph.U1, glyph.V1),
            Color = OrbGpuInstance.ToVector(color),
            Glow = glow is { } g ? OrbGpuInstance.ToVector(g) : Vector4.Zero,
            Params = new Vector4(0, 0, 0, (float)clip.Mode),
            Clip = new Vector4(clip.X0, clip.Y0, clip.X1, clip.Y1),
            Rotate = new Vector4((float)rotation, (float)pivotX, (float)pivotY, 0),
        });
        OverlayTextStart = TextCount;
        if (BatchCount > 0)
        {
            ref var last = ref LastBatch;
            if (last.Pass == OrbPass.Text && last.Start + last.Count == before)
            {
                last = last with { Count = last.Count + 1 };
                return;
            }
        }

        AddBatch(new OrbBatch(OrbPass.Text, OrbBlend.Over, 0, 0, before, 1));
    }

    private ref OrbBatch LastBatch => ref _batches[BatchCount - 1];

    private ulong MixCharacter(ulong hash)
    {
        if (CharacterDotCount > 0)
        {
            hash = Mix(hash, MemoryMarshal.AsBytes(CharacterDots));
        }

        if (CharacterSpriteCount > 0)
        {
            hash = Mix(hash, MemoryMarshal.AsBytes(CharacterSprites));
        }

        for (var i = 0; i < CharacterMeshCount; i++)
        {
            var m = _meshes[i];
            hash = Mix(hash, MemoryMarshal.AsBytes(m.Bones.AsSpan(0, m.BoneCount * 8)));
            hash = Mix(hash, MemoryMarshal.AsBytes(m.Opacities.AsSpan(0, m.PartCount)));
            Span<float> place = [m.Placement.X, m.Placement.Y, m.Placement.Z, m.Placement.W, m.Feet.X, m.Feet.Y, m.AtlasStep,
                m.Pose?.GetHashCode() ?? 0];
            hash = Mix(hash, MemoryMarshal.AsBytes(place));
        }

        return hash;
    }
}
