namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>
/// The name plate's geometry for a disc of size S (DESIGN 2b, Name plate, Geometry), in pt. The type never shrinks
/// below 9 pt: a plate that does not fit its budget shortens its text instead.
/// </summary>
public readonly record struct NamePlateMetrics
{
    public NamePlateMetrics(double size)
    {
        Size = double.IsFinite(size) && size > 0 ? size : 148;
        FontSize = Math.Clamp(Math.Round(0.066 * Size * 2, MidpointRounding.AwayFromZero) / 2, 9, 12);
        Height = Math.Round(1.78 * FontSize, MidpointRounding.AwayFromZero);
        Gap = Math.Clamp(Math.Round(0.034 * Size, MidpointRounding.AwayFromZero), 4, 7);
        Dot = Math.Max(5, 0.6 * FontSize);
    }

    /// <summary>The disc size S.</summary>
    public double Size { get; }

    /// <summary>Text size f: 0.066 S rounded to 0.5, 9–12.</summary>
    public double FontSize { get; }

    /// <summary>Plate height h: round(1.78 f).</summary>
    public double Height { get; }

    public double CornerRadius => Height / 2;

    /// <summary>Gap to the disc g: round(0.034 S), 4–7.</summary>
    public double Gap { get; }

    /// <summary>Dot diameter d: max(5, 0.6 f).</summary>
    public double Dot { get; }

    /// <summary>Dots overlap the previous one by a third.</summary>
    public double DotPitch => Dot * 2 / 3;

    /// <summary>The first dot is concentric with the left end cap.</summary>
    public double Leading => Height / 2 - Dot / 2;

    /// <summary>Space between the last dot and the text: 0.42 f.</summary>
    public double TextGap => 0.42 * FontSize;

    /// <summary>Trailing padding: 0.52 h (also the leading padding of a line without dots).</summary>
    public double Trailing => 0.52 * Height;

    /// <summary>Space between the items of the folded chip: 0.9 f.</summary>
    public double ItemGap => 0.9 * FontSize;

    /// <summary>The width budget of a plate alone: 1.5 S.</summary>
    public double AloneBudget => 1.5 * Size;

    /// <summary>Stroke of an unavailable source's hollow ring: max(1 pt, 0.18 d).</summary>
    public double HollowStroke => Math.Max(1, 0.18 * Dot);

    /// <summary>Letter spacing: +0.012 em, or +0.04 em when the name contains CJK.</summary>
    public double Tracking(bool cjk) => (cjk ? 0.04 : 0.012) * FontSize;

    /// <summary>Width of <paramref name="count"/> overlapping dots.</summary>
    public double DotsWidth(int count) => count <= 0 ? 0 : Dot + (count - 1) * DotPitch;

    /// <summary>The plate's width for its dots and a text of <paramref name="textWidth"/>.</summary>
    public double PlateWidth(int dots, double textWidth) => Leading + DotsWidth(dots) + TextGap + Math.Max(0, textWidth) + Trailing;

    /// <summary>The room left for the text within a plate budget.</summary>
    public double TextBudget(int dots, double plateBudget) => plateBudget - (Leading + DotsWidth(dots) + TextGap + Trailing);
}
