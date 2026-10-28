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
    /// Taskbar widget rectangle in taskbar client coordinates: immediately before the notification area along
    /// the taskbar, spanning its thickness. Returns null when there is not enough room.
    /// </summary>
    public static RectD? TaskbarWidget(RectD taskbar, RectD tray, double length, double gap, bool vertical)
    {
        if (taskbar.IsEmpty || length <= 0)
        {
            return null;
        }

        if (vertical)
        {
            var bottom = tray.IsEmpty ? taskbar.Bottom : tray.Y - gap;
            var top = bottom - length;
            if (top < taskbar.Y + taskbar.Height * 0.25)
            {
                return null;
            }

            return new RectD(taskbar.X, Math.Round(top), taskbar.Width, Math.Round(length));
        }

        var right = tray.IsEmpty ? taskbar.Right : tray.X - gap;
        var left = right - length;
        if (left < taskbar.X + taskbar.Width * 0.25)
        {
            return null;
        }

        var y = taskbar.Y;
        var h = taskbar.Height;
        if (!tray.IsEmpty && tray.Height > 0 && tray.Height < taskbar.Height)
        {
            y = tray.Y;
            h = tray.Height;
        }

        return new RectD(Math.Round(left), y, Math.Round(length), h);
    }

    private static double ClampAxis(double value, double min, double max) =>
        min > max ? (min + max) / 2 : Math.Clamp(value, min, max);
}
