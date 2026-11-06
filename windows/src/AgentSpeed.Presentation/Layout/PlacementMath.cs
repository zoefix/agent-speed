namespace AgentSpeed.Presentation.Layout;

/// <summary>Window placement rules for the orb, the dashboard flyout and the taskbar widget (physical pixels).</summary>
public static class PlacementMath
{
    /// <summary>Keeps the disc fully inside the work area with a margin.</summary>
    public static PointD ClampDisc(PointD center, double radius, RectD work, double margin = 0)
    {
        var minX = work.X + radius + margin;
        var maxX = work.Right - radius - margin;
        var minY = work.Y + radius + margin;
        var maxY = work.Bottom - radius - margin;
        return new PointD(ClampAxis(center.X, minX, maxX), ClampAxis(center.Y, minY, maxY));
    }

    /// <summary>
    /// Magnetic edge snapping: when the disc edge is within <paramref name="threshold"/> of a work area edge,
    /// it moves to exactly <paramref name="margin"/> from that edge.
    /// </summary>
    public static PointD Snap(PointD center, double radius, RectD work, double threshold, double margin)
    {
        var x = center.X;
        var y = center.Y;

        var left = x - radius - work.X;
        var right = work.Right - (x + radius);
        if (left < threshold && left <= right)
        {
            x = work.X + margin + radius;
        }
        else if (right < threshold)
        {
            x = work.Right - margin - radius;
        }

        var top = y - radius - work.Y;
        var bottom = work.Bottom - (y + radius);
        if (top < threshold && top <= bottom)
        {
            y = work.Y + margin + radius;
        }
        else if (bottom < threshold)
        {
            y = work.Bottom - margin - radius;
        }

        return ClampDisc(new PointD(x, y), radius, work);
    }

    public static (double X, double Y) ToFraction(PointD center, RectD work) => (
        work.Width > 0 ? Math.Clamp((center.X - work.X) / work.Width, 0, 1) : 0.5,
        work.Height > 0 ? Math.Clamp((center.Y - work.Y) / work.Height, 0, 1) : 0.5);

    public static PointD FromFraction(double fx, double fy, RectD work) =>
        new(work.X + Math.Clamp(fx, 0, 1) * work.Width, work.Y + Math.Clamp(fy, 0, 1) * work.Height);

    /// <summary>Default orb position: bottom-right corner of the work area.</summary>
    public static PointD DefaultOrbCenter(RectD work, double radius, double margin) =>
        ClampDisc(new PointD(work.Right - margin - radius, work.Bottom - margin * 2 - radius), radius, work, margin);

