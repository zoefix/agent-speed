using System.Text.Json;

namespace AgentSpeed.Presentation.Character;

/// <summary>
/// A character's motion personality (motions/profile.py, spec/CHARACTERS.md Motion profile): the manifest's
/// <c>motion</c> block. Every key is optional; the defaults are Claude's behavior.
/// </summary>
public sealed record MotionProfile
{
    public double Tempo { get; init; } = 1;

    public double Energy { get; init; } = 1;

    public double Sway { get; init; } = 1;

    public double Bounce { get; init; } = 1;

    public double Gap { get; init; } = 1;

    /// <summary>The idle groove: <c>read</c> (looking down at a book) or <c>rest</c>.</summary>
    public string Idle { get; init; } = "read";

    public bool Seated { get; init; }

    public bool Talk { get; init; } = true;

    public IReadOnlyList<string> Signature { get; init; } = [];

    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Fidget sets of the free hand.</summary>
    public IReadOnlyList<string> Hands { get; init; } = [];

    /// <summary>A hand set per pose band, played back to back on its hands.</summary>
    public IReadOnlyDictionary<string, string> PoseHands { get; init; } = new Dictionary<string, string>();

    /// <summary>The ambient particle kind (<c>petals</c>, <c>bubbles</c>, <c>notes</c>, ...).</summary>
    public string? Particles { get; init; }

    /// <summary>The bands an action fits in, lowest and highest.</summary>
    internal static readonly IReadOnlyDictionary<string, (SpeedBand Low, SpeedBand High)> ActionBands =
        new Dictionary<string, (SpeedBand, SpeedBand)>(StringComparer.Ordinal)
        {
            ["yawn"] = (SpeedBand.Idle, SpeedBand.Cruise), ["stretch"] = (SpeedBand.Idle, SpeedBand.Cruise),
            ["ponder"] = (SpeedBand.Idle, SpeedBand.Cruise),
            ["bow"] = (SpeedBand.Idle, SpeedBand.Boost), ["curtsy"] = (SpeedBand.Idle, SpeedBand.Boost),
            ["nod"] = (SpeedBand.Idle, SpeedBand.Boost), ["tilt"] = (SpeedBand.Idle, SpeedBand.Boost),
            ["lookaround"] = (SpeedBand.Idle, SpeedBand.Boost), ["shake"] = (SpeedBand.Idle, SpeedBand.Boost),
            ["foottap"] = (SpeedBand.Idle, SpeedBand.Boost), ["floatbob"] = (SpeedBand.Idle, SpeedBand.Boost),
            ["hum"] = (SpeedBand.Cruise, SpeedBand.Boost), ["sway"] = (SpeedBand.Cruise, SpeedBand.Boost),
            ["tiptoe"] = (SpeedBand.Cruise, SpeedBand.Boost), ["peek"] = (SpeedBand.Cruise, SpeedBand.Boost),
            ["giggle"] = (SpeedBand.Cruise, SpeedBand.Overdrive), ["doubletake"] = (SpeedBand.Cruise, SpeedBand.Overdrive),
            ["step"] = (SpeedBand.Idle, SpeedBand.Overdrive), ["swagger"] = (SpeedBand.Cruise, SpeedBand.Overdrive),
            ["hop"] = (SpeedBand.Cruise, SpeedBand.Redline), ["hops"] = (SpeedBand.Boost, SpeedBand.Redline),
            ["spin"] = (SpeedBand.Boost, SpeedBand.Redline), ["pounce"] = (SpeedBand.Boost, SpeedBand.Redline),
            ["run"] = (SpeedBand.Overdrive, SpeedBand.Redline), ["jump"] = (SpeedBand.Overdrive, SpeedBand.Redline),
            ["jumpjoy"] = (SpeedBand.Overdrive, SpeedBand.Redline), ["tailswish"] = (SpeedBand.Idle, SpeedBand.Redline),
            ["wingflap"] = (SpeedBand.Idle, SpeedBand.Redline), ["eartwitch"] = (SpeedBand.Idle, SpeedBand.Redline),
        };

    internal static readonly HashSet<string> SeatedSkip = new(StringComparer.Ordinal)
    {
        "hop", "jump", "hops", "spin", "step", "run", "jumpjoy", "tiptoe", "foottap", "curtsy", "pounce", "floatbob",
    };

