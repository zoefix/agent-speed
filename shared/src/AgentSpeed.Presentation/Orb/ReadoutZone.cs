namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// The readout zone (DESIGN 2a, Shared foundation): particles passing behind the readout fade to 35 % inside an ellipse
/// 0.30 S × 0.19 S around <c>c + (0, 0.07 S)</c> with a Gaussian falloff. Coordinates are relative to the disc center in
/// units of S.
/// </summary>
public static class ReadoutZone
{
    public const double Floor = 0.35;
    public const double HalfWidth = 0.15;
    public const double HalfHeight = 0.095;
    public const double CenterY = 0.07;

    /// <summary>Alpha multiplier (0.35..1) for a particle at (<paramref name="x"/>, <paramref name="y"/>) S from the center.</summary>
    public static double Fade(double x, double y)
    {
        var u = x / HalfWidth;
        var v = (y - CenterY) / HalfHeight;
        var d2 = u * u + v * v;
        return 1 - (1 - Floor) * Math.Exp(-2 * d2);
    }
}
