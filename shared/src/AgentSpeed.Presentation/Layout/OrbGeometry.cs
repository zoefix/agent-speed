namespace AgentSpeed.Presentation.Layout;

/// <summary>A floating orb's disc in physical pixels (virtual-screen coordinates, y down).</summary>
public readonly record struct OrbDisc(PointD Center, double Radius);

/// <summary>A 1 pt dashed alignment guide while dragging, as a segment in physical pixels.</summary>
public readonly record struct OrbGuide(PointD Start, PointD End)
{
    /// <summary>True for a guide along a shared center x, false for one along a shared center y.</summary>
    public bool IsVertical => Start.X == End.X;
}

/// <summary>The work-area edge a disc is docked to.</summary>
public enum OrbEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>
/// Shared constants and tests of the multi-orb geometry (DESIGN 2, 2b). Distances are in pt/DIP and multiplied by the
/// display scale; every position is a disc center in physical pixels.
/// </summary>
public static class OrbGeometry
{
    /// <summary>Released this close to a work-area edge, the disc snaps to <see cref="Margin"/>.</summary>
    public const double SnapThreshold = 24;

    /// <summary>Distance from the work-area edges a placed disc keeps.</summary>
    public const double Margin = 12;

    /// <summary>Edge-to-edge gap of touching orbs, also the clearance every orb keeps from the others.</summary>
    public const double Gap = 12;

    /// <summary>Step of the free-spot search and the cascade.</summary>
    public const double Grid = 24;

    /// <summary>Margin argument of <see cref="PlacementMath.DefaultOrbCenter"/> for today's default position.</summary>
    public const double DefaultInset = 24;

    /// <summary>Within this distance of a touching position the dragged disc is pulled in.</summary>
    public const double PullDistance = 16;

    /// <summary>Center offset within which the dragged disc aligns with another orb.</summary>
    public const double AlignDistance = 8;

    /// <summary>Largest edge-to-edge distance between two discs that still align.</summary>
    public const double AlignReach = 64;

    /// <summary>Rounding allowance in pixels, so touching or docked positions count as such.</summary>
    public const double Tolerance = 0.5;

    /// <summary>A usable display scale (finite, 0.25–8); anything else is 1.</summary>
    public static double ValidScale(double scale) => double.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, 0.25, 8) : 1;

    public static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The two discs keep at least the 12 pt gap.</summary>
    public static bool IsClear(PointD center, double radius, OrbDisc other, double scale) =>
        Distance(center, other.Center) >= radius + other.Radius + Gap * ValidScale(scale) - Tolerance;

    /// <summary>The disc keeps the 12 pt gap from every other disc.</summary>
    public static bool IsFree(PointD center, double radius, IReadOnlyList<OrbDisc> others, double scale)
    {
        ArgumentNullException.ThrowIfNull(others);
        foreach (var other in others)
        {
            if (!IsClear(center, radius, other, scale))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The disc lies inside the work area with the 12 pt margin.</summary>
    public static bool Fits(PointD center, double radius, RectD work, double scale)
    {
        var inset = radius + Margin * ValidScale(scale) - Tolerance;
        return center.X - inset >= work.X && center.X + inset <= work.Right
            && center.Y - inset >= work.Y && center.Y + inset <= work.Bottom;
    }

    /// <summary>Inside the work area with the 12 pt margin (centered on an axis that is too small).</summary>
    public static PointD Clamp(PointD center, double radius, RectD work, double scale) =>
        PlacementMath.ClampDisc(center, radius, work, Margin * ValidScale(scale));

    /// <summary>Today's default position (bottom-right of the work area), used when an orb has no record.</summary>
    public static PointD DefaultCenter(RectD work, double radius, double scale) =>
        PlacementMath.DefaultOrbCenter(work, radius, DefaultInset * ValidScale(scale));

    /// <summary>Today's release rule: within 24 pt of a work-area edge the disc moves to the 12 pt margin.</summary>
    public static PointD SnapToEdges(PointD center, double radius, RectD work, double scale)
    {
        var s = ValidScale(scale);
        return PlacementMath.Snap(center, radius, work, SnapThreshold * s, Margin * s);
    }

    /// <summary>Whether each axis is within the snap threshold of one of its work-area edges.</summary>
    public static (bool X, bool Y) NearEdges(PointD center, double radius, RectD work, double scale)
    {
        var threshold = SnapThreshold * ValidScale(scale);
        var (left, right, top, bottom) = EdgeDistances(new OrbDisc(center, radius), work);
        return (Math.Min(left, right) < threshold, Math.Min(top, bottom) < threshold);
    }

    /// <summary>The edge the disc is within the snap threshold of: left or right first, then top or bottom.</summary>
    public static OrbEdge? DockedEdge(OrbDisc disc, RectD work, double scale)
    {
        var threshold = SnapThreshold * ValidScale(scale);
        var (left, right, top, bottom) = EdgeDistances(disc, work);
        if (Math.Min(left, right) < threshold)
        {
            return left <= right ? OrbEdge.Left : OrbEdge.Right;
        }

        if (Math.Min(top, bottom) < threshold)
        {
            return top <= bottom ? OrbEdge.Top : OrbEdge.Bottom;
        }

        return null;
    }

    /// <summary>
    /// The center after a size change: an axis docked to an edge (disc within the 12 pt margin, plus 0.5 px) keeps its
    /// edge distance, any other axis keeps its center; always inside the work area with the margin.
    /// </summary>
    public static PointD Resized(PointD center, double oldRadius, double newRadius, RectD work, double scale)
    {
        var docked = Margin * ValidScale(scale) + Tolerance;
        var (left, right, top, bottom) = EdgeDistances(new OrbDisc(center, oldRadius), work);
        var x = center.X;
        var y = center.Y;
        if (left <= docked && left <= right)
        {
            x = work.X + Math.Max(0, left) + newRadius;
        }
        else if (right <= docked)
        {
            x = work.Right - Math.Max(0, right) - newRadius;
        }

        if (top <= docked && top <= bottom)
        {
            y = work.Y + Math.Max(0, top) + newRadius;
        }
        else if (bottom <= docked)
        {
            y = work.Bottom - Math.Max(0, bottom) - newRadius;
        }

        return Clamp(new PointD(x, y), newRadius, work, scale);
    }

    internal static (double Left, double Right, double Top, double Bottom) EdgeDistances(OrbDisc disc, RectD work) => (
        disc.Center.X - disc.Radius - work.X,
        work.Right - (disc.Center.X + disc.Radius),
        disc.Center.Y - disc.Radius - work.Y,
        work.Bottom - (disc.Center.Y + disc.Radius));
}
