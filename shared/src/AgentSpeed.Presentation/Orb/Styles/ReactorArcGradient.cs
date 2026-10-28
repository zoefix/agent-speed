namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// A rotor arc's tail-to-head gradient as one linear gradient along its chord (DESIGN 2a.4): for an arc shorter than
/// 180° the projection of its points onto the chord is monotonic, so gradient stops placed at the projected positions
/// make the color run evenly along the arc with a single stroke instead of many segments.
/// </summary>
public static class ReactorArcGradient
{
    /// <summary>Longest arc the projection stays usable for.</summary>
    public const double MaxLengthDegrees = 179;

    /// <summary>
    /// Position (0 at the tail, 1 at the head) on the tail→head chord of the arc point at fraction <paramref name="u"/>
    /// of an arc <paramref name="lengthDegrees"/> long.
    /// </summary>
    public static double ChordOffset(double u, double lengthDegrees)
    {
        u = Math.Clamp(double.IsFinite(u) ? u : 0, 0, 1);
        var half = Math.Clamp(double.IsFinite(lengthDegrees) ? lengthDegrees : 0, 0.01, MaxLengthDegrees) / 2 * Math.PI / 180;
        return Math.Clamp(0.5 + Math.Sin((2 * u - 1) * half) / (2 * Math.Sin(half)), 0, 1);
    }
}
