namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// Atlas resolution steps (DESIGN 2a.6, Techniques): atlases are decoded at √2 steps of the drawn scale (at most 1 ×, at
/// least 1/16 ×), so a small size change reuses the texture and a big one decodes a sharper or lighter one.
/// </summary>
public static class CharacterAtlasSteps
{
    /// <summary>The step for <paramref name="scale"/> texture pixels per atlas pixel, rounded up (sharper).</summary>
    public static int Step(double scale)
    {
        var clamped = Math.Min(1, Math.Max(1.0 / 16, double.IsFinite(scale) ? scale : 1));
        return (int)Math.Ceiling(Math.Log2(clamped) * 2);
    }

    public static double Factor(int step) => Math.Min(1, Math.Pow(2, step / 2.0));

    /// <summary>
    /// Texture pixels per atlas pixel for a pose drawn at <paramref name="pointsPerPixel"/> screen points per pose pixel on
    /// a display of <paramref name="backingScale"/> pixels per point, with a little headroom for hover and hops.
    /// </summary>
    public static double Needed(double pointsPerPixel, double backingScale) => pointsPerPixel * backingScale * 1.15;
}
