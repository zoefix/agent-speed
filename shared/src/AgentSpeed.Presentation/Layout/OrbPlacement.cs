namespace AgentSpeed.Presentation.Layout;

/// <summary>Where the orbs of a split go and which existing orbs had to move (indices into <c>others</c>).</summary>
public sealed record OrbSplitLayout(IReadOnlyList<PointD> Children, IReadOnlyDictionary<int, PointD> Pushed);

/// <summary>
/// Placement of several orbs on one display (DESIGN 2b: creating, overlaps, split, move to corner, Reset Position).
/// Physical pixels with an explicit display scale; <c>others</c> are the discs already on that display.
/// </summary>
public static class OrbPlacement
{
    private const int MaxGridPoints = 250_000;

    /// <summary>The four touching slots around <paramref name="anchor"/>: left, below, right, above (centers aligned, 12 pt gap).</summary>
    public static IReadOnlyList<PointD> TouchingSlots(OrbDisc anchor, double radius, double scale)
    {
        var d = anchor.Radius + OrbGeometry.Gap * OrbGeometry.ValidScale(scale) + radius;
        var c = anchor.Center;
        return [new PointD(c.X - d, c.Y), new PointD(c.X, c.Y + d), new PointD(c.X + d, c.Y), new PointD(c.X, c.Y - d)];
    }

    /// <summary>
    /// A new orb next to the one it came from: the first touching slot inside the work area that keeps clear of every
    /// orb; else the nearest free point of a 24 pt grid around that orb; else cascaded +24 / +24 pt from it. Without
    /// an anchor the default corner is the start.
    /// </summary>
    public static PointD PlaceNew(double radius, OrbDisc? anchor, IReadOnlyList<OrbDisc> others, RectD work, double scale)
    {
        ArgumentNullException.ThrowIfNull(others);
        if (anchor is not { } from)
        {
            var start = OrbGeometry.DefaultCenter(work, radius, scale);
            return OrbGeometry.IsFree(start, radius, others, scale)
                ? start
                : GridSearch(start, radius, others, work, scale) ?? Cascade(start, radius, work, scale);
        }

        var occupied = others.Contains(from) ? others : [.. others, from];
        foreach (var slot in TouchingSlots(from, radius, scale))
        {
            if (OrbGeometry.Fits(slot, radius, work, scale) && OrbGeometry.IsFree(slot, radius, occupied, scale))
            {
                return slot;
            }
        }

        return GridSearch(from.Center, radius, occupied, work, scale) ?? Cascade(from.Center, radius, work, scale);
    }

    /// <summary>The free point of a 24 pt grid through <paramref name="origin"/> nearest to it, or null when the display is full.</summary>
    public static PointD? GridSearch(PointD origin, double radius, IReadOnlyList<OrbDisc> others, RectD work, double scale)
    {
        ArgumentNullException.ThrowIfNull(others);
        var s = OrbGeometry.ValidScale(scale);
        var inset = radius + OrbGeometry.Margin * s;
        var step = OrbGeometry.Grid * s;
        var lowX = Math.Ceiling((work.X + inset - origin.X) / step);
        var highX = Math.Floor((work.Right - inset - origin.X) / step);
        var lowY = Math.Ceiling((work.Y + inset - origin.Y) / step);
        var highY = Math.Floor((work.Bottom - inset - origin.Y) / step);
        if (!(lowX <= highX && lowY <= highY) || (highX - lowX + 1) * (highY - lowY + 1) > MaxGridPoints)
        {
            return null;
        }

        PointD? best = null;
        var bestDistance = double.PositiveInfinity;
        for (var j = lowY; j <= highY; j++)
        {
            for (var i = lowX; i <= highX; i++)
            {
                var point = new PointD(origin.X + i * step, origin.Y + j * step);
                var d = OrbGeometry.Distance(point, origin);
                if (d < bestDistance - 0.001 && OrbGeometry.IsFree(point, radius, others, s))
                {
                    best = point;
                    bestDistance = d;
                }
            }
        }

        return best;
    }

    /// <summary>+24 / +24 pt (right and down) from <paramref name="origin"/>, kept inside the work area.</summary>
    public static PointD Cascade(PointD origin, double radius, RectD work, double scale)
    {
        var step = OrbGeometry.Grid * OrbGeometry.ValidScale(scale);
        return OrbGeometry.Clamp(new PointD(origin.X + step, origin.Y + step), radius, work, scale);
    }