    /// <summary>Index of the rectangle containing the point, else the nearest one; -1 for an empty list.</summary>
    public static int NearestIndex(PointD point, IReadOnlyList<RectD> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < rects.Count; i++)
        {
            if (rects[i].Contains(point))
            {
                return i;
            }

            var distance = rects[i].DistanceSquaredTo(point);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Flyout rectangle in the work-area corner next to the taskbar's notification area. The edges against the
    /// corner are snapped first and the size is subtracted from them, so a flyout whose height animates keeps its
    /// anchored edge on the same pixel every frame.
    /// </summary>
    public static RectD Flyout(RectD work, TaskbarEdge edge, double width, double height, double margin)
    {
        width = Math.Round(Math.Min(width, Math.Max(0, work.Width - 2 * margin)));
        height = Math.Round(Math.Min(height, Math.Max(0, work.Height - 2 * margin)));
        var x = edge == TaskbarEdge.Left ? Math.Round(work.X + margin) : Math.Round(work.Right - margin) - width;
        var y = edge == TaskbarEdge.Top ? Math.Round(work.Y + margin) : Math.Round(work.Bottom - margin) - height;
        return new RectD(x, y, width, height);
    }

    /// <summary>
    /// Taskbar widget rectangle in screen pixels (DESIGN 3, Position on the taskbar): its trailing edge
    /// <paramref name="gap"/> plus <paramref name="offset"/> before the notification area (left of it, or above it when
    /// vertical), spanning the taskbar's thickness. The offset is limited and clears <paramref name="occupied"/> as
    /// <see cref="TaskbarWidgetOffset"/> says; null when the widget has no room.
    /// </summary>
    public static RectD? TaskbarWidget(RectD taskbar, RectD tray, double length, double gap, bool vertical, double offset = 0,
        IReadOnlyList<RectD>? occupied = null)
    {
        if (TaskbarWidgetOffset(taskbar, tray, length, gap, vertical, offset, occupied) is not { } shift)
        {
            return null;
        }

        var end = TaskbarWidgetEnd(taskbar, tray, gap, vertical) - shift;
        if (vertical)
        {
            return new RectD(taskbar.X, Math.Round(end - length), taskbar.Width, Math.Round(length));
        }

        var left = end - length;
        var y = taskbar.Y;
        var h = taskbar.Height;
        if (!tray.IsEmpty && tray.Height > 0 && tray.Height < taskbar.Height)
        {
            y = tray.Y;
            h = tray.Height;
        }

        return new RectD(Math.Round(left), y, Math.Round(length), h);
    }

    /// <summary>
    /// The offset (whole pixels) the taskbar widget takes for a requested one: from 0 (the default spot) up to where its
    /// far edge reaches the first <see cref="TaskbarPosition.ReservedStart"/> of the taskbar, which stays clear for a
    /// left-aligned Start button and the first app buttons. A spot that comes within <paramref name="gap"/> of a window of
    /// another program on the taskbar (<paramref name="occupied"/>) moves past it toward the taskbar's start, or, without
    /// room there, toward the notification area. Null when no spot fits.
    /// </summary>
    public static double? TaskbarWidgetOffset(RectD taskbar, RectD tray, double length, double gap, bool vertical, double offset,
        IReadOnlyList<RectD>? occupied = null)
    {
        if (taskbar.IsEmpty || !(length > 0) || !double.IsFinite(length))
        {
            return null;
        }

        var limit = vertical
            ? taskbar.Y + taskbar.Height * TaskbarPosition.ReservedStart
            : taskbar.X + taskbar.Width * TaskbarPosition.ReservedStart;
        var room = TaskbarWidgetEnd(taskbar, tray, gap, vertical) - length - limit;
        if (!(room >= 0))
        {
            return null;
        }

        var requested = double.IsFinite(offset) ? Math.Round(offset, MidpointRounding.AwayFromZero) : 0;
        var shift = Math.Clamp(requested, 0, Math.Floor(room));
        var spans = OccupiedSpans(taskbar, vertical, occupied);
        return spans.Count == 0 ? shift : ClearShift(spans, TaskbarWidgetEnd(taskbar, tray, gap, vertical), length, gap, shift, Math.Floor(room));
    }

    /// <summary>
    /// The stretches of the taskbar's length that other programs' windows cover. A window that does not cross the taskbar,
    /// or covers more than half of its length (an overlay rather than a neighbour), is left out.
    /// </summary>
    private static List<(double Start, double End)> OccupiedSpans(RectD taskbar, bool vertical, IReadOnlyList<RectD>? occupied)
    {
        var spans = new List<(double Start, double End)>();
        if (occupied is null)
        {
            return spans;
        }

        var barStart = vertical ? taskbar.Y : taskbar.X;
        var barEnd = vertical ? taskbar.Bottom : taskbar.Right;
        foreach (var rect in occupied)
        {
            var crosses = vertical
                ? rect.X < taskbar.Right && rect.Right > taskbar.X
                : rect.Y < taskbar.Bottom && rect.Bottom > taskbar.Y;
            var start = Math.Max(barStart, vertical ? rect.Y : rect.X);
            var end = Math.Min(barEnd, vertical ? rect.Bottom : rect.Right);
            if (crosses && end > start && end - start <= (barEnd - barStart) / 2)
            {
                spans.Add((start, end));
            }
        }

        return spans;
    }

    /// <summary>
    /// The nearest shift from <paramref name="shift"/> whose widget keeps <paramref name="gap"/> to every span: first toward
    /// the taskbar's start (up to <paramref name="max"/>), then toward the notification area (down to 0); null without one.
    /// </summary>
    private static double? ClearShift(List<(double Start, double End)> spans, double end, double length, double gap, double shift, double max)
    {
        for (var s = shift; s <= max;)
        {
            if (Overlap(spans, end - s - length, end - s, gap) is not { } hit)
            {
                return s;
            }

            s = Math.Ceiling(end - hit.Start + gap);
        }

        for (var s = shift; s >= 0;)
        {
            if (Overlap(spans, end - s - length, end - s, gap) is not { } hit)
            {
                return s;
            }

            s = Math.Floor(end - hit.End - gap - length);
        }

        return null;
    }

    /// <summary>The extent (lowest start, highest end) of the spans that come within <paramref name="gap"/> of [far, near].</summary>
    private static (double Start, double End)? Overlap(List<(double Start, double End)> spans, double far, double near, double gap)
    {
        (double Start, double End)? hit = null;
        foreach (var (start, end) in spans)
        {
            if (start < near + gap && end > far - gap)
            {
                hit = hit is { } h ? (Math.Min(h.Start, start), Math.Max(h.End, end)) : (start, end);
            }
        }

        return hit;
    }

    /// <summary>The widget's trailing edge at the default spot: the gap before the notification area, else the taskbar's end.</summary>
    private static double TaskbarWidgetEnd(RectD taskbar, RectD tray, double gap, bool vertical) => vertical
        ? tray.IsEmpty ? taskbar.Bottom : tray.Y - gap
        : tray.IsEmpty ? taskbar.Right : tray.X - gap;

    private static double ClampAxis(double value, double min, double max) =>
        min > max ? (min + max) / 2 : Math.Clamp(value, min, max);
}
