namespace AgentSpeed.Presentation.Character;

/// <summary>
/// Whole-body moves (motions/moves.py): hops, a jump, a paper-doll spin, side steps, a curtsy, tiptoes, foot taps, a
/// sway dance, running in place, a stretch, a bow and a peek at the orb. <c>out</c> is the free arm, <c>book</c> the arm
/// holding an object, <c>orb</c> is -1 or 1 toward the orb.
/// </summary>
internal static partial class Moves
{
    public static readonly string[] Names = ["hop", "jump", "hops", "spin", "curtsy", "tiptoe", "foottap", "sway", "run", "stretch", "bow"];

    public static MotionSpec? Make(string name, CharacterSide output, CharacterSide book) => name switch
    {
        "hop" => Hop(output, book),
        "jump" => Jump(output, book),
        "hops" => Hops(output, book),
        "spin" => Spin(output, book),
        "curtsy" => Curtsy(output, book),
        "tiptoe" => Tiptoe(output, book),
        "foottap" => Foottap(output, book),
        "sway" => Sway(output, book),
        "run" => Run(output, book),
        "stretch" => Stretch(output, book),
        "bow" => Bow(output, book),
        _ => null,
    };

    private static void Both(CurveSet c, string prefix, IEnumerable<(double T, double V)> keys)
    {
        var list = keys.ToList();
        c.Set($"{prefix}.left", list);
        c.Set($"{prefix}.right", list);
    }

    internal static List<(double T, double V)> Swing(double d, double period, double amplitude, double baseValue = 0, double phase = 0,
        double ease = 0.15) =>
        [(0, 0), .. Keys.Oscillate(ease, d - ease, period, amplitude, baseValue, phase), (d, 0)];

    public static MotionSpec Hop(CharacterSide output, CharacterSide book, double height = 0.3, double air = 0.36, string name = "hop",
        string expression = "happy", bool arms = true)
    {
        const double t0 = 0.14;
        const double t1 = 0.22;
        var t2 = t1 + air;
        var t3 = t2 + 0.08;
        var t4 = t3 + 0.26;
        var apex = t1 + air / 2;
        var c = new CurveSet();
        c.Set("PosY", [(0, 0), .. Keys.Ballistic(t1, air, height), (t4, 0)]);
        c.Set("Squash", [(0, 0), (t0, -0.55), (t1, 0.45), (apex, 0.08), (t2 - 0.02, 0.3), (t3, -0.6), (t3 + 0.1, 0.12), (t4, 0)]);
        c.Set("SkirtFlare", [(0, 0), (t1, 0.05), (apex, 0.6), (t2, 0.45), (t3, 0.75), (t4, 0)]);
        c.Set("FootPoint", [(0, 0), (t1, 0.2), (t1 + 0.08, 1.0), (t2 - 0.06, 0.9), (t2, 0), (t4, 0)]);
        c.Set("FootLift", [(0, 0), (t1, 0), (t1 + 0.1, 0.5), (t2 - 0.08, 0.3), (t2, 0), (t4, 0)]);
        c.Set("AngleY", [(0, 0), (t0, -0.3), (t1 + 0.05, 0.35), (t2, 0.0), (t3, -0.35), (t4, 0)]);
        Both(c, "Shoulder", [(0, 0), (t0, -0.4), (t1 + 0.05, 0.7), (t2, 0.2), (t3, -0.3), (t4, 0)]);
        if (arms)
        {
            c.Set($"Arm.{output.Key()}", [(0, 0), (t0, -0.1), (t1 + 0.08, 0.35), (t2, 0.15), (t3, 0), (t4, 0)]);
            c.Set($"Arm.{book.Key()}", [(0, 0), (t0, -0.05), (t1 + 0.08, 0.25), (t2, 0.1), (t3, 0), (t4, 0)]);
        }

        return new MotionSpec(name, t4, c, expression: expression, events: [new MotionEvent(t1, "petals")]);
    }