    /// <summary>
    /// Keeps <paramref name="center"/> (clamped inside the work area) when it is clear of every orb; else the nearest
    /// free touching position around the orbs it overlaps; else the nearest free grid point. Never off-screen.
    /// </summary>
    public static PointD ResolveOverlap(PointD center, double radius, IReadOnlyList<OrbDisc> others, RectD work, double scale)
    {
        ArgumentNullException.ThrowIfNull(others);
        var start = OrbGeometry.Clamp(center, radius, work, scale);
        if (OrbGeometry.IsFree(start, radius, others, scale))
        {
            return start;
        }

        var gap = OrbGeometry.Gap * OrbGeometry.ValidScale(scale);
        PointD? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var other in others)
        {
            if (OrbGeometry.IsClear(start, radius, other, scale))
            {
                continue;
            }

            var reach = other.Radius + gap + radius + 0.01;
            var dx = start.X - other.Center.X;
            var dy = start.Y - other.Center.Y;
            var baseAngle = Math.Abs(dx) + Math.Abs(dy) < 1e-9 ? 0 : Math.Atan2(dy, dx);
            for (var step = 0; step < 72; step++)
            {
                var angle = baseAngle + step * Math.PI / 36;
                var point = new PointD(other.Center.X + reach * Math.Cos(angle), other.Center.Y + reach * Math.Sin(angle));
                var d = OrbGeometry.Distance(point, start);
                if (d < bestDistance - 1e-9 && OrbGeometry.Fits(point, radius, work, scale)
                    && OrbGeometry.IsFree(point, radius, others, scale))
                {
                    best = point;
                    bestDistance = d;
                }
            }
        }

