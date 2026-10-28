using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// One graduation of the <c>needle</c> dial for the current frame. A round-capped line at <see cref="Degrees"/> from
/// <see cref="InnerRadius"/> to <see cref="OuterRadius"/> (S), <see cref="Width"/> (S) wide, in <see cref="Color"/>
/// (its alpha is the opacity). Lit majors in dark glow with σ <see cref="GlowSigma"/> (S) at <see cref="GlowOpacity"/>.
/// </summary>
public readonly record struct NeedleTick(
    int Index,
    double Dial,
    double Degrees,
    double InnerRadius,
    double OuterRadius,
    double Width,
    bool Major,
    bool Visible,
    bool Lit,
    bool Leading,
    Rgba Color,
    double GlowSigma,
    double GlowOpacity);

/// <summary>
/// One shift light: a housing disc of radius <see cref="NeedleGeometry.LampSize"/> at (<see cref="X"/>, <see cref="Y"/>)
/// (S from the center) in <see cref="NeedlePalette.LampHousing"/>, and the lamp itself (radius × 0.86) in the opaque
/// <see cref="Color"/> at <see cref="Opacity"/>, with a glow of σ <see cref="GlowSigma"/> (S, 0 = none) at
/// <see cref="GlowOpacity"/>. <see cref="Color"/> changes only with <see cref="NeedleModel.LampKey"/>.
/// </summary>
public readonly record struct NeedleLamp(double X, double Y, double Degrees, Rgba Color, double Opacity, double GlowSigma, double GlowOpacity);

/// <summary>
/// A live peg spark: a streak from the tail (<see cref="X"/> + <see cref="StreakX"/>, <see cref="Y"/> +
/// <see cref="StreakY"/>) to the head (<see cref="X"/>, <see cref="Y"/>). Positions are in S from the disc center,
/// <see cref="Width"/> is in DIP (multiply by the DPI scale for device pixels). <see cref="Color"/> is opaque;
/// <see cref="Alpha"/> already includes the life fade and the readout zone.
/// </summary>
public readonly record struct NeedleSpark(float X, float Y, float StreakX, float StreakY, float Width, Rgba Color, float Alpha);
