using System.Text.Json;

namespace AgentSpeed.Presentation.Character;

/// <summary>A rig parameter: its id, range and default.</summary>
public sealed record CharacterParam(string Id, double Min, double Max, double Default)
{
    public static CharacterParam Decode(JsonElement e) =>
        new(RigJson.String(e, "id"), RigJson.Double(e, "min"), RigJson.Double(e, "max"), RigJson.Double(e, "default"));
}

/// <summary>
/// The parameters the director writes: the rig's own, then every name a motion may write, each at a fixed slot. Values
/// live in dense arrays indexed by slot; names are resolved once, never per frame.
/// </summary>
public sealed class ParamSpace
{
    private readonly List<string> _names = [];
    private readonly List<CharacterParam?> _params = [];
    private readonly List<SpringTuning?> _springs = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public ParamSpace(IEnumerable<CharacterParam> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var p in parameters)
        {
            if (!_index.ContainsKey(p.Id))
            {
                Add(p.Id, p);
            }
        }

        foreach (var name in Vocabulary)
        {
            if (!_index.ContainsKey(name))
            {
                Add(name, null);
            }
        }
    }

    public IReadOnlyList<string> Names => _names;

    public IReadOnlyList<CharacterParam?> Params => _params;

    public IReadOnlyList<SpringTuning?> Springs => _springs;

    public int Count => _names.Count;

    /// <summary>Every parameter a motion of the reference can write.</summary>
    public static IReadOnlyList<string> Vocabulary { get; } = BuildVocabulary();

    private void Add(string name, CharacterParam? param)
    {
        _index[name] = _names.Count;
        _names.Add(name);
        _params.Add(param);
        _springs.Add(SpringTuning.Of(name));
    }

    public int? Slot(string name) => _index.TryGetValue(name, out var i) ? i : null;

    public bool IsParam(int slot) => _params[slot] is not null;

    public bool IsParam(string name) => _index.TryGetValue(name, out var i) && _params[i] is not null;

    private static string[] BuildVocabulary()
    {
        var names = new List<string>
        {
            "AngleX", "AngleY", "AngleZ", "BodyAngleZ", "BodySway", "Breath", "EyeOpen.left", "EyeOpen.right",
            "EyeBallX", "EyeBallY", "Wind", "PosX", "PosY", "Squash", "Lean", "Spin", "Hip", "SkirtFlare",
            "FootLift", "FootPoint", "TailSwish", "WingFlap", "EarTwitch.left", "EarTwitch.right",
        };
        foreach (var side in CharacterSides.All)
        {
            var s = side.Key();
            names.AddRange([$"Shoulder.{s}", $"Arm.{s}", $"Forearm.{s}", $"Lift.{s}", $"Bend.{s}", $"Wrist.{s}", $"Spread.{s}"]);
            for (var i = 0; i < 5; i++)
            {
                names.Add($"Finger.{s}.{i}");
            }
        }

        return names.ToArray();
    }
}

/// <summary>An image side of the character (the reference's <c>left</c> / <c>right</c>).</summary>
public enum CharacterSide
{
    Left,
    Right,
}

public static class CharacterSides
{
    public static IReadOnlyList<CharacterSide> All { get; } = [CharacterSide.Left, CharacterSide.Right];

    public static string Key(this CharacterSide side) => side == CharacterSide.Left ? "left" : "right";

    public static CharacterSide Other(this CharacterSide side) => side == CharacterSide.Left ? CharacterSide.Right : CharacterSide.Left;

    public static CharacterSide? Parse(string? key) => key switch
    {
        "left" => CharacterSide.Left,
        "right" => CharacterSide.Right,
        _ => null,
    };
}

/// <summary>The slots the motions write, resolved once from a <see cref="ParamSpace"/>.</summary>
internal sealed class Slots
{
    internal sealed class Side
    {
        public required int Shoulder { get; init; }
        public required int Arm { get; init; }
        public required int Forearm { get; init; }
        public required int Lift { get; init; }
        public required int Bend { get; init; }
        public required int Wrist { get; init; }
        public required int Spread { get; init; }
        public required int[] Fingers { get; init; }
    }

    public Slots(ParamSpace space)
    {
        int S(string name) => space.Slot(name)!.Value;
        AngleX = S("AngleX");
        AngleY = S("AngleY");
        AngleZ = S("AngleZ");
        BodyAngleZ = S("BodyAngleZ");
        BodySway = S("BodySway");
        Breath = S("Breath");
        EyeBallX = S("EyeBallX");
        EyeBallY = S("EyeBallY");
        Wind = S("Wind");
        PosX = S("PosX");
        PosY = S("PosY");
        Squash = S("Squash");
        Lean = S("Lean");
        Spin = S("Spin");
        Hip = S("Hip");
        SkirtFlare = S("SkirtFlare");
        FootLift = S("FootLift");
        FootPoint = S("FootPoint");
        TailSwish = S("TailSwish");
        WingFlap = S("WingFlap");
        EyeOpen = [S("EyeOpen.left"), S("EyeOpen.right")];
        Side SideOf(string x) => new()
        {
            Shoulder = S($"Shoulder.{x}"),
            Arm = S($"Arm.{x}"),
            Forearm = S($"Forearm.{x}"),
            Lift = S($"Lift.{x}"),
            Bend = S($"Bend.{x}"),
            Wrist = S($"Wrist.{x}"),
            Spread = S($"Spread.{x}"),
            Fingers = Enumerable.Range(0, 5).Select(i => S($"Finger.{x}.{i}")).ToArray(),
        };
        Left = SideOf("left");
        Right = SideOf("right");
    }

    public int AngleX { get; }
    public int AngleY { get; }
    public int AngleZ { get; }
    public int BodyAngleZ { get; }
    public int BodySway { get; }
    public int Breath { get; }
    public int EyeBallX { get; }
    public int EyeBallY { get; }
    public int Wind { get; }
    public int PosX { get; }
    public int PosY { get; }
    public int Squash { get; }
    public int Lean { get; }
    public int Spin { get; }
    public int Hip { get; }
    public int SkirtFlare { get; }
    public int FootLift { get; }
    public int FootPoint { get; }
    public int TailSwish { get; }
    public int WingFlap { get; }
    public int[] EyeOpen { get; }
    public Side Left { get; }
    public Side Right { get; }

    public Side SideOf(CharacterSide side) => side == CharacterSide.Left ? Left : Right;
}
