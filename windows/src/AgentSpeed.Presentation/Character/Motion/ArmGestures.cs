namespace AgentSpeed.Presentation.Character;

/// <summary>
/// Arm gestures that swap in a pose variant, and combos of one variant per arm (motions/gestures.py): the old arm turns
/// to meet the new one, they cross-fade, the new arm settles into its pose, holds with a flourish, and the swap runs
/// backwards. The shoulder rises with the arm, the weight shifts to the other leg and the head follows the hand.
/// </summary>
internal static class ArmGestures
{
    public sealed record ArmSpec(double Hold, string Flourish, string? Expression, bool Tiptoe);

    public sealed record ComboSpec(string Book, string Free, double Hold, string? Expression);

    public static readonly IReadOnlyDictionary<string, ArmSpec> Arms = new Dictionary<string, ArmSpec>(StringComparer.Ordinal)
    {
        ["wave"] = new(1.6, "wave", "smile", true),
        ["peace"] = new(1.3, "bob", "happy", true),
        ["fist"] = new(1.2, "pump", "determined", false),
        ["idea"] = new(1.4, "pop", "surprised", true),
        ["chin"] = new(2.4, "think", null, false),
        ["hairtuck"] = new(1.6, "slow", "smile", true),
        ["present"] = new(1.8, "offer", "talk", false),
        ["hip"] = new(1.8, "lean", "determined", false),
        ["hug"] = new(2.4, "sway", "smile", true),
        ["raise"] = new(1.4, "pump", "happy", false),
        ["open"] = new(3.4, "read", null, false),
        ["thumbsup"] = new(1.4, "bob", "smile", false),
        ["shh"] = new(1.6, "slow", "smile", false),
        ["cheek"] = new(1.8, "slow", "shy", false),
        ["salute"] = new(1.2, "bob", "smile", false),
        ["reach"] = new(1.6, "slow", "sleepy", true),
    };

    public static readonly IReadOnlyDictionary<string, ComboSpec> Combos = new Dictionary<string, ComboSpec>(StringComparer.Ordinal)
    {
        ["cheer"] = new("raise", "fist", 1.4, "happy"),
        ["hooray"] = new("raise", "wave", 1.6, "happy"),
        ["eureka"] = new("open", "idea", 1.8, "surprised"),
        ["ponder"] = new("open", "chin", 2.4, null),
        ["cuddle"] = new("hug", "peace", 1.6, "happy"),
        ["proud"] = new("hug", "hip", 1.8, "determined"),
    };

    private static double[] Timeline(double hold)
    {
        var t1 = 0.22;
        var t2 = t1 + 0.1;
        var t3 = t2 + 0.32;
        var t4 = t3 + hold;
        var t5 = t4 + 0.24;
        var t6 = t5 + 0.1;
        var t7 = t6 + 0.36;
        return [t1, t2, t3, t4, t5, t6, t7];
    }

