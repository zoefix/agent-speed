using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>Where a plate sits around its disc.</summary>
public enum NamePlateSlot
{
    Below,
    Above,
    Left,
    Right,
}

/// <summary>
/// One orb for the placement: its disc center and size in pt in the coordinate space of its display's usable area
/// (<paramref name="Area"/>, also pt), and whether it carries a plate. Orbs without a plate are still obstacles.
/// </summary>
public readonly record struct NamePlateSubject(PointD Center, double Size, RectD Area, bool HasPlate);

/// <summary>A placed plate: its slot, its rectangle (pt, the subject's space) and the width budget it was fitted to.</summary>
public readonly record struct NamePlatePlaced(NamePlateSlot Slot, RectD Rect, double Budget);

/// <summary>
/// Collision-free plate placement (DESIGN 2b, Name plate, Placement). Orbs whose discs are within 16 pt form a group;
/// a group takes the first slot that fits every member — below, above, the side facing the center of the usable area,
/// the other side — else each member takes its own first fit, else its plate hides. A plate fits when it stays 3 pt
/// inside the usable area and 4 pt from every other disc and placed plate. Orbs on different areas never interact.
/// </summary>
public static class NamePlatePlacement
{
    public const double GroupDistance = 16;
    public const double AreaInset = 3;
    public const double Clearance = 4;

    /// <summary>A plate above or below stops this far before the point dividing the distance to a neighbor.</summary>
    public const double NeighborMargin = 3;

    /// <summary>
    /// Resolves every plate. <paramref name="widthFor"/> returns the width of subject <c>i</c>'s plate fitted to a
    /// budget (the name shortens; the width may exceed a budget too small for one grapheme). Null: no plate.
    /// </summary>
    public static NamePlatePlaced?[] Resolve(IReadOnlyList<NamePlateSubject> subjects, Func<int, double, double> widthFor)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(widthFor);
        var results = new NamePlatePlaced?[subjects.Count];
        foreach (var area in subjects.Select(s => s.Area).Distinct())
        {
            var members = Enumerable.Range(0, subjects.Count).Where(i => subjects[i].Area == area).ToArray();
            ResolveArea(subjects, members, area, widthFor, results);
        }

