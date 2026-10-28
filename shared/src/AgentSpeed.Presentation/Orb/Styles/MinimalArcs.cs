namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// One piece of an arc split for a gradient: from <see cref="FromDegrees"/> to <see cref="ToDegrees"/>, colored at
/// <see cref="Fraction"/> (0..1 along the whole arc, at the piece's middle). <see cref="Full"/> pieces span a whole
/// grid step, so a renderer can reuse their geometry.
/// </summary>
public readonly record struct MinimalArcSegment(double FromDegrees, double ToDegrees, double Fraction, bool Full);

/// <summary>
/// The first lap's conic gradient of the <c>minimal</c> ring (DESIGN 2a.5) approximated by arc pieces on a fixed grid
/// of <see cref="StepDegrees"/> from the start angle: every piece but the last spans one whole step, each piece takes
/// the gradient's color at its middle.
/// </summary>
public static class MinimalArcs
{
    public const double StepDegrees = 7.5;

    /// <summary>Pieces in a whole lap.</summary>
    public const int LapSegments = 48;

    public static int SegmentCount(double sweepDegrees, double step = StepDegrees)
    {
        if (!(sweepDegrees > 0) || !double.IsFinite(sweepDegrees) || !(step > 0))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(sweepDegrees / step - 1e-9));
    }

    public static MinimalArcSegment Segment(double startDegrees, double sweepDegrees, int index, double step = StepDegrees)
    {
        var from = index * step;
        var gridEnd = (index + 1) * step;
        var to = Math.Min(sweepDegrees, gridEnd);
        var fraction = sweepDegrees > 0 ? StyleMath.Clamp01((from + to) / 2 / sweepDegrees) : 0;
        return new MinimalArcSegment(startDegrees + from, startDegrees + to, fraction, to >= gridEnd - 1e-9);
    }
}
