using System.Numerics;
using System.Runtime.InteropServices;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>
/// Per-frame parameters of the <c>led</c> style's per-pixel layers (DESIGN 2.1), the layout of <c>LedParams</c> in the HLSL
/// source (14 × float4, a constant buffer). Shader coordinates are the prototype's: orb units with the rim at r = 0.5, y up.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OrbLedUniforms
{
    /// <summary>Center x, center y (canvas points), S (points), alpha of every pass (crossfades).</summary>
    public Vector4 Frame;

    /// <summary>h, o, act = smoothstep(0, 0.08, h), lvl.</summary>
    public Vector4 Heat;

    /// <summary>Dark (1/0), silk brightness before the flash, wave and sweep, ripple amount, response waves move the cloth (1/0).</summary>
    public Vector4 Look;

    /// <summary>Silk fold phase, silk noise z, light phase, clock (ripple and wave patterns).</summary>
    public Vector4 Silk;

    /// <summary>Corona phase, warp phase, corona amount, warp amount.</summary>
    public Vector4 Halo;

    /// <summary>Seconds since the new peak (≥ 1.4: none), tier flash 0..1, tip flare amount, motion (1/0: the rays and the growing ring; 0 with Reduce Motion).</summary>
    public Vector4 Peak;

    /// <summary>Leading-tip angle (radians, clockwise on screen from 3 o'clock), value box y, unit box y (orb units, y up), unused.</summary>
    public Vector4 Tip;

    /// <summary>The newest four response waves, oldest first: progress 0..1, strength, heat, active (1/0).</summary>
    public Vector4 Wave0;

    public Vector4 Wave1;

    public Vector4 Wave2;

    public Vector4 Wave3;

    /// <summary>Quad of mode 0 (the corona ring's outer radius, points, in x; the rest unused).</summary>
    public Vector4 Quad0;

    /// <summary>Quad of mode 1: min x, min y, max x, max y (canvas points).</summary>
    public Vector4 Quad1;

    /// <summary>Quad of mode 2: min x, min y, max x, max y (canvas points).</summary>
    public Vector4 Quad2;

    public const int Size = 14 * 16;

    /// <summary>Vertices of a mode: the corona ring is <see cref="RingWedges"/> wedges, the others one quad.</summary>
    public static int VertexCount(int mode) => mode == 0 ? RingWedges * 6 : 6;

    /// <summary>
    /// The corona ring's wedges: the slow body noise is evaluated per vertex and interpolated, so the corona needs three noise
    /// evaluations per pixel (DESIGN 2.1, Budget).
    /// </summary>
    public const int RingWedges = 128;

    public void SetWave(int index, Vector4 value)
    {
        switch (index)
        {
            case 0:
                Wave0 = value;
                break;
            case 1:
                Wave1 = value;
                break;
            case 2:
                Wave2 = value;
                break;
            default:
                Wave3 = value;
                break;
        }
    }
}
