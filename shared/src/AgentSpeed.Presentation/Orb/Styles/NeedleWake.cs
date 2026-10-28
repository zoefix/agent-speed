namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>One wedge of the stacked wake: from the needle back <see cref="TrailDegrees"/>, drawn at <see cref="Alpha"/>.</summary>
public readonly record struct NeedleWakeLayer(double TrailDegrees, double Alpha);

/// <summary>
/// The needle's wake (DESIGN 2a.2, layer 8) as stacked annular wedges that all start at the needle: wedge <c>k</c>
/// reaches back <c>L (1 − k/n)</c>, so the composite rises linearly from 0 at the tail to the head alpha at the needle
/// in steps of <see cref="SliceDegrees"/>, without the seams of abutting slices.
/// </summary>
public static class NeedleWake
{
    public const double SliceDegrees = 2.5;

    public const int MaxLayers = 24;

    /// <summary>Wake lengths are drawn in whole slices (≤ 55° fits <see cref="MaxLayers"/>).</summary>
    public static int SliceCount(double lengthDegrees) =>
        lengthDegrees > 0 && double.IsFinite(lengthDegrees)
            ? Math.Clamp((int)Math.Round(lengthDegrees / SliceDegrees), 1, MaxLayers)
            : 0;

    /// <summary>Fills the wedges (longest first) of a wake of <paramref name="slices"/> slices; returns their number.</summary>
    public static int Layers(int slices, double headAlpha, Span<NeedleWakeLayer> layers)
    {
        var n = Math.Clamp(slices, 0, MaxLayers);
        headAlpha = StyleMath.Clamp01(headAlpha);
        if (n == 0 || headAlpha <= 0)
        {
            return 0;
        }

        if (layers.Length < n)
        {
            throw new ArgumentException($"At least {n} layers are needed.", nameof(layers));
        }

        Span<double> targets = stackalloc double[MaxLayers];
        for (var j = 0; j < n; j++)
        {
            targets[j] = headAlpha * (j + 0.5) / n;
        }

        Span<double> alphas = stackalloc double[MaxLayers];
        NeedleGlow.StackAlphas(targets[..n], alphas);
        for (var k = 0; k < n; k++)
        {
            layers[k] = new NeedleWakeLayer(SliceDegrees * (n - k), alphas[k]);
        }

        return n;
    }
}
