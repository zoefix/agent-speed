using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>A point in units of S relative to the disc center (y down).</summary>
public readonly record struct ReactorPoint(double X, double Y);

/// <summary>
/// One lightning bolt (DESIGN 2a.4, layer 8). Its polyline is the <see cref="ReactorModel.BoltPointCount"/> points of
/// <see cref="ReactorModel.BoltPoints"/> starting at <see cref="PathOffset"/> (see <see cref="ReactorModel.BoltPath"/>);
/// <see cref="Version"/> changes when they are re-randomized (every 50 ms). Three strokes: glow, body and core
/// (<see cref="ReactorGeometry"/> widths, <see cref="ReactorModel.BoltColor"/> / <see cref="ReactorModel.BoltCoreColor"/>),
/// at 28 % / 75 % / 100 % of <see cref="Alpha"/>, additive in dark.
/// </summary>
public readonly record struct ReactorBolt(double Alpha, bool Outward, int PathOffset, int Version);

/// <summary>A spark at a bolt's far end: a soft dot (S units), color and final opacity (readout fade included).</summary>
public readonly record struct ReactorSpark(double X, double Y, double Radius, Rgba Color, double Alpha);

/// <summary>Fixed geometry of the reactor style in units of S (DESIGN 2a.4); the renderer caches these once per size.</summary>
public static class ReactorGeometry
{
    public const double StatorRadius = 0.468;
    public const double StatorWidth = 0.006;
    public const double NotchInner = 0.456;
    public const double NotchOuter = 0.474;
    public const double NotchWidth = 0.005;

    public const double RotorARadius = 0.425;
    public const double RotorAWidth = 0.024;
    public const int RotorAArcs = 3;

    public const double RotorBRadius = 0.383;
    public const double RotorBWidth = 0.018;

    public const double RotorCRadius = 0.338;
    public const double RotorCWidth = 0.009;
    public const int RotorCArcs = 2;

    /// <summary>Corona: radial gradient between these radii (color at the inner radius fading to 0).</summary>
    public const double CoronaInner = 0.30;
    public const double CoronaOuter = 0.40;

    /// <summary>Core window disc; its radial gradient runs from c − (0, 0.08 S) at r 0.02 S to c at r 0.30 S.</summary>
    public const double CoreRadius = 0.30;
    public const double CoreRimWidth = 0.009;
    public const double CoreGradientOffsetY = -0.08;
    public const double CoreGradientInner = 0.02;

    public const double BoltInnerStart = 0.306;
    public const double BoltInnerEnd = 0.425;
    public const double BoltOuterStart = 0.437;
    public const double BoltOuterMin = 0.56;
    public const double BoltOuterMax = 0.66;
    public const double BoltGlowWidth = 0.03;
    public const double BoltBodyWidth = 0.011;
    public const double BoltCoreWidth = 0.0045;

    public const double FlareStart = 0.30;
    public const double FlareEnd = 0.50;
    public const double FlareWidth = 0.012;
}
