namespace AgentSpeed.Presentation.Character;

/// <summary>
/// Named key lists in insertion order, like the reference's curve dictionaries (assigning an existing name replaces its
/// keys in place).
/// </summary>
public sealed class CurveSet
{
    private readonly List<string> _names = [];
    private readonly List<CurveKey[]> _keys = [];

    public IReadOnlyList<string> Names => _names;

    public IReadOnlyList<CurveKey[]> Keys => _keys;

    public CurveKey[]? this[string name]
    {
        get
        {
            var i = _names.IndexOf(name);
            return i >= 0 ? _keys[i] : null;
        }

        set
        {
            var i = _names.IndexOf(name);
            if (i >= 0)
            {
                if (value is not null)
                {
                    _keys[i] = value;
                }
                else
                {
                    _names.RemoveAt(i);
                    _keys.RemoveAt(i);
                }
            }
            else if (value is not null)
            {
                _names.Add(name);
                _keys.Add(value);
            }
        }
    }

    internal void Set(string name, IEnumerable<(double T, double V)> values) => this[name] = values.Select(k => new CurveKey(k.T, k.V)).ToArray();
}

/// <summary>A timed event of a motion (a puff of petals at a take-off).</summary>
public readonly record struct MotionEvent(double Time, string Name);

/// <summary>A motion before its curves are bound to parameter slots.</summary>
public sealed class MotionSpec
{
    public MotionSpec(string name, double duration, CurveSet curves, bool loop = false, double fade = 0.12, string? expression = null,
        bool talk = false, IEnumerable<MotionEvent>? events = null, double travel = 0)
    {
        Name = name;
        Duration = duration;
        Curves = curves;
        Loop = loop;
        Fade = fade;
        Expression = expression;
        Talk = talk;
        Events = (events ?? []).OrderBy(e => e.Time).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
        Travel = travel;
    }

    public string Name { get; }

    public double Duration { get; }

    public CurveSet Curves { get; }

    public bool Loop { get; }

    public double Fade { get; }

    public string? Expression { get; }

    public bool Talk { get; }

    public MotionEvent[] Events { get; }

    public double Travel { get; }

    /// <summary>The motion with its curves bound to slots; names <paramref name="slot"/> returns null for are dropped.</summary>
    public CharacterMotion Bind(Func<string, int?> slot)
    {
        var entries = new List<CharacterMotion.Entry>();
        for (var i = 0; i < Curves.Names.Count; i++)
        {
            if (slot(Curves.Names[i]) is not { } s)
            {
                continue;
            }

            entries.Add(new CharacterMotion.Entry(s, Curves.Names[i], new Curve(Curves.Keys[i], Loop)));
        }

        return new CharacterMotion(Name, Duration, Loop, Fade, Expression, Talk, Events, Travel, entries.ToArray());
    }
}

/// <summary>
/// Curves over time, bound to parameter slots. One-shot motions may carry an expression, lip-flap (<c>talk</c>), timed
/// events and a sideways <c>travel</c> that stays after they end.
/// </summary>
public sealed record CharacterMotion(string Name, double Duration, bool Loop, double Fade, string? Expression, bool Talk, MotionEvent[] Events,
    double Travel, CharacterMotion.Entry[] Entries)
{
    public sealed record Entry(int Slot, string Name, Curve Curve);

    /// <summary>The time a sample at <paramref name="t"/> reads: wrapped when looping, held at the end otherwise.</summary>
    public double LocalTime(double t) => Loop ? CharacterMath.Mod(t, Duration) : Math.Min(t, Duration);
}

/// <summary>Key builders of the reference (motions/curves.py).</summary>
public static class Keys
{
    public static List<(double T, double V)> Loop(double period, IReadOnlyList<double> values)
    {
        var n = (double)values.Count;
        var output = values.Select((v, i) => (period * i / n, v)).ToList();
        output.Add((period, values[0]));
        return output;
    }

    /// <summary>Quarter-period keys of a sine from start to end, settling on <paramref name="baseValue"/>.</summary>
    public static List<(double T, double V)> Oscillate(double start, double end, double period, double amplitude, double baseValue = 0,
        double phase = 0)
    {
        var keys = new List<(double, double)>();
        var t = start;
        var k = 0;
        var step = period / 4;
        while (t < end - 1e-6)
        {
            keys.Add((t, baseValue + amplitude * Math.Sin(phase + k * Math.PI / 2)));
            t += step;
            k++;
        }

        keys.Add((end, baseValue));
        return keys;
    }

    /// <summary>A jump arc: up from the ground at <paramref name="start"/>, <paramref name="height"/> at the top, back down after <paramref name="air"/> s.</summary>
    public static List<(double T, double V)> Ballistic(double start, double air, double height, int samples = 10)
    {
        var n = (double)samples;
        var keys = new List<(double, double)>(samples + 1);
        for (var k = 0; k <= samples; k++)
        {
            double kd = k;
            keys.Add((start + air * kd / n, height * 4 * (kd / n) * (1 - kd / n)));
        }

        return keys;
    }

    /// <summary>Keys delayed by dt, holding the first value meanwhile.</summary>
    public static CurveKey[] Shift(CurveKey[] keys, double dt)
    {
        if (dt <= 0)
        {
            return keys;
        }

        return [new CurveKey(0, keys[0].V), .. keys.Select(k => new CurveKey(k.T + dt, k.V))];
    }

    /// <summary>Plays motions together; a later motion wins a parameter both use.</summary>
    public static MotionSpec Merge(string name, IReadOnlyList<MotionSpec> motions, IReadOnlyList<double> delays, string? expression)
    {
        var curves = new CurveSet();
        var events = new List<MotionEvent>();
        var end = 0.0;
        for (var i = 0; i < Math.Min(motions.Count, delays.Count); i++)
        {
            var m = motions[i];
            var dt = delays[i];
            for (var j = 0; j < m.Curves.Names.Count; j++)
            {
                curves[m.Curves.Names[j]] = Shift(CurveKey.Sorted(m.Curves.Keys[j]), dt);
            }

            events.AddRange(m.Events.Select(e => new MotionEvent(e.Time + dt, e.Name)));
            end = Math.Max(end, m.Duration + dt);
        }

        return new MotionSpec(name, end, curves, expression: expression, talk: motions.Any(m => m.Talk), events: events,
            travel: motions.Sum(m => m.Travel));
    }
}

/// <summary>Helpers for building key lists like the Swift reference's array literals.</summary>
internal static class KeyList
{
    public static List<(double T, double V)> Of(params (double T, double V)[] keys) => [.. keys];

    public static List<(double T, double V)> Concat(params IEnumerable<(double T, double V)>[] parts)
    {
        var list = new List<(double T, double V)>();
        foreach (var p in parts)
        {
            list.AddRange(p);
        }

        return list;
    }
}