    public static MotionSpec Jump(CharacterSide output, CharacterSide book, bool arms = true) =>
        Hop(output, book, height: 0.45, air: 0.48, name: "jump", arms: arms);

    public static MotionSpec Hops(CharacterSide output, CharacterSide book, int count = 3, double height = 0.22, double air = 0.3)
    {
        var ao = $"Arm.{output.Key()}";
        var ab = $"Arm.{book.Key()}";
        string[] order = ["PosY", "Squash", "FootPoint", "SkirtFlare", "AngleY", "Hip", ao, ab];
        var keys = new Dictionary<string, List<(double T, double V)>>(StringComparer.Ordinal);
        foreach (var p in order)
        {
            keys[p] = [(0, 0)];
        }

        var t = 0.12;
        keys["Squash"].Add((t, -0.4));
        for (var i = 0; i < count; i++)
        {
            double side = i % 2 == 0 ? 1 : -1;
            var t1 = t + 0.06;
            var apex = t1 + air / 2;
            keys["Squash"].AddRange([(t1, 0.35), (apex, 0.05)]);
            keys["PosY"].AddRange(Keys.Ballistic(t1, air, height));
            keys["SkirtFlare"].AddRange([(apex, 0.5), (t1 + air, 0.35)]);
            keys["FootPoint"].AddRange([(t1 + 0.06, 1.0), (t1 + air - 0.05, 0.8), (t1 + air, 0.0)]);
            keys["AngleY"].AddRange([(apex, 0.25), (t1 + air, -0.15)]);
            keys["Hip"].Add((apex, 0.3 * side));
            keys[ao].Add((apex, 0.4));
            keys[ab].Add((apex, 0.2));
            t = t1 + air;
            keys["Squash"].Add((t + 0.06, -0.45));
            t += 0.06;
        }

        var end = t + 0.28;
        var c = new CurveSet();
        foreach (var p in order)
        {
            c.Set(p, [.. keys[p], (end, 0)]);
        }

        return new MotionSpec("hops", end, c, expression: "happy");
    }

    public static MotionSpec Spin(CharacterSide output, CharacterSide book)
    {
        const double d = 0.95;
        var c = new CurveSet();
        c.Set("Spin", [(0, 0), (0.12, 0), (0.68, 1.0), (d, 1.0)]);
        c.Set("PosY", [(0, 0), (0.12, 0), .. Keys.Ballistic(0.14, 0.52, 0.22), (d, 0)]);
        c.Set("Squash", [(0, 0), (0.1, -0.35), (0.16, 0.3), (0.42, 0.15), (0.64, 0.2), (0.72, -0.4), (d, 0)]);
        c.Set("SkirtFlare", [(0, 0), (0.18, 0.6), (0.42, 1.0), (0.7, 0.8), (d, 0)]);
        c.Set($"Arm.{output.Key()}", [(0, 0), (0.16, 0.5), (0.62, 0.5), (d, 0)]);
        c.Set($"Arm.{book.Key()}", [(0, 0), (0.16, 0.3), (0.62, 0.3), (d, 0)]);
        c.Set("AngleZ", [(0, 0), (0.18, 0.3), (0.62, 0.3), (d, 0)]);
        c.Set("AngleY", [(0, 0), (0.18, 0.25), (0.62, 0.2), (d, 0)]);
        c.Set("FootPoint", [(0, 0), (0.16, 1.0), (0.62, 1.0), (0.7, 0), (d, 0)]);
        return new MotionSpec("spin", d, c, expression: "happy", events: [new MotionEvent(0.25, "petals"), new MotionEvent(0.5, "petals")]);
    }

