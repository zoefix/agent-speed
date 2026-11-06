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

/// <summary>Persisted keys, string keys and the size limits every style shares (DESIGN 2, 2b).</summary>
public static class OrbStyles
{
    public const double MinSize = 64;
    public const double MaxSize = 240;

    /// <summary>The step of the size sliders (Settings and the context menu).</summary>
    public const double SizeStep = 4;

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

    /// <summary>Clamps a size to the shared range, so changing the style never resizes an orb.</summary>
    public static double ClampSize(double size) => double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinSize, MaxSize) : 148;
}
