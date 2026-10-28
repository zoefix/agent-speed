namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// Where a renderer samples the liquid surface (DESIGN 2a.3): only across the part of the glass the surface can reach,
/// so a few dozen samples keep their resolution at every level. Units of S from the disc center, y down.
/// </summary>
public static class LiquidSurfaceSampling
{
    /// <summary>The largest ripple displacement: <c>0.022 (0.6 + 0.6 × 1.25)</c>.</summary>
    public const double MaxRipple = 0.022 * (0.6 + 0.6 * 1.25);

    /// <summary>The back wave sits this much above the front surface.</summary>
    public const double BackRaise = 0.008;

    private const double R = LiquidGeometry.GlassRadius;

    /// <summary>How far the surface (front and back wave) can stray from its mean height.</summary>
    public static double Margin(double waveAmplitude, double chopAmplitude, bool ripples) =>
        Math.Max(0, waveAmplitude) + Math.Max(0, chopAmplitude) + (ripples ? MaxRipple : 0) + BackRaise + 0.004;

    /// <summary>
    /// Half the width of the band of x where a surface within <paramref name="margin"/> of <paramref name="meanY"/> is
    /// inside the glass; beyond it every such surface point lies on or outside the glass circle.
    /// </summary>
    public static double VisibleHalfWidth(double meanY, double margin)
    {
        var nearest = Math.Abs(meanY) - Math.Max(0, margin);
        if (!(nearest > 0))
        {
            return R;
        }

        return nearest >= R ? 0 : Math.Min(R, Math.Sqrt(R * R - nearest * nearest));
    }

    /// <summary>Fills <paramref name="xs"/> with evenly spaced x from −<paramref name="half"/> to +<paramref name="half"/>.</summary>
    public static void FillSampleX(Span<double> xs, double half)
    {
        if (xs.Length == 1)
        {
            xs[0] = 0;
            return;
        }

        for (var i = 0; i < xs.Length; i++)
        {
            xs[i] = -half + 2 * half * i / (xs.Length - 1);
        }
    }
}
