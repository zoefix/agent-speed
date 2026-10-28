using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Hero;

public enum PartRole
{
    Body,
    Detail,
    Window,
    Wheel,
    Rim,
    Spoke,
    Crank,
    Rider,
    Light,
    Glow,
    Shock,
    Flame,
    FlameCore,
}

public enum RoleFill
{
    None,
    Heat,
    Hot,
}

public enum SceneryKind
{
    Road,
    Rail,
    Guideway,
    Sky,
    Stratosphere,
    Space,
}

public enum EmitterKind
{
    Dust,
    Arc,
    Exhaust,
    Trail,
    Sparks,
    Contrail,
    Afterburner,
    Plume,
}

/// <summary>Stroke style of a part role (table <c>roles</c>).</summary>
public sealed record RoleStyle(double Weight, double Opacity, double Glow, bool Sprung, bool Hot, RoleFill Fill, double FillOpacity);

public readonly record struct CircleD(double X, double Y, double Radius)
{
    public PointD Center => new(X, Y);
}

/// <param name="Radius">Rolling radius: the spin angle is the ground distance divided by it.</param>
public sealed record SpinSpec(PointD Pivot, double Radius, int Symmetry);

public sealed record NozzleSpec(PointD At, PointD Direction);

/// <summary>One stroked (or, for flames, filled) part of a vehicle drawing; exactly one of path and circle is set.</summary>
public sealed class VehiclePart
{
    public required string Id { get; init; }

    public required PartRole Role { get; init; }

    public PathData? Path { get; init; }

    public CircleD? Circle { get; init; }

    public SpinSpec? Spin { get; init; }

    public NozzleSpec? Nozzle { get; init; }

    /// <summary>Moves with the body (suspension, pitch, float); resolved from the role unless the part overrides it.</summary>
    public required bool Sprung { get; init; }

    public bool Bleed { get; init; }
}

public sealed record EmitterSpec(string Id, EmitterKind Kind, PointD At, PointD Direction, int Count, IReadOnlyList<double> Lanes,
    double Length, bool Weak);

/// <summary>World speed range and idle amplitudes of a vehicle (units, units/s).</summary>
public sealed record VehicleMotion(double SpeedLow, double SpeedHigh, double Rest, double Bob, double Float, double Hover);

public sealed record WalkRigSpec(
    PointD Hip,
    PointD Torso,
    CircleD Head,
    double Thigh,
    double Shin,
    double Foot,
    double UpperArm,
    double Forearm,
    double Cycle,
    double Stance,
    double Lift,
    double Bob,
    double ArmSwing,
    double Elbow);

public sealed record PedalRigSpec(PointD Hip, PointD CrankPivot, double CrankLength, double CrankRadius, double Thigh, double Shin,
    double Pedal, double Bob);

public sealed class Vehicle
{
    public required string Key { get; init; }

    /// <summary>Position on the title ladder (0 = walk … 12 = rocket).</summary>
    public required int Index { get; init; }

    public required SceneryKind Scenery { get; init; }

    /// <summary>Road line, rail or guideway top; null for the sky, stratosphere and space.</summary>
    public double? GroundY { get; init; }

    /// <summary>Travel direction in degrees on screen (0 = right); null travels right.</summary>
    public double? Heading { get; init; }

    public required VehicleMotion Motion { get; init; }

    public WalkRigSpec? Walk { get; init; }

    public PedalRigSpec? Pedal { get; init; }

    public required IReadOnlyList<VehiclePart> Parts { get; init; }

    public required IReadOnlyList<EmitterSpec> Emitters { get; init; }

    /// <summary>Bounds of the solid drawing (flames, shock cones and bleeding tails excluded), as in the prototype.</summary>
    public required RectD Bounds { get; init; }

    public SpeedTitle Title => SpeedTitles.Ladder[Index].Title;

    public bool HasWheels { get; init; }

    /// <summary>Pitch pivot: the rear tire's ground contact, or the drawing's center.</summary>
    public required PointD PitchPivot { get; init; }

