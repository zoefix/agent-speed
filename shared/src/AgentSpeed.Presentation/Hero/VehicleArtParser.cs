using System.Text.Json;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Hero;

/// <summary>
/// Reads <c>vehicles.json</c> and checks the structure the renderers rely on (the full geometric validation lives in
/// <c>spec/tools/vehicles.py</c>). Any problem is a <see cref="FormatException"/>.
/// </summary>
public static class VehicleArtParser
{
    private static readonly string[] RoleNames =
        ["body", "detail", "window", "wheel", "rim", "spoke", "crank", "rider", "light", "glow", "shock", "flame", "flameCore"];

    public static VehicleArt Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        return Parse(document.RootElement);
    }

    public static VehicleArt Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        return Parse(document.RootElement);
    }

    private static VehicleArt Parse(JsonElement root)
    {
        Require(root.ValueKind == JsonValueKind.Object, "the root must be an object");
        Require(Number(root, "version") == 1, "version must be 1");
        var canvas = Object(root, "canvas");
        Require(Number(canvas, "width") == 100 && Number(canvas, "height") == 100, "the canvas must be 100 × 100");
        var orbFraction = Number(canvas, "orbFraction");
        Require(orbFraction is > 0 and < 0.775, "canvas.orbFraction is out of range");

        var safe = Object(root, "safeCircle");
        var mask = Object(root, "mask");
        var solid = Number(mask, "solid");
        var clear = Number(mask, "clear");
        Require(solid > 0 && solid < clear && clear <= 50, "mask radii must satisfy 0 < solid < clear ≤ 50");

        var stroke = Object(root, "stroke");
        var width = Number(stroke, "width");
        Require(width is > 0 and < 10, "stroke.width is out of range");

        var roles = ParseRoles(Object(root, "roles"));
        var scenery = ParseScenery(Object(root, "scenery"));

        var vehicles = new List<Vehicle>();
        var list = root.GetProperty("vehicles");
        Require(list.ValueKind == JsonValueKind.Array, "vehicles must be an array");
        var previousTop = 0.0;
        foreach (var item in list.EnumerateArray())
        {
            var vehicle = ParseVehicle(item, vehicles.Count, roles);
            Require(vehicle.Motion.SpeedLow >= previousTop, $"{vehicle.Key}: its speed range starts below the previous title's top");
            previousTop = vehicle.Motion.SpeedHigh;
            vehicles.Add(vehicle);
        }

        Require(vehicles.Count == SpeedTitles.Ladder.Count, "there must be one vehicle per speed title");
        for (var i = 0; i < vehicles.Count; i++)
        {
            Require(vehicles[i].Key == VehicleArt.KeyFor(SpeedTitles.Ladder[i].Title), $"vehicle {i} must be {VehicleArt.KeyFor(SpeedTitles.Ladder[i].Title)}");
        }

        var idle = Object(root, "idle");
        var fallback = String(idle, "fallback");
        var fallbackIndex = vehicles.FindIndex(v => v.Key == fallback);
        Require(String(idle, "vehicle") == "last" && fallbackIndex >= 0, "idle must name last and a known fallback");

        return new VehicleArt
        {
            OrbFraction = orbFraction,
            SafeCircle = new CircleD(Number(safe, "cx"), Number(safe, "cy"), Number(safe, "r")),
            MaskCenter = new PointD(Number(mask, "cx"), Number(mask, "cy")),
            MaskSolid = solid,
            MaskClear = clear,
            StrokeWidth = width,
            Roles = roles,
            Scenery = scenery,
            Vehicles = vehicles,
            IdleFallback = fallbackIndex,
        };
    }

    private static RoleStyle[] ParseRoles(JsonElement roles)
    {
        var result = new RoleStyle[RoleNames.Length];
        var count = 0;
        foreach (var property in roles.EnumerateObject())
        {
            var index = Array.IndexOf(RoleNames, property.Name);
            Require(index >= 0, $"unknown role {property.Name}");
            var r = property.Value;
            var fill = OptionalString(r, "fill") switch
            {
                null => RoleFill.None,
                "heat" => RoleFill.Heat,
                "hot" => RoleFill.Hot,
                var other => throw new FormatException($"role {property.Name}: unknown fill {other}"),
            };
            result[index] = new RoleStyle(Number(r, "weight"), Number(r, "opacity"), Number(r, "glow"), Bool(r, "sprung"),
                OptionalString(r, "color") == "hot", fill, OptionalNumber(r, "fillOpacity") ?? 1);
            count++;
        }

        Require(count == RoleNames.Length && result.All(r => r is not null), "every role must be defined exactly once");
        return result;
    }

    private static SceneryCatalog ParseScenery(JsonElement s)
    {
        var road = Object(s, "road");
        var dash = road.GetProperty("dash");
        Require(dash.GetArrayLength() == 2, "scenery.road.dash is [dash, gap]");
        var rail = Object(s, "rail");
        var guideway = Object(s, "guideway");
        var sky = Object(s, "sky");
        var clouds = sky.GetProperty("clouds").EnumerateArray().Select(c => PathData.Parse(c.GetString() ?? string.Empty)).ToArray();
        Require(clouds.Length == 2, "scenery.sky needs two clouds");
        var layers = sky.GetProperty("layers").EnumerateArray()
            .Select(l => new SkyLayer(Number(l, "y"), Number(l, "scale"), Number(l, "parallax"), Number(l, "opacity")))
            .ToArray();
        Require(layers.Length > 0 && layers.All(l => l.Scale > 0), "scenery.sky needs layers");
        var stratosphere = Object(s, "stratosphere");
        var horizon = Object(stratosphere, "horizon");
        var space = Object(s, "space");
        var stars = (int)Number(space, "stars");
        Require(stars is > 0 and <= 64, "scenery.space.stars is out of range");

        return new SceneryCatalog(
            dash[0].GetDouble(), dash[1].GetDouble(), Number(road, "parallax"),
            Number(rail, "sleeperSpacing"), Number(rail, "sleeperDepth"), Number(rail, "parallax"),
            Number(guideway, "depth"), Number(guideway, "pylonSpacing"), Number(guideway, "pylonDepth"), Number(guideway, "parallax"),
            clouds, layers, Number(horizon, "cy"), Number(horizon, "r"), stars, Number(space, "parallax"));
    }

    private static Vehicle ParseVehicle(JsonElement v, int index, RoleStyle[] roles)
    {
        var key = String(v, "key");
        var scenery = String(v, "scenery") switch
        {
            "road" => SceneryKind.Road,
            "rail" => SceneryKind.Rail,
            "guideway" => SceneryKind.Guideway,
            "sky" => SceneryKind.Sky,
            "stratosphere" => SceneryKind.Stratosphere,
            "space" => SceneryKind.Space,
            var other => throw new FormatException($"{key}: unknown scenery {other}"),
        };
        var groundY = OptionalNumber(v, "groundY");
        var grounded = scenery is SceneryKind.Road or SceneryKind.Rail or SceneryKind.Guideway;
        Require(grounded == groundY.HasValue, $"{key}: groundY is required exactly for road, rail and guideway scenery");

        var m = Object(v, "motion");
        var speed = m.GetProperty("speed");
        Require(speed.GetArrayLength() == 2, $"{key}: motion.speed is [lo, hi]");
        var motion = new VehicleMotion(speed[0].GetDouble(), speed[1].GetDouble(), OptionalNumber(m, "rest") ?? 0,
            OptionalNumber(m, "bob") ?? 0, OptionalNumber(m, "float") ?? 0, OptionalNumber(m, "hover") ?? 0);
        Require(motion.SpeedLow > 0 && motion.SpeedLow < motion.SpeedHigh, $"{key}: motion.speed must be an increasing pair");

        WalkRigSpec? walk = null;
        PedalRigSpec? pedal = null;
        if (v.TryGetProperty("rig", out var rig))
        {
            switch (String(rig, "type"))
            {
                case "walk":
                    var head = rig.GetProperty("head");
                    walk = new WalkRigSpec(Point(rig, "hip"), Point(rig, "torso"),
                        new CircleD(head[0].GetDouble(), head[1].GetDouble(), head[2].GetDouble()),
                        Number(rig, "thigh"), Number(rig, "shin"), Number(rig, "foot"), Number(rig, "upperArm"), Number(rig, "forearm"),
                        Number(rig, "cycle"), Number(rig, "stance"), Number(rig, "lift"), Number(rig, "bob"), Number(rig, "armSwing"),
                        Number(rig, "elbow"));
                    Require(walk.Cycle > 0 && walk.Stance is > 0 and < 1 && groundY.HasValue, $"{key}: bad walk rig");
                    break;
                case "pedal":
                    var crank = Object(rig, "crank");
                    pedal = new PedalRigSpec(Point(rig, "hip"), Point(crank, "pivot"), Number(crank, "length"), Number(crank, "radius"),
                        Number(rig, "thigh"), Number(rig, "shin"), Number(rig, "pedal"), Number(rig, "bob"));
                    Require(pedal.CrankRadius > 0, $"{key}: bad pedal rig");
                    break;
                default:
                    throw new FormatException($"{key}: unknown rig type");
            }
        }

        var parts = new List<VehiclePart>();
        foreach (var p in v.GetProperty("parts").EnumerateArray())
        {
            parts.Add(ParsePart(p, key, roles));
        }

        Require(parts.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == parts.Count, $"{key}: duplicate part ids");

        var emitters = new List<EmitterSpec>();
        foreach (var e in v.GetProperty("emitters").EnumerateArray())
        {
            emitters.Add(ParseEmitter(e, key));
        }

        var heading = OptionalNumber(v, "heading");
        var bounds = Bounds(parts, walk, pedal, groundY);
        var rearSpoke = parts.FirstOrDefault(p => p.Spin is not null && p.Role == PartRole.Spoke);
        var pivot = rearSpoke is not null && groundY is { } g
            ? new PointD(rearSpoke.Spin!.Pivot.X, g)
            : bounds.Center;

        return new Vehicle
        {
            Key = key,
            Index = index,
            Scenery = scenery,
            GroundY = groundY,
            Heading = heading,
            Motion = motion,
            Walk = walk,
            Pedal = pedal,
            Parts = parts,
            Emitters = emitters,
            Bounds = bounds,
            HasWheels = parts.Any(p => p.Role == PartRole.Wheel),
            PitchPivot = pivot,
        };
    }

    private static VehiclePart ParsePart(JsonElement p, string key, RoleStyle[] roles)
    {
        var id = String(p, "id");
        var roleName = String(p, "role");
        var roleIndex = Array.IndexOf(RoleNames, roleName);
        Require(roleIndex >= 0, $"{key}.{id}: unknown role {roleName}");
        var role = (PartRole)roleIndex;
        var hasPath = p.TryGetProperty("d", out var d);
        var hasCircle = p.TryGetProperty("circle", out var c);
        Require(hasPath != hasCircle, $"{key}.{id}: exactly one of d and circle");

        PathData? path = null;
        CircleD? circle = null;
        try
        {
            if (hasPath)
            {
                path = PathData.Parse(d.GetString() ?? string.Empty);
            }
        }
        catch (FormatException ex)
        {
            throw new FormatException($"{key}.{id}: {ex.Message}", ex);
        }

        if (hasCircle)
        {
            Require(c.GetArrayLength() == 3 && c[2].GetDouble() > 0, $"{key}.{id}: circle is [cx, cy, r]");
            circle = new CircleD(c[0].GetDouble(), c[1].GetDouble(), c[2].GetDouble());
        }

        SpinSpec? spin = null;
        if (p.TryGetProperty("spin", out var s))
        {
            spin = new SpinSpec(Point(s, "pivot"), Number(s, "radius"), (int)(OptionalNumber(s, "symmetry") ?? 1));
            Require(spin.Radius > 0 && spin.Symmetry > 0, $"{key}.{id}: spin needs a positive radius and symmetry");
        }

        NozzleSpec? nozzle = null;
        if (p.TryGetProperty("nozzle", out var n))
        {
            nozzle = new NozzleSpec(Point(n, "at"), Point(n, "dir"));
        }

        if (role is PartRole.Flame or PartRole.FlameCore)
        {
            Require(path is not null && nozzle is not null, $"{key}.{id}: flames are closed paths with a nozzle");
        }

        return new VehiclePart
        {
            Id = id,
            Role = role,
            Path = path,
            Circle = circle,
            Spin = spin,
            Nozzle = nozzle,
            Sprung = p.TryGetProperty("sprung", out var sprung) ? sprung.GetBoolean() : roles[roleIndex].Sprung,
            Bleed = p.TryGetProperty("bleed", out var bleed) && bleed.GetBoolean(),
        };
    }

    private static EmitterSpec ParseEmitter(JsonElement e, string key)
    {
        var id = String(e, "id");
        var kind = String(e, "type") switch
        {
            "dust" => EmitterKind.Dust,
            "arc" => EmitterKind.Arc,
            "exhaust" => EmitterKind.Exhaust,
            "trail" => EmitterKind.Trail,
            "sparks" => EmitterKind.Sparks,
            "contrail" => EmitterKind.Contrail,
            "afterburner" => EmitterKind.Afterburner,
            "plume" => EmitterKind.Plume,
            var other => throw new FormatException($"{key}.{id}: unknown emitter type {other}"),
        };
        double[] lanes = e.TryGetProperty("lanes", out var l) ? l.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [];
        Require(kind != EmitterKind.Trail || lanes.Length is > 0 and <= 4, $"{key}.{id}: a trail needs 1–4 lanes");
        return new EmitterSpec(id, kind, Point(e, "at"), Point(e, "dir"), (int)(OptionalNumber(e, "count") ?? 0), lanes,
            OptionalNumber(e, "length") ?? 0, e.TryGetProperty("weak", out var weak) && weak.GetBoolean());
    }

    /// <summary>Bounds of the solid drawing, matching the prototype: bleeding bodies count only beyond x = 30.</summary>
    private static RectD Bounds(List<VehiclePart> parts, WalkRigSpec? walk, PedalRigSpec? pedal, double? groundY)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double x, double y)
        {
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
        }

        foreach (var part in parts)
        {
            if (part.Bleed || part.Role is PartRole.Flame or PartRole.FlameCore or PartRole.Shock or PartRole.Glow)
            {
                if (part.Bleed && part.Path is { } bleed && part.Role == PartRole.Body)
                {
                    foreach (var point in bleed.ControlPoints())
                    {
                        if (point.X > 30)
                        {
                            Add(point.X, point.Y);
                        }
                    }
                }

                continue;
            }

            if (part.Circle is { } c)
            {
                Add(c.X - c.Radius, c.Y - c.Radius);
                Add(c.X + c.Radius, c.Y + c.Radius);
            }
            else if (part.Path is { } path)
            {
                foreach (var point in path.ControlPoints())
                {
                    Add(point.X, point.Y);
                }
            }
        }

        if (walk is not null)
        {
            Add(walk.Hip.X - 10, walk.Hip.Y + walk.Torso.Y + walk.Head.Y - walk.Head.Radius);
            Add(walk.Hip.X + 12, groundY ?? walk.Hip.Y);
        }
        else if (pedal is not null)
        {
            Add(pedal.Hip.X - 10, pedal.Hip.Y - 20);
            Add(pedal.Hip.X + 12, groundY ?? pedal.Hip.Y);
        }

        return x0 <= x1 ? RectD.FromEdges(x0, y0, x1, y1) : new RectD(40, 40, 20, 20);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new FormatException("vehicles.json: " + message);
        }
    }

    private static JsonElement Object(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"vehicles.json: {name} must be an object");
        }

        return value;
    }

    private static double Number(JsonElement parent, string name) =>
        OptionalNumber(parent, name) ?? throw new FormatException($"vehicles.json: {name} must be a number");

    private static double? OptionalNumber(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            throw new FormatException($"vehicles.json: {name} must be a finite number");
        }

        return number;
    }

    private static string String(JsonElement parent, string name) =>
        OptionalString(parent, name) ?? throw new FormatException($"vehicles.json: {name} must be a string");

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static PointD Point(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
        {
            throw new FormatException($"vehicles.json: {name} must be [x, y]");
        }

        return new PointD(value[0].GetDouble(), value[1].GetDouble());
    }
}
