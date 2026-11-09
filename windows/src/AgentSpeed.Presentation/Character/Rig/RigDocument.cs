using System.Text.Json;

namespace AgentSpeed.Presentation.Character;

/// <summary>
/// <c>rig.json</c> of a character package (characters/README.md, format <c>agent-speed-character</c> v1). Unknown keys
/// are ignored; landmarks the player does not use are skipped.
/// </summary>
public sealed class RigDocument
{
    public const string Format = "agent-speed-character";
    public const int Version = 1;

    public sealed record Atlas(string File, int[] Size);

    public sealed record Bone(string Name, string? Parent, double[] Pivot);

    public sealed record Driver(string Param, string Bone, string Channel, double[][] Keys);

    public sealed record PartDriver(string Param, string Part, double[][] Keys);

    public sealed record Part(string Name, string Tag, double Order, double[] Vertices, double[] Uv, int[] Triangles, int Influences,
        int[] Bones, double[] Weights);

    public sealed record PhysicsInput(string Param, double? X, double? Y);

    public sealed record PhysicsOutput(string Param, int Segment, double Degrees);

    public sealed record Chain(string Name, double[] Anchor, double[] Segments, PhysicsInput[] Inputs, PhysicsOutput[] Outputs,
        double Stiffness, double Damping, double Wind);

    public sealed record Hand(double[] Wrist, double Size, double? Open);

    public sealed record Landmarks
    {
        public double[]? Face { get; init; }

        public double? FaceWidth { get; init; }

        public double[]? Feet { get; init; }

        public double[]? Waist { get; init; }

        public double? Chin { get; init; }

        public double? Hem { get; init; }

        public double? TorsoLength { get; init; }

        public string? ObjectSide { get; init; }

        public IReadOnlyDictionary<string, Hand>? Hands { get; init; }
    }

    public required string FormatName { get; init; }

    public required int FormatVersion { get; init; }

    public string Name { get; init; } = string.Empty;

    public required double[] Canvas { get; init; }

    public required Atlas AtlasInfo { get; init; }

    public Landmarks LandmarkSet { get; init; } = new();

    public required Bone[] Bones { get; init; }

    public required CharacterParam[] Params { get; init; }

    public Driver[] Drivers { get; init; } = [];

    public Chain[] Physics { get; init; } = [];

    public PartDriver[] PartDrivers { get; init; } = [];

    public required Part[] Parts { get; init; }

    /// <exception cref="JsonException">Not JSON, or a required key is missing or of the wrong type.</exception>
    public static RigDocument Decode(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 64 });
        using var doc = JsonDocument.ParseValue(ref reader);
        return Decode(doc.RootElement);
    }

    public static RigDocument Decode(JsonElement root)
    {
        var c = RigJson.Object(root);
        return new RigDocument
        {
            FormatName = RigJson.String(c, "format"),
            FormatVersion = RigJson.Int(c, "version"),
            Name = RigJson.OptionalString(c, "name") ?? string.Empty,
            Canvas = RigJson.Doubles(RigJson.Required(c, "canvas")),
            AtlasInfo = DecodeAtlas(RigJson.Required(c, "atlas")),
            LandmarkSet = c.TryGetProperty("landmarks", out var lm) && lm.ValueKind != JsonValueKind.Null ? DecodeLandmarks(lm) : new Landmarks(),
            Bones = RigJson.Array(RigJson.Required(c, "bones"), DecodeBone),
            Params = RigJson.Array(RigJson.Required(c, "params"), CharacterParam.Decode),
            Drivers = RigJson.OptionalArray(c, "drivers", DecodeDriver),
            Physics = RigJson.OptionalArray(c, "physics", DecodeChain),
            PartDrivers = RigJson.OptionalArray(c, "partDrivers", DecodePartDriver),
            Parts = RigJson.Array(RigJson.Required(c, "parts"), DecodePart),
        };
    }

    public static Chain DecodeChain(JsonElement e) => new(
        RigJson.String(e, "name"),
        RigJson.Doubles(RigJson.Required(e, "anchor")),
        RigJson.Doubles(RigJson.Required(e, "segments")),
        RigJson.Array(RigJson.Required(e, "inputs"),
            i => new PhysicsInput(RigJson.String(i, "param"), RigJson.OptionalDouble(i, "x"), RigJson.OptionalDouble(i, "y"))),
        RigJson.Array(RigJson.Required(e, "outputs"),
            o => new PhysicsOutput(RigJson.String(o, "param"), RigJson.Int(o, "segment"), RigJson.Double(o, "degrees"))),
        RigJson.Double(e, "stiffness"),
        RigJson.Double(e, "damping"),
        RigJson.Double(e, "wind"));

    private static Atlas DecodeAtlas(JsonElement e) => new(RigJson.String(e, "file"), RigJson.Ints(RigJson.Required(e, "size")));

    private static Bone DecodeBone(JsonElement e) =>
        new(RigJson.String(e, "name"), RigJson.OptionalString(e, "parent"), RigJson.Doubles(RigJson.Required(e, "pivot")));

    private static Driver DecodeDriver(JsonElement e) => new(RigJson.String(e, "param"), RigJson.String(e, "bone"),
        RigJson.String(e, "channel"), RigJson.Array(RigJson.Required(e, "keys"), RigJson.Doubles));

    private static PartDriver DecodePartDriver(JsonElement e) =>
        new(RigJson.String(e, "param"), RigJson.String(e, "part"), RigJson.Array(RigJson.Required(e, "keys"), RigJson.Doubles));

    private static Part DecodePart(JsonElement e) => new(
        RigJson.String(e, "name"),
        RigJson.String(e, "tag"),
        RigJson.Double(e, "order"),
        RigJson.Doubles(RigJson.Required(e, "vertices")),
        RigJson.Doubles(RigJson.Required(e, "uv")),
        RigJson.Ints(RigJson.Required(e, "triangles")),
        RigJson.Int(e, "influences"),
        RigJson.Ints(RigJson.Required(e, "bones")),
        RigJson.Doubles(RigJson.Required(e, "weights")));

    private static Landmarks DecodeLandmarks(JsonElement e)
    {
        RigJson.Object(e);
        Dictionary<string, Hand>? hands = null;
        if (e.TryGetProperty("hands", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            hands = new Dictionary<string, Hand>(StringComparer.Ordinal);
            foreach (var p in h.EnumerateObject())
            {
                hands[p.Name] = new Hand(RigJson.Doubles(RigJson.Required(p.Value, "wrist")), RigJson.Double(p.Value, "size"),
                    RigJson.OptionalDouble(p.Value, "open"));
            }
        }

        return new Landmarks
        {
            Face = RigJson.OptionalDoubles(e, "face"),
            FaceWidth = RigJson.OptionalDouble(e, "faceWidth"),
            Feet = RigJson.OptionalDoubles(e, "feet"),
            Waist = RigJson.OptionalDoubles(e, "waist"),
            Chin = RigJson.OptionalDouble(e, "chin"),
            Hem = RigJson.OptionalDouble(e, "hem"),
            TorsoLength = RigJson.OptionalDouble(e, "torsoLength"),
            ObjectSide = RigJson.OptionalString(e, "objectSide"),
            Hands = hands,
        };
    }
}

