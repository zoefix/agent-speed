namespace AgentSpeed.Presentation.Orb;

/// <summary>The instrument a floating orb draws (DESIGN 2a).</summary>
public enum OrbStyle
{
    Led,
    Needle,
    Liquid,
    Reactor,
    Minimal,
}

/// <summary>Persisted keys, string keys and size limits of the orb styles.</summary>
public static class OrbStyles
{
    public const double MinSize = 96;
    public const double MinimalMinSize = 64;
    public const double MaxSize = 240;

    public static IReadOnlyList<OrbStyle> All { get; } = [OrbStyle.Led, OrbStyle.Needle, OrbStyle.Liquid, OrbStyle.Reactor, OrbStyle.Minimal];

    /// <summary>The persisted key (<c>led</c>, <c>needle</c>, …), also the suffix of the <c>style.*</c> strings.</summary>
    public static string Key(OrbStyle style) => style switch
    {
        OrbStyle.Needle => "needle",
        OrbStyle.Liquid => "liquid",
        OrbStyle.Reactor => "reactor",
        OrbStyle.Minimal => "minimal",
        _ => "led",
    };

    /// <summary>Unknown or missing keys are <see cref="OrbStyle.Led"/>.</summary>
    public static OrbStyle Parse(string? key) => key switch
    {
        "needle" => OrbStyle.Needle,
        "liquid" => OrbStyle.Liquid,
        "reactor" => OrbStyle.Reactor,
        "minimal" => OrbStyle.Minimal,
        _ => OrbStyle.Led,
    };

    public static double MinSizeFor(OrbStyle style) => style == OrbStyle.Minimal ? MinimalMinSize : MinSize;

    /// <summary>Clamps a size to the style's range (a 72 pt <c>minimal</c> becomes 96 pt in any other style).</summary>
    public static double ClampSize(OrbStyle style, double size) =>
        double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinSizeFor(style), MaxSize) : 148;
}
