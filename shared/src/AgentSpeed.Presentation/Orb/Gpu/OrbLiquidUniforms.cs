using System.Numerics;
using System.Runtime.InteropServices;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>
/// Per-frame parameters of the <c>liquid</c> volume shader (DESIGN 2a.3), the layout of <c>LiquidParams</c> in the HLSL source
/// (21 × float4, a constant buffer). Positions and lengths are canvas points (y down); colors straight-alpha sRGB.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OrbLiquidUniforms
{
    /// <summary>Center x, center y, S, glass radius.</summary>
    public Vector4 Frame;

    /// <summary>Mean level y_L, wave amplitude, chop amplitude, back wave on (1/0).</summary>
    public Vector4 Surface;

    /// <summary>φ1, φ2, φ3, φ4 (radians).</summary>
    public Vector4 Phases;

    /// <summary>Ripple: impact x (canvas), front distance 0.42 S τ, amplitude (points), active (1/0).</summary>
    public Vector4 Ripple0;

    public Vector4 Ripple1;

    public Vector4 Ripple2;

    public Vector4 Ripple3;

    /// <summary>L (rgb), front alpha.</summary>
    public Vector4 Liquid;

    /// <summary>Darkened L (rgb), alpha at the bottom.</summary>
    public Vector4 Deep;

    /// <summary>Back wave color, alpha.</summary>
    public Vector4 Back;

    /// <summary>Spill color, alpha at the surface (0: none).</summary>
    public Vector4 Spill;

    /// <summary>Inner glow color, alpha at its center (0: none).</summary>
    public Vector4 Glow;

    /// <summary>Inner glow center x, center y, radius, additive (1/0).</summary>
    public Vector4 GlowGeom;

    /// <summary>Spherical shade color, alpha at the glass.</summary>
    public Vector4 Shade;

    /// <summary>Current filament color, alpha (0: none).</summary>
    public Vector4 Current;

    /// <summary>Unused, blur σ (points), drift phase, additive (1/0).</summary>
    public Vector4 CurrentGeom;

    /// <summary>Meniscus color, alpha.</summary>
    public Vector4 Meniscus;

    /// <summary>Meniscus glow color, alpha (0: none).</summary>
    public Vector4 MeniscusGlow;

    /// <summary>Meniscus width (points), glow σ (points), additive (1/0), unused.</summary>
    public Vector4 MeniscusGeom;

    /// <summary>New-peak gold band center y, alpha, half height (points), additive (1/0).</summary>
    public Vector4 Gold;

    /// <summary>Spill additive (1/0), scrim strength (0: none), body gradient top y, body gradient bottom y.</summary>
    public Vector4 Flags;

    public const int Size = 21 * 16;

    public void SetRipple(int index, Vector4 value)
    {
        switch (index)
        {
            case 0:
                Ripple0 = value;
                break;
            case 1:
                Ripple1 = value;
                break;
            case 2:
                Ripple2 = value;
                break;
            default:
                Ripple3 = value;
                break;
        }
    }
}