/// <summary>Why a package cannot be played.</summary>
public sealed class CharacterRigException : Exception
{
    public CharacterRigException()
    {
    }

    public CharacterRigException(string message)
        : base(message)
    {
    }

    public CharacterRigException(string message, Exception inner)
        : base(message, inner)
    {
    }

    private CharacterRigException(string kind, string detail, bool _)
        : base($"{kind}: {detail}")
    {
        Kind = kind;
        Detail = detail;
    }

    public string Kind { get; } = string.Empty;

    public string Detail { get; } = string.Empty;

    public static CharacterRigException Unsupported(string format, int version) => new("unsupported", $"{format} {version}", true);

    public static CharacterRigException BadBone(string name) => new("badBone", name, true);

    public static CharacterRigException ParentsNotFirst(string name) => new("parentsNotFirst", name, true);

    public static CharacterRigException BadDriver(string name) => new("badDriver", name, true);

    public static CharacterRigException BadPart(string name) => new("badPart", name, true);

    public static CharacterRigException BadChain(string name) => new("badChain", name, true);
}

/// <summary>Strict readers for the rig's JSON: a missing required key or a wrong type throws <see cref="JsonException"/>.</summary>
internal static class RigJson
{
    public static JsonElement Object(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e : throw new JsonException("Expected an object.");

    public static JsonElement Required(JsonElement e, string name) =>
        Object(e).TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new JsonException($"Missing {name}.");

    public static string String(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw new JsonException($"{name} is not a string.");

    public static string? OptionalString(JsonElement e, string name)
    {
        if (!Object(e).TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.String ? v.GetString() : throw new JsonException($"{name} is not a string.");
    }

    public static double Double(JsonElement e, string name) => Number(Required(e, name));

    public static double? OptionalDouble(JsonElement e, string name) =>
        Object(e).TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? Number(v) : null;

    public static int Int(JsonElement e, string name)
    {
        var v = Required(e, name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw new JsonException($"{name} is not an integer.");
    }

    public static double Number(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw new JsonException("Expected a number.");

    public static double[] Doubles(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected an array.");
        }

        var values = new double[v.GetArrayLength()];
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            values[i++] = Number(item);
        }

        return values;
    }

    public static double[]? OptionalDoubles(JsonElement e, string name) =>
        Object(e).TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? Doubles(v) : null;

    public static int[] Ints(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected an array.");
        }

        var values = new int[v.GetArrayLength()];
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            values[i++] = item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n) ? n : throw new JsonException("Expected an integer.");
        }

        return values;
    }

    public static T[] Array<T>(JsonElement v, Func<JsonElement, T> read)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected an array.");
        }

        var values = new T[v.GetArrayLength()];
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            values[i++] = read(item);
        }

        return values;
    }

    public static T[] OptionalArray<T>(JsonElement e, string name, Func<JsonElement, T> read) =>
        Object(e).TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? Array(v, read) : [];
}
