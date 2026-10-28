namespace AgentSpeed.Presentation.Layout;

/// <summary>
/// Where the dragged disc wants to be (the app follows it through a critically damped spring, settle 0.12 s), the
/// alignment guides to draw, and the index into <c>others</c> of the orb it is pulled against.
/// </summary>
public sealed record OrbDragResult(PointD Target, IReadOnlyList<OrbGuide> Guides, int? AttachedTo);

/// <summary>
/// Orb-to-orb magnetism and alignment while dragging and on release (DESIGN 2b). Priority: screen edge, then
/// orb-to-orb touching, then alignment; alignment never pulls a disc away from an edge it is docked to.
/// </summary>
public static class OrbMagnet
{
    /// <summary>The touching position against <paramref name="other"/> along the line between the centers (12 pt gap).</summary>
    public static PointD Touching(PointD point, double radius, OrbDisc other, double scale)
    {
        var dx = point.X - other.Center.X;
        var dy = point.Y - other.Center.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6)
        {
            dx = 1;
            dy = 0;
            length = 1;
        }

        var reach = other.Radius + OrbGeometry.Gap * OrbGeometry.ValidScale(scale) + radius;
        return new PointD(other.Center.X + dx / length * reach, other.Center.Y + dy / length * reach);
    }

    /// <summary>
    /// The drag target for the pointer position <paramref name="raw"/>. Axes within the snap threshold of a screen edge
    /// stay where the pointer puts them (they snap on release); otherwise the disc is pulled onto a touching position
    /// within 16 pt (aligned with that orb when its center is within 8 pt on an axis), else aligned with nearby orbs.
    /// </summary>
    public static OrbDragResult Drag(PointD raw, double radius, IReadOnlyList<OrbDisc> others, RectD work, double scale)
    {
        ArgumentNullException.ThrowIfNull(others);
        var (lockX, lockY) = OrbGeometry.NearEdges(raw, radius, work, scale);
        return Assist(raw, radius, others, scale, lockX, lockY);
    }

    /// <summary>
    /// Where the disc settles when released at <paramref name="raw"/>: today's edge snap first (per axis), then
    /// orb-to-orb touching on the free axes, then alignment; any remaining overlap slides to the nearest free touching
    /// position. Fully inside the work area and clear of every other orb.
    /// </summary>
    public static PointD Release(PointD raw, double radius, IReadOnlyList<OrbDisc> others, RectD work, double scale)
    {
        ArgumentNullException.ThrowIfNull(others);
        var snapped = OrbGeometry.SnapToEdges(raw, radius, work, scale);
        var near = OrbGeometry.NearEdges(raw, radius, work, scale);
        var lockX = near.X || Math.Abs(snapped.X - raw.X) > 1e-6;
        var lockY = near.Y || Math.Abs(snapped.Y - raw.Y) > 1e-6;
        var target = Assist(snapped, radius, others, scale, lockX, lockY).Target;
        return OrbPlacement.ResolveOverlap(target, radius, others, work, scale);
    }

    private static OrbDragResult Assist(PointD point, double radius, IReadOnlyList<OrbDisc> others, double scale, bool lockX, bool lockY)
    {
        var s = OrbGeometry.ValidScale(scale);
        var align = OrbGeometry.AlignDistance * s;
        if (!lockX && !lockY && NearestTouching(point, radius, others, s) is { } touch)
        {
            var other = others[touch.Index];
            var reach = other.Radius + OrbGeometry.Gap * s + radius;
            if (Math.Abs(point.Y - other.Center.Y) <= align)
            {
                var x = other.Center.X + (point.X >= other.Center.X ? reach : -reach);
                var target = new PointD(x, other.Center.Y);
                return new OrbDragResult(target, [HorizontalGuide(target, radius, other)], touch.Index);
            }

            if (Math.Abs(point.X - other.Center.X) <= align)
            {
                var y = other.Center.Y + (point.Y >= other.Center.Y ? reach : -reach);
                var target = new PointD(other.Center.X, y);
                return new OrbDragResult(target, [VerticalGuide(target, radius, other)], touch.Index);
            }

            return new OrbDragResult(touch.Point, [], touch.Index);
        }

        if (lockX != lockY && ConstrainedTouching(point, radius, others, s, fixedX: lockX) is { } constrained)
        {
            var target = lockX ? point with { Y = constrained.Value } : point with { X = constrained.Value };
            return new OrbDragResult(target, [], constrained.Index);
        }

        return Aligned(point, radius, others, s, lockX, lockY);
    }

    private static (int Index, PointD Point)? NearestTouching(PointD point, double radius, IReadOnlyList<OrbDisc> others, double scale)
    {
        (int, PointD)? best = null;
        var bestDistance = OrbGeometry.PullDistance * scale + 1e-6;
        for (var i = 0; i < others.Count; i++)
        {
            var candidate = Touching(point, radius, others[i], scale);
            var d = OrbGeometry.Distance(candidate, point);
            if (d < bestDistance)
            {
                best = (i, candidate);
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>Touching another orb while one axis stays where an edge put it: the free coordinate at the 12 pt gap within the pull distance.</summary>
    private static (int Index, double Value)? ConstrainedTouching(PointD point, double radius, IReadOnlyList<OrbDisc> others, double scale, bool fixedX)
    {
        (int, double)? best = null;
        var bestDistance = OrbGeometry.PullDistance * scale + 1e-6;
        var fixedValue = fixedX ? point.X : point.Y;
        var free = fixedX ? point.Y : point.X;
        for (var i = 0; i < others.Count; i++)
        {
            var other = others[i];
            var reach = other.Radius + OrbGeometry.Gap * scale + radius;
            var offset = fixedValue - (fixedX ? other.Center.X : other.Center.Y);
            if (Math.Abs(offset) > reach)
            {
                continue;
            }

            var span = Math.Sqrt(reach * reach - offset * offset);
            var center = fixedX ? other.Center.Y : other.Center.X;
            foreach (var candidate in (ReadOnlySpan<double>)[center - span, center + span])
            {
                var d = Math.Abs(candidate - free);
                if (d < bestDistance)
                {
                    best = (i, candidate);
                    bestDistance = d;
                }
            }
        }

        return best;
    }

    private static OrbDragResult Aligned(PointD point, double radius, IReadOnlyList<OrbDisc> others, double scale, bool lockX, bool lockY)
    {
        var align = OrbGeometry.AlignDistance * scale;
        var reach = OrbGeometry.AlignReach * scale;
        bool Near(OrbDisc other) => OrbGeometry.Distance(point, other.Center) - radius - other.Radius <= reach;

        var vertical = lockX ? null : Closest(o => Math.Abs(o.Center.X - point.X));
        var horizontal = lockY ? null : Closest(o => Math.Abs(o.Center.Y - point.Y));
        if (vertical is { } overlappedV && !OrbGeometry.IsClear(Target(), radius, overlappedV, scale))
        {
            vertical = null;
        }

        if (horizontal is { } overlappedH && !OrbGeometry.IsClear(Target(), radius, overlappedH, scale))
        {
            horizontal = null;
        }

        var target = Target();
        var guides = new List<OrbGuide>(2);
        if (vertical is { } v)
        {
            guides.Add(VerticalGuide(target, radius, v));
        }

        if (horizontal is { } h)
        {
            guides.Add(HorizontalGuide(target, radius, h));
        }

        return new OrbDragResult(target, guides, null);

        PointD Target() => new(vertical?.Center.X ?? point.X, horizontal?.Center.Y ?? point.Y);

        OrbDisc? Closest(Func<OrbDisc, double> offset)
        {
            OrbDisc? best = null;
            var bestOffset = align + 1e-6;
            foreach (var other in others)
            {
                var d = offset(other);
                if (d < bestOffset && Near(other))
                {
                    best = other;
                    bestOffset = d;
                }
            }

            return best;
        }
    }

    private static OrbGuide VerticalGuide(PointD target, double radius, OrbDisc other) => new(
        new PointD(other.Center.X, Math.Min(target.Y - radius, other.Center.Y - other.Radius)),
        new PointD(other.Center.X, Math.Max(target.Y + radius, other.Center.Y + other.Radius)));

    private static OrbGuide HorizontalGuide(PointD target, double radius, OrbDisc other) => new(
        new PointD(Math.Min(target.X - radius, other.Center.X - other.Radius), other.Center.Y),
        new PointD(Math.Max(target.X + radius, other.Center.X + other.Radius), other.Center.Y));
}
