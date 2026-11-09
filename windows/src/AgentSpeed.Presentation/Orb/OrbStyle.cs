namespace AgentSpeed.Presentation.Orb;

/// <summary>The instrument a floating orb draws (DESIGN 2a).</summary>
public enum OrbStyle
{
    Led,
    Needle,
    Liquid,
    Reactor,
    Minimal,

    /// <summary>An animated character with floating cards (DESIGN 2a.6); stands in a disc of 2 S.</summary>
    Character,
}

/// <summary>Persisted keys, string keys and the size limits every style shares (DESIGN 2, 2b).</summary>
public static class OrbStyles
{
    public const double MinSize = 64;
    public const double MaxSize = 240;

    /// <summary>The step of the size sliders (Settings and the context menu).</summary>
    public const double SizeStep = 4;

    public static IReadOnlyList<OrbStyle> All { get; } = [OrbStyle.Led, OrbStyle.Needle, OrbStyle.Liquid, OrbStyle.Reactor, OrbStyle.Minimal, OrbStyle.Character];

    /// <summary>The persisted key (<c>led</c>, <c>needle</c>, …), also the suffix of the <c>style.*</c> strings.</summary>
    public static string Key(OrbStyle style) => style switch
    {
        OrbStyle.Needle => "needle",
        OrbStyle.Liquid => "liquid",
        OrbStyle.Reactor => "reactor",
        OrbStyle.Minimal => "minimal",
        OrbStyle.Character => "character",
        _ => "led",
    };

    /// <summary>Unknown or missing keys are <see cref="OrbStyle.Led"/>.</summary>
    public static OrbStyle Parse(string? key) => key switch
    {
        "needle" => OrbStyle.Needle,
        "liquid" => OrbStyle.Liquid,
        "reactor" => OrbStyle.Reactor,
        "minimal" => OrbStyle.Minimal,
        "character" => OrbStyle.Character,
        _ => OrbStyle.Led,
    };

    /// <summary>
    /// The disc the style occupies, × the orb size S: the character stands in a disc twice as wide (DESIGN 2a.6).
    /// Placement, snapping, magnets, name plates and the window follow this disc.
    /// </summary>
    public static double Footprint(OrbStyle style) => style == OrbStyle.Character ? 2 : 1;

    /// <summary>The footprint disc's diameter D for an orb of size S.</summary>
    public static double FootprintDiameter(OrbStyle style, double size) => size * Footprint(style);

    /// <summary>Clamps a size to the shared range, so changing the style never resizes an orb.</summary>
    public static double ClampSize(double size) => double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinSize, MaxSize) : 148;
}

/// <summary>
/// Where a click lands on an orb, in its footprint disc of diameter D (DESIGN 2a.6): the whole disc, or for
/// <see cref="OrbStyle.Character"/> an upright capsule around her body, 0.42 D wide and 0.9 D tall, its center 0.04 D
/// below the disc's.
/// </summary>
public static class OrbHitShape
{
    public const double CharacterWidth = 0.42;
    public const double CharacterHeight = 0.9;
    public const double CharacterDrop = 0.04;

    /// <summary>Whether a point relative to the disc center (y down, as on Windows) is inside, with <paramref name="tolerance"/> of slack.</summary>
    public static bool Contains(OrbStyle style, double dx, double dy, double diameter, double tolerance = 0)
    {
        if (style != OrbStyle.Character)
        {
            return Math.Sqrt(dx * dx + dy * dy) <= diameter / 2 + tolerance;
        }

        var radius = CharacterWidth * diameter / 2 + tolerance;
        var half = Math.Max(0, CharacterHeight * diameter / 2 + tolerance - radius);
        var y = dy - CharacterDrop * diameter;
        var nearest = Math.Min(Math.Max(y, -half), half);
        var ny = y - nearest;
        return Math.Sqrt(dx * dx + ny * ny) <= radius;
    }

    /// <summary>The shape's bounding box relative to the disc center (y down): left, top, width, height.</summary>
    public static (double X, double Y, double Width, double Height) Bounds(OrbStyle style, double diameter)
    {
        if (style != OrbStyle.Character)
        {
            return (-diameter / 2, -diameter / 2, diameter, diameter);
        }

        var w = CharacterWidth * diameter;
        var h = CharacterHeight * diameter;
        return (-w / 2, -h / 2 + CharacterDrop * diameter, w, h);
    }
}