    /// <summary>The gesture of an arm variant (<c>kind: arm</c>); null when the variant lacks the parameters it drives.</summary>
    public static MotionSpec? ArmGesture(RigDocument.Variant meta, double hold, string flourish, string? expression, bool tiptoe = false)
    {
        if (meta.Arm is not { } arm || meta.Forearm is not { } fore || meta.Side is not { } side)
        {
            return null;
        }

        var sign = side == "right" ? 1.0 : -1.0;
        var t = Timeline(hold);
        var (t1, t2, t3, t4, t5, t6, t7) = (t[0], t[1], t[2], t[3], t[4], t[5], t[6]);
        var c = new CurveSet();
        c.Set(meta.Param, [(0, 0), (t1, 0), (t2, 1), (t5, 1), (t6, 0), (t7, 0)]);
        c.Set($"Arm.{side}", [(0, 0), (t7, 0)]);
        c.Set($"Forearm.{side}", [(0, 0), (t7, 0)]);
        if (meta.Lead is { } leads)
        {
            foreach (var (pid, lead) in leads.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var dip = Math.Abs(lead) > 0.2 ? -0.12 * (double.IsNegative(lead) ? -1.0 : 1.0) : 0.0;
                c.Set(pid, [(0, 0), (0.08, dip), (t1 - 0.04, lead), (t6 + 0.02, lead), (t7, 0)]);
            }
        }

        foreach (var pid in new[] { arm, fore })
        {
            var a = meta.Approach is { } approach && approach.TryGetValue(pid, out var v) ? v : 0;
            c.Set(pid, [(0, a), (t1, a), (t3, 0), (t4, 0), (t5 - 0.04, a), (t7, a)]);
        }

        void During(string pid, IEnumerable<(double T, double V)> keys)
        {
            if (c[pid] is not { Length: >= 6 } old)
            {
                return;
            }

            c[pid] = [.. old.Take(3), .. keys.Select(k => new CurveKey(k.T, k.V)), .. old.Skip(old.Length - 3)];
        }

        var lift = meta.Front == true ? 0.6 : 0.25;
        var body = new CurveSet();
        body.Set($"Shoulder.{side}", [(0, 0), (t1, 0.3 * lift), (t3, lift), (t4, 0.9 * lift), (t6, 0.3 * lift), (t7, 0)]);
        body.Set("Hip", [(0, 0), (t3, -0.3 * sign), (t4, -0.3 * sign), (t7, 0)]);
        body.Set("BodyAngleZ", [(0, 0), (t3, 0.2 * sign), (t4, 0.2 * sign), (t7, 0)]);
        if (tiptoe)
        {
            body.Set("FootPoint", [(0, 0), (t2, 0), (t3, 0.8), (t4, 0.8), (t5, 0), (t7, 0)]);
            body.Set("FootLift", [(0, 0), (t2, 0), (t3, 0.25), (t4, 0.25), (t5, 0), (t7, 0)]);
            body.Set("PosY", [(0, 0), (t2, 0), (t3, 0.025), (t4, 0.025), (t5, 0), (t7, 0)]);
        }

        var head = new CurveSet();
        switch (flourish)
        {
            case "wave":
                During(fore, Keys.Oscillate(t3, t4, 0.42, 0.45));
                head.Set("AngleZ", [(0, 0), (t3, 0.25), (t4, 0.25), (t7, 0)]);
                head.Set("AngleX", [(0, 0), (t3, -0.3), (t4, -0.3), (t7, 0)]);
                break;
            case "bob":
                During(arm, Keys.Oscillate(t3, t4, 0.5, 0.06));
                head.Set("AngleZ", [(0, 0), (t3, 0.45), (t4, 0.45), (t7, 0)]);
                break;
            case "pump":
                During(arm, Keys.Oscillate(t3, t4, 0.34, 0.12));
                head.Set("AngleY", [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)]);
                head.Set("BodyAngleZ", [(0, 0), (t3, -0.4 * sign), (t4, -0.4 * sign), (t7, 0)]);
                body.Set("PosY", [(0, 0), (t2, 0), .. Keys.Oscillate(t3, t4, 0.68, 0.03, baseValue: 0.03), (t5, 0), (t7, 0)]);
                break;
            case "pop":
                During(fore, [(t3 + 0.12, -0.2), (t3 + 0.3, 0)]);
                head.Set("AngleY", [(0, 0), (t3, 0.5), (t4, 0.45), (t7, 0)]);
                head.Set("EyeBallY", [(0, 0), (t3, -0.7), (t4, -0.7), (t7, 0)]);
                break;
            case "think":
                head.Set("AngleZ", [(0, 0), (t3, 0.35), (t4, 0.4), (t7, 0)]);
                head.Set("AngleY", [(0, 0), (t3, 0.25), (t4, 0.2), (t7, 0)]);
                head.Set("EyeBallY", [(0, 0), (t3, -0.8), (t4, -0.8), (t7, 0)]);
                head.Set("EyeBallX", [(0, 0), (t3, 0.5), (t4, 0.6), (t7, 0)]);
                break;
            case "slow":
                During(fore, [(t3 + hold * 0.5, 0.15)]);
                head.Set("AngleZ", [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)]);
                head.Set("EyeBallX", [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)]);
                break;
            case "offer":
                During(arm, [(t3 + hold * 0.5, -0.05)]);
                head.Set("AngleX", [(0, 0), (t3, 0.45), (t4, 0.45), (t7, 0)]);
                head.Set("EyeBallX", [(0, 0), (t3, 0.7), (t4, 0.7), (t7, 0)]);
                body.Set("Lean", [(0, 0), (t3, 0.25 * sign), (t4, 0.25 * sign), (t7, 0)]);
                break;
            case "lean":
                head.Set("BodyAngleZ", [(0, 0), (t3, 0.55 * sign), (t4, 0.55 * sign), (t7, 0)]);
                head.Set("AngleZ", [(0, 0), (t3, -0.3 * sign), (t4, -0.3 * sign), (t7, 0)]);
                body.Set("Hip", [(0, 0), (t3, 0.55 * sign), (t4, 0.55 * sign), (t7, 0)]);
                break;
            case "sway":
                head.Set("AngleZ", [(0, 0), (t3, -0.3), (t3 + hold / 2, 0.2), (t4, -0.3), (t7, 0)]);
                head.Set("BodyAngleZ", Keys.Oscillate(0, t7, 1.6, 0.25));
                body.Set("Hip", Keys.Oscillate(0, t7, 1.6, 0.3, phase: Math.PI));
                break;
            case "read":
                head.Set("AngleY", [(0, 0), (t3, -0.6), (t4, -0.6), (t7, 0)]);
                head.Set("EyeBallY", [(0, 0), (t3, 0.6), (t4, 0.6), (t7, 0)]);
                head.Set("EyeBallX", [(0, 0), (t3, -0.6), .. Keys.Oscillate(t3, t4, 1.1, 0.5, baseValue: 0), (t7, 0)]);
                break;
        }

        if (flourish is not ("wave" or "pop" or "slow"))
        {
            During(fore, Keys.Oscillate(t3, t4, 1.3, 0.04));
        }

        for (var i = 0; i < body.Names.Count; i++)
        {
            c[body.Names[i]] = body.Keys[i];
        }

        for (var i = 0; i < head.Names.Count; i++)
        {
            c[head.Names[i]] = head.Keys[i];
        }

        return new MotionSpec(meta.Id, t7, c, expression: expression);
    }

    /// <summary>Two arm gestures at once: the object arm's, then 0.1 s later the free arm's, which carries the expression.</summary>
    public static MotionSpec? Combo(string name, RigDocument.Variant book, RigDocument.Variant free, double hold, string? expression)
    {
        if (!Arms.TryGetValue(book.Id, out var a) || !Arms.TryGetValue(free.Id, out var b)
            || ArmGesture(book, hold, a.Flourish, null) is not { } first || ArmGesture(free, hold, b.Flourish, expression) is not { } second)
        {
            return null;
        }

        return Keys.Merge(name, [first, second], [0, 0.1], expression);
    }
}