        return best ?? GridSearch(start, radius, others, work, scale) ?? start;
    }

    /// <summary>
    /// The orbs of a split: a row from the original toward the screen interior (a column when the original is docked to
    /// the left or right edge), 12 pt gaps, centers aligned, wrapping into the next row or column toward the interior.
    /// Orbs of <paramref name="others"/> in the way are pushed along the same axis.
    /// </summary>
    public static OrbSplitLayout SplitLayout(OrbDisc origin, IReadOnlyList<double> radii, RectD work, double scale, IReadOnlyList<OrbDisc> others)
    {
        ArgumentNullException.ThrowIfNull(radii);
        ArgumentNullException.ThrowIfNull(others);
        var s = OrbGeometry.ValidScale(scale);
        var gap = OrbGeometry.Gap * s;
        var margin = OrbGeometry.Margin * s;
        var edge = OrbGeometry.DockedEdge(origin, work, s);
        var vertical = edge is OrbEdge.Left or OrbEdge.Right;

        double Main(PointD p) => vertical ? p.Y : p.X;
        double Cross(PointD p) => vertical ? p.X : p.Y;
        PointD Make(double main, double cross) => vertical ? new PointD(cross, main) : new PointD(main, cross);

        var mainLow = (vertical ? work.Y : work.X) + margin;
        var mainHigh = (vertical ? work.Bottom : work.Right) - margin;
        var crossLow = (vertical ? work.X : work.Y) + margin;
        var crossHigh = (vertical ? work.Right : work.Bottom) - margin;
        var mainDirection = Main(origin.Center) > (mainLow + mainHigh) / 2 ? -1.0 : 1.0;
        var crossDirection = Cross(origin.Center) > (crossLow + crossHigh) / 2 ? -1.0 : 1.0;

        var children = new List<PointD>(radii.Count);
        var cross = Cross(origin.Center);
        var previousMain = Main(origin.Center);
        var previousRadius = origin.Radius;
        var lineRadius = origin.Radius;
        foreach (var radius in radii)
        {
            var position = previousMain + mainDirection * (previousRadius + gap + radius);
            if (position - radius < mainLow - OrbGeometry.Tolerance || position + radius > mainHigh + OrbGeometry.Tolerance)
            {
                cross += crossDirection * (lineRadius + gap + radius);
                position = Main(origin.Center);
                lineRadius = radius;
            }
            else
            {
                lineRadius = Math.Max(lineRadius, radius);
            }

            children.Add(OrbGeometry.Clamp(Make(position, cross), radius, work, s));
            previousMain = position;
            previousRadius = radius;
        }

        var layout = new List<OrbDisc>(children.Count + 1) { origin };
        for (var i = 0; i < children.Count; i++)
        {
            layout.Add(new OrbDisc(children[i], radii[i]));
        }

        // A push train: each orb, taken in the direction of the row, is pushed clear of the new orbs and of the orbs
        // before it (pushed or not); the orbs after it are pushed in their turn.
        var pushed = new Dictionary<int, PointD>();
        var settled = new List<OrbDisc>(layout);
        var order = Enumerable.Range(0, others.Count).OrderBy(i => mainDirection * Main(others[i].Center)).ToArray();
        for (var n = 0; n < order.Length; n++)
        {
            var index = order[n];
            var radius = others[index].Radius;
            var center = others[index].Center;
            if (!OrbGeometry.IsFree(center, radius, settled, s))
            {
                for (var attempt = 0; attempt < 64; attempt++)
                {
                    var blocker = settled.FindIndex(o => !OrbGeometry.IsClear(center, radius, o, s));
                    if (blocker < 0)
                    {
                        break;
                    }

                    var target = Main(settled[blocker].Center) + mainDirection * (settled[blocker].Radius + gap + radius);
                    center = Make(target, Cross(center));
                }

                if (!OrbGeometry.Fits(center, radius, work, s) || !OrbGeometry.IsFree(center, radius, settled, s))
                {
                    var remaining = order.Skip(n + 1).Select(k => others[k]);
                    center = ResolveOverlap(others[index].Center, radius, [.. settled, .. remaining], work, s);
                }

                pushed[index] = center;
            }

            settled.Add(new OrbDisc(center, radius));
        }

        return new OrbSplitLayout(children, pushed);
    }

    /// <summary>
    /// The corners of the work area at the 12 pt margin in the order of "move to next corner": top-right, bottom-right,
    /// bottom-left, top-left.
    /// </summary>
    public static IReadOnlyList<PointD> Corners(double radius, RectD work, double scale)
    {
        var inset = OrbGeometry.Margin * OrbGeometry.ValidScale(scale) + radius;
        PointD[] points =
        [
            new(work.Right - inset, work.Y + inset),
            new(work.Right - inset, work.Bottom - inset),
            new(work.X + inset, work.Bottom - inset),
            new(work.X + inset, work.Y + inset),
        ];
        return points.Select(p => OrbGeometry.Clamp(p, radius, work, scale)).ToArray();
    }

    /// <summary>
    /// <c>a11y.orb.moveCorner</c>: from the corner nearest the disc to the next one of the cycle, skipping corners
    /// another orb occupies; when every corner is taken, the nearest free touching position at the next corner.
    /// </summary>
    public static PointD NextCorner(OrbDisc current, IReadOnlyList<OrbDisc> others, RectD work, double scale)
    {
        ArgumentNullException.ThrowIfNull(others);
        var points = Corners(current.Radius, work, scale);
        var nearest = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (OrbGeometry.Distance(points[i], current.Center) < OrbGeometry.Distance(points[nearest], current.Center) - 1e-9)
            {
                nearest = i;
            }
        }

        for (var step = 1; step <= 3; step++)
        {
            var candidate = points[(nearest + step) % 4];
            if (OrbGeometry.IsFree(candidate, current.Radius, others, scale))
            {
                return candidate;
            }
        }

        return ResolveOverlap(points[(nearest + 1) % 4], current.Radius, others, work, scale);
    }

    /// <summary>Reset Position: the default corner, or the new-orb rule next to the orb that sits there.</summary>
    public static PointD ResetPosition(double radius, IReadOnlyList<OrbDisc> others, RectD work, double scale)
    {
        ArgumentNullException.ThrowIfNull(others);
        var start = OrbGeometry.DefaultCenter(work, radius, scale);
        OrbDisc? blocker = null;
        foreach (var other in others)
        {
            if (!OrbGeometry.IsClear(start, radius, other, scale)
                && (blocker is null || OrbGeometry.Distance(other.Center, start) < OrbGeometry.Distance(blocker.Value.Center, start)))
            {
                blocker = other;
            }
        }

        return blocker is null ? start : PlaceNew(radius, blocker, others, work, scale);
    }
}
