using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// Switching an orb's unit by clicking it (DESIGN 9 Orb, Switching 1): a press and release within 4 DIP of movement and
/// 0.5 s on the unit line toggles once the system double-click interval has passed without a second click, so a
/// double-click still opens the dashboard and never toggles; a drag that starts on the unit moves the orb. Pure state:
/// the orb window feeds it input and asks <see cref="IsDue"/> from a timer. Times are seconds.
/// </summary>
public sealed class OrbUnitToggle
{
    public const double MaxMovement = 4;
    public const double MaxPress = 0.5;

    /// <summary>The hit area is the unit text grown to at least this (DIP), clipped to the disc.</summary>
    public const double MinHitWidth = 44;

    public const double MinHitHeight = 20;

    /// <summary>The hover affordance's delay before <c>orbs.unit.toggleHint</c> shows (DESIGN 9).</summary>
    public const double HintDelay = 0.8;

    private (double Time, PointD Point)? _press;
    private double? _dueAt;

    /// <summary>A toggle is waiting for the double-click interval to pass.</summary>
    public bool IsPending => _dueAt is not null;

    public double? DueAt => _dueAt;

    /// <summary>A left press on the disc. <paramref name="clickCount"/> 2 (a double-click) cancels any pending toggle.</summary>
    public void Press(PointD point, double time, bool onUnit, int clickCount)
    {
        if (clickCount >= 2)
        {
            Cancel();
            return;
        }

        _press = onUnit ? (time, point) : null;
    }

    /// <summary>The release; <paramref name="moved"/> when the press turned into a drag. Returns true when a toggle is now pending.</summary>
    public bool Release(PointD point, double time, bool moved, double doubleClickInterval)
    {
        if (_press is not { } press)
        {
            return false;
        }

        _press = null;
        var dx = point.X - press.Point.X;
        var dy = point.Y - press.Point.Y;
        if (moved || dx * dx + dy * dy > MaxMovement * MaxMovement || time - press.Time > MaxPress)
        {
            return false;
        }

        _dueAt = press.Time + Math.Max(0, doubleClickInterval);
        return true;
    }

    /// <summary>True once (and clears the pending toggle) when the double-click interval has passed.</summary>
    public bool IsDue(double time)
    {
        if (_dueAt is not { } due || time < due)
        {
            return false;
        }

        _dueAt = null;
        return true;
    }

    public void Cancel()
    {
        _press = null;
        _dueAt = null;
    }

    /// <summary>
    /// The hit area (DIP, y down) of the unit line, or of the value for a style without one (<c>minimal</c>): the text
    /// bounds grown to at least 44 × 20, clipped to the disc's square. <paramref name="textWidth"/> is the measured width
    /// of <paramref name="layout"/>'s unit (or value) text at the orb's size.
    /// </summary>
    public static RectD HitRect(OrbReadoutLayout layout, double diameter, PointD center, double textWidth)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var s = diameter;
        var size = layout.UnitSize is { } unit ? OrbReadoutLayout.TextSize(unit, s) : layout.ValueSize * s;
        var baseline = (layout.UnitBaseline ?? layout.ValueBaseline) * s;
        var capHeight = size * 0.7;
        var width = Math.Max(MinHitWidth, textWidth);
        var height = Math.Max(MinHitHeight, capHeight + 2 * Math.Max(4, capHeight * 0.4));
        var midY = center.Y + baseline - capHeight / 2;
        var rect = new RectD(center.X - width / 2, midY - height / 2, width, height);
        return RectD.FromEdges(
            Math.Max(rect.X, center.X - s / 2),
            Math.Max(rect.Y, center.Y - s / 2),
            Math.Min(rect.Right, center.X + s / 2),
            Math.Min(rect.Bottom, center.Y + s / 2));
    }

    /// <summary>Whether a point (DIP) is in the hit area and on the disc.</summary>
    public static bool Contains(PointD point, RectD hit, PointD center, double diameter)
    {
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return hit.Contains(point) && dx * dx + dy * dy <= diameter * diameter / 4;
    }
}