    public EmitterSpec? Emitter(EmitterKind kind)
    {
        foreach (var emitter in Emitters)
        {
            if (emitter.Kind == kind)
            {
                return emitter;
            }
        }

        return null;
    }

    public VehiclePart? Part(string id)
    {
        foreach (var part in Parts)
        {
            if (part.Id == id)
            {
                return part;
            }
        }

        return null;
    }
}

public sealed record SkyLayer(double Y, double Scale, double Parallax, double Opacity);

public sealed record SceneryCatalog(
    double RoadDash,
    double RoadGap,
    double RoadParallax,
    double SleeperSpacing,
    double SleeperDepth,
    double RailParallax,
    double GuidewayDepth,
    double PylonSpacing,
    double PylonDepth,
    double GuidewayParallax,
    IReadOnlyList<PathData> Clouds,
    IReadOnlyList<SkyLayer> SkyLayers,
    double HorizonCenterY,
    double HorizonRadius,
    int Stars,
    double SpaceParallax);

/// <summary>
/// The hero vehicle art (DESIGN 4c, <c>spec/assets/vehicles.json</c>): a 100 × 100 canvas, y down, every vehicle
/// facing right. Parsed and validated once.
/// </summary>
public sealed class VehicleArt
{
    public const string ResourceName = "AgentSpeed.Vehicles.json";

    private static readonly Lazy<VehicleArt?> SharedArt = new(LoadShared);

    public required double OrbFraction { get; init; }

    public required CircleD SafeCircle { get; init; }

    public required PointD MaskCenter { get; init; }

    /// <summary>Opaque up to this radius.</summary>
    public required double MaskSolid { get; init; }

    /// <summary>Fully transparent from this radius.</summary>
    public required double MaskClear { get; init; }

    public required double StrokeWidth { get; init; }

    public required IReadOnlyList<RoleStyle> Roles { get; init; }

    public required SceneryCatalog Scenery { get; init; }

    /// <summary>Walk to rocket, in title-ladder order.</summary>
    public required IReadOnlyList<Vehicle> Vehicles { get; init; }

    /// <summary>The vehicle shown parked on first launch (or for an unknown persisted key).</summary>
    public required int IdleFallback { get; init; }

    /// <summary>The embedded art, or null when it failed to load (the hero then shows no vehicle).</summary>
    public static VehicleArt? Shared => SharedArt.Value;

    /// <summary>Why <see cref="Shared"/> is null, if it is.</summary>
    public static string? LoadError { get; private set; }

    public RoleStyle Role(PartRole role) => Roles[(int)role];

    public int IndexOf(string? key)
    {
        if (key is null)
        {
            return -1;
        }

        for (var i = 0; i < Vehicles.Count; i++)
        {
            if (string.Equals(Vehicles[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The vehicle for a speed title (idle has none).</summary>
    public static int IndexFor(SpeedTitle title) => title == SpeedTitle.Idle ? -1 : (int)title - 1;

    /// <summary>The vehicle key of a title (<c>title.sportsCar</c> → <c>sportsCar</c>); null for idle.</summary>
    public static string? KeyFor(SpeedTitle title)
    {
        if (title == SpeedTitle.Idle)
        {
            return null;
        }

        var key = SpeedTitles.Key(title);
        return key.StartsWith("title.", StringComparison.Ordinal) ? key["title.".Length..] : key;
    }

    /// <summary>The persisted <c>heroLastTitle</c> when it names a vehicle of the ladder, else null.</summary>
    public static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 32)
        {
            return null;
        }

        foreach (var step in SpeedTitles.Ladder)
        {
            if (string.Equals(KeyFor(step.Title), key, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }

    public static VehicleArt LoadEmbedded()
    {
        using var stream = typeof(VehicleArt).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        return VehicleArtParser.Parse(stream);
    }

    private static VehicleArt? LoadShared()
    {
        try
        {
            return LoadEmbedded();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Text.Json.JsonException
                                       or IOException or KeyNotFoundException)
        {
            LoadError = ex.Message;
            return null;
        }
    }
}