    public static MotionSpec Sidestep(CharacterSide output, CharacterSide book, double dx)
    {
        const double d = 1.15;
        var s = double.IsNegative(dx) ? -1.0 : 1.0;
        var c = new CurveSet();
        c.Set("PosX", [(0, 0), (0.1, 0), (0.98, dx), (d, dx)]);
        c.Set("PosY", [(0, 0), (0.12, 0), .. Keys.Ballistic(0.15, 0.34, 0.08), .. Keys.Ballistic(0.57, 0.34, 0.08), (d, 0)]);
        c.Set("Squash", [(0, 0), (0.12, -0.25), (0.18, 0.15), (0.5, -0.22), (0.6, 0.15), (0.92, -0.22), (d, 0)]);
        c.Set("Lean", [(0, 0), (0.15, 0.4 * s), (0.8, 0.25 * s), (d, 0)]);
        c.Set("Hip", [(0, 0), (0.3, -0.3 * s), (0.72, 0.3 * s), (d, 0)]);
        c.Set("SkirtFlare", [(0, 0), (0.3, 0.3), (0.9, 0.2), (d, 0)]);
        c.Set("FootLift", [(0, 0), (0.22, 0.5), (0.48, 0), (0.64, 0.5), (0.9, 0), (d, 0)]);
        c.Set("AngleX", [(0, 0), (0.15, 0.35 * s), (0.8, 0.35 * s), (d, 0)]);
        c.Set($"Arm.{output.Key()}", [(0, 0), (0.3, 0.3), (0.72, -0.05), (d, 0)]);
        c.Set($"Arm.{book.Key()}", [(0, 0), (0.3, -0.05), (0.72, 0.2), (d, 0)]);
        return new MotionSpec("step", d, c, travel: dx);
    }

    private static List<(double T, double V)> Scaled(IEnumerable<(double T, double V)> hold, double k) => hold.Select(h => (h.T, h.V * k)).ToList();

    public static MotionSpec Curtsy(CharacterSide output, CharacterSide book)
    {
        const double d = 2.0;
        (double, double)[] hold = [(0, 0), (0.5, 1.0), (1.3, 1.0), (d, 0)];
        var c = new CurveSet();
        c.Set("Squash", Scaled(hold, -0.17));
        c.Set("PosY", Scaled(hold, -0.04));
        c.Set("AngleY", Scaled(hold, -0.6));
        c.Set("AngleZ", Scaled(hold, 0.25));
        c.Set("SkirtFlare", Scaled(hold, 0.5));
        c.Set("Hip", Scaled(hold, -0.2));
        c.Set($"Arm.{output.Key()}", Scaled(hold, 0.3));
        c.Set($"Arm.{book.Key()}", Scaled(hold, 0.1));
        Both(c, "Shoulder", Scaled(hold, -0.3));
        return new MotionSpec("curtsy", d, c, fade: 0.2, expression: "smile");
    }

    public static MotionSpec Tiptoe(CharacterSide output, CharacterSide book)
    {
        const double d = 2.0;
        var c = new CurveSet();
        c.Set("FootPoint", [(0, 0), (0.25, 1.0), (1.7, 1.0), (d, 0)]);
        c.Set("FootLift", [(0, 0), (0.25, 0.3), (1.7, 0.3), (d, 0)]);
        c.Set("PosY", [(0, 0), (0.2, 0.04), .. Keys.Oscillate(0.25, 1.7, 0.5, 0.025, baseValue: 0.045), (d, 0)]);
        c.Set("Squash", [(0, 0), (0.25, 0.1), (1.7, 0.08), (d, 0)]);
        c.Set("Hip", [(0, 0), .. Keys.Oscillate(0.2, 1.8, 1.0, 0.18), (d, 0)]);
        c.Set($"Arm.{output.Key()}", [(0, 0), (0.3, 0.25), (1.6, 0.25), (d, 0)]);
        c.Set($"Arm.{book.Key()}", [(0, 0), (0.3, 0.15), (1.6, 0.15), (d, 0)]);
        c.Set("AngleY", [(0, 0), (0.3, 0.2), (1.6, 0.2), (d, 0)]);
        Both(c, "Shoulder", [(0, 0), (0.3, 0.3), (1.6, 0.3), (d, 0)]);
        return new MotionSpec("tiptoe", d, c, expression: "happy");
    }