        return results;
    }

    /// <summary>
    /// The budget of a plate above or below: 1.5 S, and never past the point that divides the center distance to a
    /// neighbor overlapping its horizontal band (<c>|Δy| &lt; (S + Sn) / 2</c>) in proportion to the two sizes, less
    /// 3 pt. Equal orbs at the 12 pt gap get S + 6 each.
    /// </summary>
    public static double RowBudget(IReadOnlyList<NamePlateSubject> subjects, int index)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        var self = subjects[index];
        var half = new NamePlateMetrics(self.Size).AloneBudget / 2;
        for (var j = 0; j < subjects.Count; j++)
        {
            var other = subjects[j];
            if (j == index || other.Area != self.Area || Math.Abs(other.Center.Y - self.Center.Y) >= (self.Size + other.Size) / 2)
            {
                continue;
            }

            var share = Math.Abs(other.Center.X - self.Center.X) * self.Size / (self.Size + other.Size);
            half = Math.Min(half, share - NeighborMargin);
        }

        return Math.Max(0, 2 * half);
    }

    /// <summary>
    /// The budget of a side plate: 1.5 S, limited by the usable area (3 pt inset) on that side.
    /// </summary>
    public static double SideBudget(NamePlateSubject subject, NamePlateSlot slot)
    {
        var metrics = new NamePlateMetrics(subject.Size);
        var edge = subject.Size / 2 + metrics.Gap;
        var room = slot == NamePlateSlot.Left
            ? subject.Center.X - edge - (subject.Area.X + AreaInset)
            : subject.Area.Right - AreaInset - (subject.Center.X + edge);
        return Math.Max(0, Math.Min(metrics.AloneBudget, room));
    }

    /// <summary>The rectangle of a plate of <paramref name="width"/> in a slot: g from the disc edge, centered on the disc.</summary>
    public static RectD Rect(PointD center, double size, double width, NamePlateSlot slot)
    {
        var metrics = new NamePlateMetrics(size);
        var h = metrics.Height;
        var edge = size / 2 + metrics.Gap;
        return slot switch
        {
            NamePlateSlot.Below => new RectD(center.X - width / 2, center.Y + edge, width, h),
            NamePlateSlot.Above => new RectD(center.X - width / 2, center.Y - edge - h, width, h),
            NamePlateSlot.Left => new RectD(center.X - edge - width, center.Y - h / 2, width, h),
            _ => new RectD(center.X + edge, center.Y - h / 2, width, h),
        };
    }

    /// <summary>The orbs connected by gaps of at most 16 pt, in order of their first member.</summary>
    public static List<List<int>> Groups(IReadOnlyList<NamePlateSubject> subjects, IReadOnlyList<int> members)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(members);
        var groups = new List<List<int>>();
        var seen = new HashSet<int>();
        foreach (var start in members)
        {
            if (!seen.Add(start))
            {
                continue;
            }

            var group = new List<int> { start };
            for (var k = 0; k < group.Count; k++)
            {
                var a = subjects[group[k]];
                foreach (var j in members)
                {
                    var b = subjects[j];
                    if (!seen.Contains(j) && OrbGeometry.Distance(a.Center, b.Center) - (a.Size + b.Size) / 2 <= GroupDistance)
                    {
                        seen.Add(j);
                        group.Add(j);
                    }
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static void ResolveArea(IReadOnlyList<NamePlateSubject> subjects, int[] members, RectD area, Func<int, double, double> widthFor,
        NamePlatePlaced?[] results)
    {
        var placed = new List<RectD>();
        foreach (var group in Groups(subjects, members))
        {
            var carriers = group.Where(i => subjects[i].HasPlate).ToArray();
            if (carriers.Length == 0)
            {
                continue;
            }

            var meanX = group.Average(i => subjects[i].Center.X);
            var side = meanX > area.X + area.Width / 2 ? NamePlateSlot.Left : NamePlateSlot.Right;
            var other = side == NamePlateSlot.Left ? NamePlateSlot.Right : NamePlateSlot.Left;
            NamePlateSlot[] order = [NamePlateSlot.Below, NamePlateSlot.Above, side, other];

            if (TryGroup(subjects, members, carriers, order, area, widthFor, placed, results))
            {
                continue;
            }

            foreach (var i in carriers)
            {
                foreach (var slot in order)
                {
                    var candidate = Candidate(subjects, i, slot, widthFor);
                    if (Fits(candidate.Rect, subjects, members, i, area, placed))
                    {
                        results[i] = candidate;
                        placed.Add(candidate.Rect);
                        break;
                    }
                }
            }
        }
    }

    private static bool TryGroup(IReadOnlyList<NamePlateSubject> subjects, int[] members, int[] carriers, NamePlateSlot[] order, RectD area,
        Func<int, double, double> widthFor, List<RectD> placed, NamePlatePlaced?[] results)
    {
        foreach (var slot in order)
        {
            var trial = new List<RectD>(placed);
            var candidates = new NamePlatePlaced[carriers.Length];
            var ok = true;
            for (var k = 0; k < carriers.Length && ok; k++)
            {
                candidates[k] = Candidate(subjects, carriers[k], slot, widthFor);
                ok = Fits(candidates[k].Rect, subjects, members, carriers[k], area, trial);
                trial.Add(candidates[k].Rect);
            }

            if (ok)
            {
                for (var k = 0; k < carriers.Length; k++)
                {
                    results[carriers[k]] = candidates[k];
                    placed.Add(candidates[k].Rect);
                }

                return true;
            }
        }

        return false;
    }

    private static NamePlatePlaced Candidate(IReadOnlyList<NamePlateSubject> subjects, int index, NamePlateSlot slot,
        Func<int, double, double> widthFor)
    {
        var subject = subjects[index];
        var budget = slot is NamePlateSlot.Below or NamePlateSlot.Above
            ? RowBudget(subjects, index)
            : SideBudget(subject, slot);
        var width = widthFor(index, budget);
        return new NamePlatePlaced(slot, Rect(subject.Center, subject.Size, width, slot), budget);
    }

    private static bool Fits(RectD rect, IReadOnlyList<NamePlateSubject> subjects, int[] members, int self, RectD area, List<RectD> placed)
    {
        const double Tolerance = 0.001;
        if (rect.X < area.X + AreaInset - Tolerance || rect.Y < area.Y + AreaInset - Tolerance
            || rect.Right > area.Right - AreaInset + Tolerance || rect.Bottom > area.Bottom - AreaInset + Tolerance)
        {
            return false;
        }

        foreach (var j in members)
        {
            if (j != self && rect.DistanceSquaredTo(subjects[j].Center) < Square(subjects[j].Size / 2 + Clearance))
            {
                return false;
            }
        }

        foreach (var other in placed)
        {
            if (rect.X < other.Right + Clearance && other.X < rect.Right + Clearance
                && rect.Y < other.Bottom + Clearance && other.Y < rect.Bottom + Clearance)
            {
                return false;
            }
        }

        return true;
    }

    private static double Square(double value) => value * value;
}
