namespace AgentSpeed.Presentation.Character;

/// <summary>The moves of the motion profiles (motions/moves.py: tailswish, wingflap, eartwitch, floatbob, pounce, swagger).</summary>
internal static partial class Moves
{
    /// <summary>Profile moves and the rig parameters each needs (any one of them).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Profile = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["tailswish"] = ["TailSwish"],
        ["wingflap"] = ["WingFlap"],
        ["eartwitch"] = ["EarTwitch.left", "EarTwitch.right"],
        ["floatbob"] = ["PosY"],
        ["swagger"] = ["Hip"],
    };

    public static MotionSpec? ProfileMove(string name, CharacterSide output, CharacterSide book) => name switch
    {
        "tailswish" => Tailswish(),
        "wingflap" => Wingflap(),
        "eartwitch" => Eartwitch(),
        "floatbob" => Floatbob(output, book),
        "swagger" => Swagger(output),
        _ => null,
    };

    private static MotionSpec Tailswish()
    {
        const double d = 1.6;
        var c = new CurveSet();
        c.Set("TailSwish", [(0, 0), .. Keys.Oscillate(0.1, d - 0.15, 0.7, 0.9), (d, 0)]);
        c.Set("Hip", [(0, 0), .. Keys.Oscillate(0.1, d - 0.15, 0.7, 0.18, phase: Math.PI), (d, 0)]);
        c.Set("AngleZ", [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)]);
        return new MotionSpec("tailswish", d, c);
    }

    private static MotionSpec Wingflap()
    {
        const double d = 1.5;
        var c = new CurveSet();
        c.Set("WingFlap", [(0, 0), (0.2, -0.25), (0.42, 1.0), (0.62, -0.1), (0.84, 0.9), (1.06, -0.05), (1.3, 0.3), (d, 0)]);
        c.Set("PosY", [(0, 0), (0.42, 0.03), (0.62, 0.0), (0.84, 0.035), (1.06, 0.0), (d, 0)]);
        c.Set("Squash", [(0, 0), (0.2, -0.15), (0.42, 0.12), (0.62, -0.05), (0.84, 0.1), (d, 0)]);
        Both(c, "Shoulder", [(0, 0), (0.42, 0.3), (0.84, 0.25), (d, 0)]);
        return new MotionSpec("wingflap", d, c, events: [new MotionEvent(0.42, "petals")]);
    }

    private static MotionSpec Eartwitch()
    {
        const double d = 1.0;
        static List<(double T, double V)> Flick(double t0) =>
            [(0, 0), (t0, 0), (t0 + 0.06, 1.0), (t0 + 0.14, -0.35), (t0 + 0.24, 0.15), (t0 + 0.36, 0), (d, 0)];
        var c = new CurveSet();
        c.Set("EarTwitch.left", Flick(0.08));
        c.Set("EarTwitch.right", Flick(0.3));
        c.Set("AngleZ", [(0, 0), (0.2, 0.12), (0.6, -0.08), (d, 0)]);
        c.Set("EyeBallX", [(0, 0), (0.2, 0.5), (0.6, -0.3), (d, 0)]);
        return new MotionSpec("eartwitch", d, c);
    }

    private static MotionSpec Floatbob(CharacterSide output, CharacterSide book)
    {
        const double d = 3.4;
        var c = new CurveSet();
        c.Set("PosY", [(0, 0), .. Keys.Oscillate(0.2, d - 0.3, 1.6, 0.035, baseValue: 0.03), (d, 0)]);
        c.Set("Lean", Swing(d, 1.7, 0.18));
        c.Set("AngleZ", Swing(d, 3.2, 0.3));
        c.Set("Hip", Swing(d, 1.7, 0.22, phase: Math.PI));
        c.Set($"Arm.{output.Key()}", Swing(d, 1.6, 0.15, baseValue: 0.3, ease: 0.5));
        c.Set($"Arm.{book.Key()}", Swing(d, 1.6, 0.1, baseValue: 0.2, phase: Math.PI, ease: 0.5));
        c.Set("SkirtFlare", [(0, 0), (0.5, 0.3), (d - 0.5, 0.3), (d, 0)]);
        c.Set("TailSwish", Swing(d, 1.7, 0.5));
        c.Set("AngleY", [(0, 0), (0.6, 0.15), (d - 0.6, 0.15), (d, 0)]);
        return new MotionSpec("floatbob", d, c, fade: 0.25, expression: "smile");
    }

    public static MotionSpec Pounce(CharacterSide output, CharacterSide book, double orb)
    {
        const double d = 1.25;
        var c = new CurveSet();
        c.Set("Squash", [(0, 0), (0.3, -0.6), (0.5, -0.62), (0.56, 0.4), (0.76, 0.1), (0.86, -0.45), (1.0, 0.1), (d, 0)]);
        c.Set("PosY", [(0, 0), (0.5, 0), .. Keys.Ballistic(0.54, 0.32, 0.22), (d, 0)]);
        c.Set("Lean", [(0, 0), (0.3, 0.3 * orb), (0.5, 0.35 * orb), (0.7, 0.1 * orb), (d, 0)]);
        c.Set($"Arm.{output.Key()}", [(0, 0), (0.3, -0.1), (0.6, 0.55), (0.9, 0.2), (d, 0)]);
        c.Set($"Forearm.{output.Key()}", [(0, 0), (0.3, 0.2), (0.6, -0.4), (d, 0)]);
        c.Set($"Arm.{book.Key()}", [(0, 0), (0.6, 0.3), (d, 0)]);
        c.Set("AngleY", [(0, 0), (0.3, -0.3), (0.6, 0.25), (0.9, -0.1), (d, 0)]);
        c.Set("FootPoint", [(0, 0), (0.54, 0.2), (0.6, 1.0), (0.84, 0.8), (0.86, 0), (d, 0)]);
        c.Set("SkirtFlare", [(0, 0), (0.7, 0.5), (0.9, 0.6), (d, 0)]);
        c.Set("TailSwish", [(0, 0), (0.3, -0.6), (0.6, 0.9), (d, 0)]);
        return new MotionSpec("pounce", d, c, expression: "happy", events: [new MotionEvent(0.56, "petals")]);
    }

    private static MotionSpec Swagger(CharacterSide output)
    {
        const double d = 1.9;
        var o = output.Key();
        var c = new CurveSet();
        c.Set("Hip", [(0, 0), (0.4, 0.6), (1.4, 0.6), (d, 0)]);
        c.Set("BodyAngleZ", [(0, 0), (0.4, -0.35), (1.4, -0.3), (d, 0)]);
        c.Set("Lean", [(0, 0), (0.4, -0.15), (1.4, -0.12), (d, 0)]);
        c.Set("AngleZ", [(0, 0), (0.45, 0.35), (1.4, 0.3), (d, 0)]);
        c.Set("AngleY", [(0, 0), (0.45, 0.2), (1.4, 0.2), (d, 0)]);
        c.Set($"Shoulder.{o}", [(0, 0), (0.4, 0.5), (0.8, 0.2), (1.2, 0.5), (d, 0)]);
        c.Set($"Arm.{o}", [(0, 0), (0.4, 0.2), (1.4, 0.2), (d, 0)]);
        c.Set("EyeBallX", [(0, 0), (0.45, 0.6), (1.4, 0.6), (d, 0)]);
        return new MotionSpec("swagger", d, c, expression: "determined");
    }
}