    public static MotionSpec Foottap(CharacterSide output, CharacterSide book, double beat = 0.5)
    {
        var taps = new List<(double T, double V)> { (0, 0) };
        for (var k = 0; k < 4; k++)
        {
            var t = 0.3 + k * beat;
            taps.AddRange([(t, 0), (t + 0.12, 0.45), (t + 0.26, 0)]);
        }

        var d = 0.3 + 4 * beat + 0.3;
        var c = new CurveSet();
        c.Set("FootLift", [.. taps, (d, 0)]);
        c.Set("FootPoint", [.. taps.Select(x => (x.T, 0.6 * x.V)), (d, 0)]);
        c.Set("AngleY", Keys.Oscillate(0, d, beat * 2, 0.08));
        c.Set("Hip", [(0, 0), (0.3, 0.25), (d - 0.3, 0.25), (d, 0)]);
        c.Set("BodyAngleZ", [(0, 0), (0.3, -0.15), (d - 0.3, -0.15), (d, 0)]);
        c.Set("AngleZ", Keys.Oscillate(0, d, beat * 2, 0.12));
        return new MotionSpec("foottap", d, c);
    }

    public static MotionSpec Sway(CharacterSide output, CharacterSide book, double period = 1.6)
    {
        var d = 2 * period;
        List<(double T, double V)> flare = [(0, 0), .. Keys.Oscillate(0.4, d - 0.3, period / 2, 0.15, baseValue: 0.3), (d, 0)];
        var bounce = new List<(double T, double V)> { (0, 0) };
        for (var k = 0; k < 4; k++)
        {
            bounce.AddRange(Keys.Ballistic(0.05 + k * period / 2, period / 2 - 0.1, 0.035));
        }

        var c = new CurveSet();
        c.Set("Hip", Keys.Oscillate(0, d, period, 0.6));
        c.Set("BodyAngleZ", Keys.Oscillate(0, d, period, 0.35, phase: Math.PI));
        c.Set("AngleZ", Keys.Oscillate(0, d, period, 0.35));
        c.Set($"Arm.{output.Key()}", Swing(d, period, 0.35, baseValue: 0.1));
        c.Set($"Arm.{book.Key()}", Keys.Oscillate(0, d, period, 0.2, phase: Math.PI));
        c.Set($"Shoulder.{output.Key()}", Keys.Oscillate(0, d, period, 0.4));
        c.Set($"Shoulder.{book.Key()}", Keys.Oscillate(0, d, period, 0.4, phase: Math.PI));
        c.Set("PosY", [.. bounce, (d, 0)]);
        c.Set("SkirtFlare", flare);
        return new MotionSpec("sway", d, c, fade: 0.2, expression: "smile", talk: true);
    }

