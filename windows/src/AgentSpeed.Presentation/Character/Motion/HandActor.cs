namespace AgentSpeed.Presentation.Character;

/// <summary>
/// Hand acting (motions/hands.py): a slow ripple across the fingers, a drifting wrist and spread, fidgets now and then
/// (taps, regrips and lifts on the object hand; flexes, fans, counting and drumming on the free hand), and the wrist
/// trailing the arm.
/// </summary>
internal sealed class HandActor
{
    /// <summary>Local slots of hand motions: fingers 0...4, then the wrist and the spread.</summary>
    public const int WristSlot = 5;
    public const int SpreadSlot = 6;

    /// <summary>Every hand set by name (motions/hands.py, make).</summary>
    public static readonly string[] Sets =
        ["tap", "regrip", "lift", "flex", "fan", "count", "drum", "flute", "fanflutter", "abacus", "sleeveflop", "ripple", "grip"];

    private readonly double _phase;
    private bool _modeAction;
    private double _t;
    private double _next;
    private double? _previousArm;
    private double _trail;

    public HandActor(CharacterSide side, bool holds, double phase, ref Rng rng)
    {
        Side = side;
        Holds = holds;
        _phase = phase;
        _next = rng.Uniform(1.0, 3.0);
    }

    public CharacterSide Side { get; }

    public bool Holds { get; set; }

    /// <summary>The character's fidget sets for the free hand (empty: flex, fan, count, drum).</summary>
    public IReadOnlyList<string> FreeSets { get; set; } = [];

    /// <summary>The pose's hand set, played back to back (<see cref="SetMode"/>).</summary>
    public string? Mode { get; private set; }

    public CharacterMotion? Action { get; private set; }

    /// <summary>Finger 0...4, spread and wrist offsets of the last update.</summary>
    public double[] Fingers { get; } = new double[5];

    public double Spread { get; private set; }

    public double Wrist { get; private set; }

    private static CharacterMotion Bind(MotionSpec spec) => spec.Bind(name => name switch
    {
        "W" => WristSlot,
        "S" => SpreadSlot,
        _ => int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : null,
    });

    private static MotionSpec Stagger(string name, double d, (double T, double V)[] perFinger, int[] order, double gap)
    {
        var c = new CurveSet();
        for (var k = 0; k < order.Length; k++)
        {
            var dt = k * gap;
            c.Set($"F{order[k]}", [(0, 0), .. perFinger.Select(p => (p.T + dt, p.V)), (d + dt + 0.2, 0)]);
        }

        return new MotionSpec(name, d + gap * (order.Length - 1) + 0.25, c);
    }

    private static MotionSpec Tap(double beat)
    {
        (double T, double V)[] one = [(0.08, -0.25), (0.18, 0.3), (0.3, 0.0)];
        (double T, double V)[] keys = [.. one, .. one.Select(o => (o.T + beat, o.V))];
        return Stagger("tap", 0.3 + beat, keys, [1, 2], 0.12);
    }

    private static MotionSpec Regrip()
    {
        var m = Stagger("regrip", 0.7, [(0.2, 0.32), (0.45, 0.28), (0.7, 0.0)], [4, 3, 2, 1, 0], 0.05);
        m.Curves.Set("W", [(0, 0), (0.25, -0.12), (0.8, 0)]);
        return m;
    }

    private static MotionSpec Lift() => Stagger("lift", 0.6, [(0.15, -0.22), (0.35, -0.22), (0.6, 0.0)], [1, 2, 3, 4], 0.07);

    private static MotionSpec Flex()
    {
        var m = Stagger("flex", 1.2, [(0.25, 0.4), (0.55, 0.35), (0.85, -0.15), (1.2, 0.0)], [4, 3, 2, 1, 0], 0.06);
        m.Curves.Set("S", [(0, 0), (0.6, 0), (0.95, 0.45), (1.5, 0)]);
        m.Curves.Set("W", [(0, 0), (0.3, 0.2), (0.9, -0.15), (1.5, 0)]);
        return m;
    }

    private static MotionSpec Fan()
    {
        var m = Stagger("fan", 1.0, [(0.3, -0.18), (0.7, -0.15), (1.0, 0.0)], [0, 1, 2, 3, 4], 0.04);
        m.Curves.Set("S", [(0, 0), (0.35, 0.6), (0.75, 0.5), (1.2, 0)]);
        m.Curves.Set("W", [(0, 0), (0.35, -0.2), (1.2, 0)]);
        return m;
    }

    private static MotionSpec Count(double beat)
    {
        var c = new CurveSet();
        for (var i = 0; i < 5; i++)
        {
            var t = 0.1 + i * beat;
            c.Set($"F{i}", [(0, 0), (t, 0), (t + 0.12, 0.42), (5 * beat + 0.2, 0.42), (5 * beat + 0.55, 0.0)]);
        }

        var w = Enumerable.Range(0, 5).Select(i => (0.1 + i * beat + 0.12, 0.06 * (i % 2 == 0 ? 1.0 : -1.0)));
        c.Set("W", [(0, 0), .. w, (5 * beat + 0.6, 0)]);
        return new MotionSpec("count", 5 * beat + 0.6, c);
    }