    internal static readonly string[] SeatedFreeze = ["PosX", "PosY", "Squash", "Lean", "Hip", "FootLift", "FootPoint", "BodySway", "SkirtFlare"];

    internal static readonly HashSet<string> SilentSkip = new(StringComparer.Ordinal) { "hum", "giggle" };

    /// <summary>
    /// Sets played on an instrument held in both hands: while one plays, the arms keep their pose so the hands never
    /// slide along the instrument, and the body carries both with it.
    /// </summary>
    internal static readonly HashSet<string> TwoHanded = new(StringComparer.Ordinal) { "flute", "grip" };

    /// <summary>Sets played on an instrument at the lips: the head keeps its pose too.</summary>
    internal static readonly HashSet<string> AtMouth = new(StringComparer.Ordinal) { "flute" };

    internal static readonly string[] HeadParams = ["AngleX", "AngleY", "AngleZ"];
    internal static readonly string[] ArmParams = ["Arm.", "Forearm.", "Shoulder.", "Lift.", "Bend."];

    /// <summary>Decodes a <c>motion</c> block tolerantly: wrong types take their defaults, numbers are clamped to 0…4.</summary>
    public static MotionProfile Decode(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return new MotionProfile();
        }

        double Number(string key, double fallback)
        {
            if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !double.IsFinite(v.GetDouble()))
            {
                return fallback;
            }

            return Math.Min(Math.Max(v.GetDouble(), 0), 4);
        }

        string? Text(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        bool? Flag(string key) => e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        // Swift decodes a whole list or nothing: one item of the wrong type drops the list.
        string[] List(string key)
        {
            if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var items = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return [];
                }

                items.Add(item.GetString()!);
            }

            return items.ToArray();
        }

        var poseHands = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.TryGetProperty("poseHands", out var ph) && ph.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ph.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                {
                    poseHands.Clear();
                    break;
                }

                poseHands[p.Name] = p.Value.GetString()!;
            }
        }

        return new MotionProfile
        {
            Tempo = Math.Max(0.25, Number("tempo", 1)),
            Energy = Number("energy", 1),
            Sway = Number("sway", 1),
            Bounce = Number("bounce", 1),
            Gap = Math.Max(0.25, Number("gap", 1)),
            Idle = Text("idle") ?? "read",
            Seated = Flag("seated") ?? false,
            Talk = Flag("talk") ?? true,
            Signature = List("signature").Where(ActionBands.ContainsKey).ToArray(),
            Exclude = List("exclude"),
            Hands = List("hands").Where(h => HandActor.Sets.Contains(h)).ToArray(),
            PoseHands = poseHands.Where(p => SpeedBands.Parse(p.Key) is not null && HandActor.Sets.Contains(p.Value))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Particles = Text("particles"),
        };
    }

    internal bool Allows(string action)
    {
        if (Exclude.Contains(action))
        {
            return false;
        }

        if (Seated && SeatedSkip.Contains(action))
        {
            return false;
        }

        return Talk || !SilentSkip.Contains(action);
    }

    /// <summary>The band's pool: the base actions it allows, then each signature action that fits the band three times in all.</summary>
    internal List<string> Pool(IReadOnlyList<string> baseActions, SpeedBand band)
    {
        var output = baseActions.Where(Allows).ToList();
        foreach (var a in Signature)
        {
            if (!ActionBands.TryGetValue(a, out var range) || !Allows(a) || range.Low > band || band > range.High)
            {
                continue;
            }

            var copies = baseActions.Contains(a) ? 2 : 3;
            for (var i = 0; i < copies; i++)
            {
                output.Add(a);
            }
        }

        return output;
    }

    /// <summary>How a groove's value for <paramref name="name"/> scales: bounce, sway or energy (motions/rhythm.py, groove_scale).</summary>
    internal double GrooveScale(string name)
    {
        if (name is "PosY" or "Squash" or "FootPoint" or "FootLift" or "SkirtFlare")
        {
            return Bounce;
        }

        if (name is "Hip" or "BodyAngleZ" or "Lean" or "AngleZ")
        {
            return Sway;
        }

        return name.StartsWith("Arm.", StringComparison.Ordinal) || name.StartsWith("Forearm.", StringComparison.Ordinal)
            || name.StartsWith("Shoulder.", StringComparison.Ordinal)
            ? Energy
            : 1;
    }

    internal string? PoseHandsFor(SpeedBand band) => PoseHands.TryGetValue(band.Key(), out var set) ? set : null;
}