    public static MotionSpec Run(CharacterSide output, CharacterSide book, double step = 0.32, int steps = 7)
    {
        var d = 0.1 + steps * step + 0.2;
        var pos = new List<(double T, double V)> { (0, 0) };
        var squash = new List<(double T, double V)> { (0, 0) };
        var foot = new List<(double T, double V)> { (0, 0) };
        for (var k = 0; k < steps; k++)
        {
            var t = 0.1 + k * step;
            pos.AddRange(Keys.Ballistic(t, step * 0.8, 0.06));
            squash.AddRange([(t, -0.25), (t + step * 0.4, 0.12)]);
            if (k % 2 == 0)
            {
                foot.AddRange([(t + 0.04, 0), (t + 0.14, 0.6), (t + step - 0.02, 0)]);
            }
        }

        pos.Add((d, 0));
        squash.Add((d, 0));
        foot.Add((d, 0));
        var o = output.Key();
        var b = book.Key();
        var c = new CurveSet();
        c.Set("PosY", pos);
        c.Set("Squash", squash);
        c.Set("FootLift", foot);
        c.Set($"Arm.{o}", Swing(d, 2 * step, 0.4, baseValue: 0.15));
        c.Set($"Forearm.{o}", Swing(d, 2 * step, 0.3, baseValue: -0.45, phase: Math.PI / 2));
        c.Set($"Arm.{b}", Swing(d, 2 * step, 0.3, baseValue: 0.15, phase: Math.PI));
        c.Set($"Shoulder.{o}", Keys.Oscillate(0, d, 2 * step, 0.3));
        c.Set($"Shoulder.{b}", Keys.Oscillate(0, d, 2 * step, 0.3, phase: Math.PI));
        c.Set("Hip", Keys.Oscillate(0, d, 2 * step, 0.2));
        c.Set("Lean", [(0, 0), (0.3, 0.1), (d - 0.3, 0.1), (d, 0)]);
        c.Set("AngleY", [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)]);
        c.Set("SkirtFlare", [(0, 0), (0.3, 0.4), (d - 0.3, 0.4), (d, 0)]);
        return new MotionSpec("run", d, c, fade: 0.15, expression: "determined");
    }

    public static MotionSpec Stretch(CharacterSide output, CharacterSide book)
    {
        const double d = 2.8;
        var c = new CurveSet();
        c.Set("Squash", [(0, 0), (0.9, 0.2), (1.9, 0.18), (d, 0)]);
        c.Set("AngleY", [(0, 0), (0.9, 0.45), (1.9, 0.4), (d, 0)]);
        c.Set("Lean", [(0, 0), (1.0, 0.2), (1.5, -0.2), (2.0, 0), (d, 0)]);
        c.Set("BodyAngleZ", [(0, 0), (1.0, 0.3), (1.5, -0.3), (2.0, 0), (d, 0)]);
        c.Set("FootPoint", [(0, 0), (0.9, 0.6), (1.9, 0.6), (d, 0)]);
        c.Set("PosY", [(0, 0), (0.9, 0.03), (1.9, 0.03), (d, 0)]);
        c.Set($"Arm.{output.Key()}", [(0, 0), (0.9, 0.35), (1.9, 0.35), (d, 0)]);
        c.Set($"Arm.{book.Key()}", [(0, 0), (0.9, 0.2), (1.9, 0.2), (d, 0)]);
        Both(c, "Shoulder", [(0, 0), (0.9, 0.9), (1.9, 0.85), (d, 0)]);
        return new MotionSpec("stretch", d, c, fade: 0.25, expression: "sleepy");
    }

    public static MotionSpec Bow(CharacterSide output, CharacterSide book)
    {
        const double d = 1.8;
        var c = new CurveSet();
        c.Set("AngleY", [(0, 0), (0.5, -0.85), (1.2, -0.85), (d, 0)]);
        c.Set("Squash", [(0, 0), (0.5, -0.15), (1.2, -0.15), (d, 0)]);
        Both(c, "Shoulder", [(0, 0), (0.5, -0.2), (1.2, -0.2), (d, 0)]);
        return new MotionSpec("bow", d, c, fade: 0.2, expression: "smile");
    }

    public static MotionSpec Peek(CharacterSide output, CharacterSide book, double orb)
    {
        const double d = 2.4;
        (double, double)[] hold = [(0, 0), (0.45, 1.0), (1.9, 1.0), (d, 0)];
        var c = new CurveSet();
        c.Set("Lean", Scaled(hold, 0.45 * orb));
        c.Set("AngleX", Scaled(hold, 0.6 * orb));
        c.Set("EyeBallX", Scaled(hold, 0.9 * orb));
        c.Set("Hip", Scaled(hold, -0.3 * orb));
        c.Set("AngleZ", Scaled(hold, 0.25 * orb));
        c.Set("BodyAngleZ", Scaled(hold, 0.2 * orb));
        c.Set("PosY", [(0, 0), (0.3, 0), (0.42, 0.04), (0.55, 0), (d, 0)]);
        return new MotionSpec("peek", d, c, fade: 0.2, expression: "surprised");
    }
}