    private static MotionSpec Drum(double beat, int bars = 2)
    {
        var keys = new Dictionary<int, List<(double T, double V)>> { [1] = [(0, 0)], [2] = [(0, 0)], [3] = [(0, 0)] };
        var t = 0.05;
        for (var n = 0; n < 4 * bars; n++)
        {
            foreach (var (i, dt) in new[] { (3, 0.0), (2, 0.06), (1, 0.12) })
            {
                keys[i].AddRange([(t + dt, -0.15), (t + dt + 0.07, 0.3), (t + dt + 0.16, 0.0)]);
            }

            t += beat;
        }

        var c = new CurveSet();
        foreach (var i in new[] { 1, 2, 3 })
        {
            c.Set($"F{i}", [.. keys[i], (t + 0.2, 0)]);
        }

        return new MotionSpec("drum", t + 0.2, c);
    }

    /// <summary>Fingering a transverse flute: index, middle and ring fingers lift and press through a fixed tune.</summary>
    private static MotionSpec Flute(double beat)
    {
        int[][] pattern = [[1], [1, 2], [2, 3], [1, 3], [2], [1, 2, 3], [3], [1, 2]];
        var step = 0.5 * beat;
        var keys = new Dictionary<int, List<(double T, double V)>> { [1] = [(0, 0)], [2] = [(0, 0)], [3] = [(0, 0)] };
        for (var k = 0; k < pattern.Length; k++)
        {
            var t = 0.05 + k * step;
            foreach (var i in new[] { 1, 2, 3 })
            {
                var v = pattern[k].Contains(i) ? -0.3 : 0.12;
                keys[i].AddRange([(t + 0.06, v), (t + step - 0.04, v)]);
            }
        }

        var end = 0.05 + pattern.Length * step + 0.1;
        var c = new CurveSet();
        foreach (var i in new[] { 1, 2, 3 })
        {
            c.Set($"F{i}", [.. keys[i], (end, 0)]);
        }

        return new MotionSpec("flute", end, c);
    }

    private static MotionSpec Fanflutter(double beat)
    {
        const double d = 1.9;
        var c = new CurveSet();
        c.Set("W", [(0, 0), .. Keys.Oscillate(0.1, d - 0.1, 0.6, 0.22), (d, 0)]);
        c.Set("S", [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)]);
        return new MotionSpec("fanflutter", d + 0.5 * beat, c);
    }

    private static MotionSpec Abacus(double beat)
    {
        var step = Math.Max(0.5 * beat, 0.24);
        var f1 = new List<(double T, double V)> { (0, 0) };
        var f0 = new List<(double T, double V)> { (0, 0) };
        for (var k = 0; k < 6; k++)
        {
            var t = 0.05 + k * step;
            f1.AddRange([(t, 0.0), (t + 0.05, 0.5), (t + 0.11, -0.12), (t + 0.2, 0.0)]);
            if (k % 2 == 0)
            {
                f0.AddRange([(t, 0.0), (t + 0.06, 0.25), (t + 0.16, 0.0)]);
            }
        }

        var end = 0.05 + 6 * step + 0.1;
        var c = new CurveSet();
        c.Set("F1", [.. f1, (end, 0)]);
        c.Set("F0", [.. f0, (end, 0)]);
        c.Set("W", [(0, 0), .. Keys.Oscillate(0.05, end - 0.05, 2 * step, 0.05), (end, 0)]);
        return new MotionSpec("abacus", end, c);
    }

    private static MotionSpec Sleeveflop()
    {
        const double d = 2.4;
        (double T, double V)[] droop = [(0, 0), (0.4, 0.2), (d - 0.4, 0.2), (d, 0)];
        var c = new CurveSet();
        c.Set("W", [(0, 0), .. Keys.Oscillate(0.1, d - 0.1, 1.6, 0.3), (d, 0)]);
        foreach (var i in new[] { 1, 2, 3, 4 })
        {
            c.Set($"F{i}", droop);
        }

        return new MotionSpec("sleeveflop", d, c);
    }

    private static MotionSpec Ripple()
    {
        var c = new CurveSet();
        c.Set("S", [(0, 0), (2.0, 0)]);
        return new MotionSpec("ripple", 2.0, c);
    }

    /// <summary>Both hands holding one object up: the fingers tighten a little on every other beat, the wrists still.</summary>
    private static MotionSpec Grip(double beat)
    {
        var d = 2 * beat;
        var c = new CurveSet();
        foreach (var i in new[] { 1, 2, 3, 4 })
        {
            c.Set($"F{i}", [(0, 0), (0.35 * d, 0.1), (0.6 * d, 0.1), (d, 0)]);
        }

        return new MotionSpec("grip", d, c);
    }

    private static MotionSpec Make(string name, double beat) => name switch
    {
        "tap" => Tap(beat),
        "regrip" => Regrip(),
        "lift" => Lift(),
        "flex" => Flex(),
        "fan" => Fan(),
        "count" => Count(beat),
        "drum" => Drum(beat),
        "flute" => Flute(beat),
        "fanflutter" => Fanflutter(beat),
        "abacus" => Abacus(beat),
        "sleeveflop" => Sleeveflop(),
        "grip" => Grip(beat),
        _ => Ripple(),
    };

    /// <summary>Which hands a pose's hand set plays on: both, the hand holding the object, or the free one.</summary>
    private static string ModeSides(string name) => name switch
    {
        "flute" or "sleeveflop" or "ripple" or "grip" => "both",
        "fanflutter" or "tap" or "regrip" or "lift" => "holder",
        _ => "free",
    };

    private bool ModeOn
    {
        get
        {
            if (Mode is not { } mode)
            {
                return false;
            }

            var sides = ModeSides(mode);
            return sides == "both" || (sides == "holder") == Holds;
        }
    }

    /// <summary>The pose's hand set: played back to back on the hands it belongs to, from the next action on.</summary>
    public void SetMode(string? mode)
    {
        Mode = mode;
        if (ModeOn && Action is null)
        {
            _next = Math.Min(_next, 0);
        }
    }

    private CharacterMotion Choose(SpeedBand band, double beat, ref Rng rng)
    {
        if (ModeOn && Mode is { } mode)
        {
            return Bind(Make(mode, beat));
        }

        if (Holds)
        {
            return rng.Index(3) switch
            {
                0 => Bind(Tap(beat)),
                1 => Bind(Regrip()),
                _ => Bind(Lift()),
            };
        }

        if (FreeSets.Count > 0)
        {
            var options = FreeSets.Select(s => Make(s, beat)).ToArray();
            return Bind(options[rng.Index(options.Length)]);
        }

        var busy = band is SpeedBand.Boost or SpeedBand.Overdrive or SpeedBand.Redline;
        return rng.Index(busy ? 4 : 2) switch
        {
            0 => Bind(Flex()),
            1 => Bind(Fan()),
            2 => Bind(Count(beat)),
            _ => Bind(Drum(beat)),
        };
    }

    private static double Busyness(SpeedBand band) => band switch
    {
        SpeedBand.Cruise => 0.8,
        SpeedBand.Boost => 1.1,
        SpeedBand.Overdrive => 1.4,
        SpeedBand.Redline => 1.7,
        _ => 0.5,
    };

    /// <summary>Advances one frame; the offsets land in <see cref="Fingers"/>, <see cref="Spread"/> and <see cref="Wrist"/>.</summary>
    public void Update(double dt, double time, SpeedBand band, double beat, double arm, ref Rng rng)
    {
        var rest = Holds ? 0.06 : 0.14;
        var amp = Holds ? 0.04 : 0.07;
        for (var i = 0; i < 5; i++)
        {
            Fingers[i] = rest + amp * Math.Sin(2 * Math.PI * time / 3.4 - 0.55 * i + _phase);
        }

        Spread = 0.08 * Math.Sin(2 * Math.PI * time / 4.6 + 1.1 + _phase);
        var sway = Mode is { } m && MotionProfile.TwoHanded.Contains(m) ? 0.0 : (Holds ? 0.03 : 0.09);
        Wrist = sway * Math.Sin(2 * Math.PI * time / 5.3 + _phase);

        if (_previousArm is { } previous)
        {
            var vel = (arm - previous) / Math.Max(dt, 1e-6);
            _trail += (Math.Max(-1.0, Math.Min(1.0, -0.35 * vel)) - _trail) * Math.Min(1.0, dt * 10);
        }

        _previousArm = arm;
        Wrist += _trail * (Holds ? 0.25 : 0.6);
        for (var i = 0; i < 5; i++)
        {
            Fingers[i] -= Math.Abs(_trail) * 0.25;
        }

        _next -= dt * Busyness(band);
        if (Action is null && _next <= 0)
        {
            _modeAction = ModeOn;
            Action = Choose(band, beat, ref rng);
            _t = 0;
        }

        if (Action is { } motion)
        {
            _t += dt;
            var local = motion.LocalTime(_t);
            var wristScale = _modeAction || !Holds ? 1.0 : 0.3;
            foreach (var entry in motion.Entries)
            {
                var v = entry.Curve.Evaluate(local);
                switch (entry.Slot)
                {
                    case WristSlot:
                        Wrist += v * wristScale;
                        break;
                    case SpreadSlot:
                        Spread += v;
                        break;
                    default:
                        Fingers[entry.Slot] += v;
                        break;
                }
            }

            if (_t >= motion.Duration)
            {
                Action = null;
                _next = ModeOn ? 0 : rng.Uniform(1.6, 4.2);
            }
        }
    }
}
