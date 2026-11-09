namespace AgentSpeed.Presentation.Character;

/// <summary>
/// A soft round light of the floating readout (hud.py dots): an ellipse with radii (rx, ry) around (x, y). Coverage at
/// normalized radius r: <c>softness</c> ≥ 0.99 is a glow, (1 - r)²; otherwise 1 - smoothstep(1 - softness, 1, r).
/// Colors are straight RGB 0…1 with <c>alpha</c>; <c>additive</c> lights add up, the others blend over.
/// </summary>
public readonly record struct HudDot(float X, float Y, float Rx, float Ry, float R, float G, float B, float Alpha, float Softness, bool Additive);

/// <summary>The reference heat palette (green → yellow → orange → red), as 0…255 integer RGB like hud.py.</summary>
public static class HeatColor
{
    private static readonly (double H, (double R, double G, double B) C)[] Stops =
    [
        (0.0, (61, 220, 132)), (0.35, (255, 213, 74)), (0.7, (255, 138, 61)), (1.0, (255, 59, 59)),
    ];

    public static (double R, double G, double B) WhiteHot { get; } = (255, 247, 214);

    public static (double R, double G, double B) Rgb(double h)
    {
        h = Math.Min(Math.Max(h, 0), 1);
        for (var i = 0; i < Stops.Length - 1; i++)
        {
            var (h0, c0) = Stops[i];
            var (h1, c1) = Stops[i + 1];
            if (h <= h1)
            {
                var k = (h - h0) / (h1 - h0);
                return (Math.Truncate(c0.R + (c1.R - c0.R) * k), Math.Truncate(c0.G + (c1.G - c0.G) * k), Math.Truncate(c0.B + (c1.B - c0.B) * k));
            }
        }

        return Stops[^1].C;
    }
}

/// <summary>Where the new-peak pill sits this frame.</summary>
public readonly record struct HudBody(double X, double Y, double Scale, double Alpha);

/// <summary>The geometry the readout is laid around, in screen points (y down).</summary>
/// <param name="Ground">The gauge ring's ellipse at the feet.</param>
/// <param name="Waist">The agents' orbit around the waist.</param>
/// <param name="CharacterHeight">The idle character's height.</param>
/// <param name="Unit">Points per reference unit (the reference film is 1350 px tall: unit = height / 1350).</param>
public readonly record struct HudLayout((double Cx, double Cy, double Rx, double Ry) Ground, (double Cx, double Cy, double Rx, double Ry) Waist,
    double CharacterHeight, double Unit);

/// <summary>
/// The floating readout's state and lights (hud.py): the gauge ring with its comet, response pulses, agent lights orbiting
/// the waist, embers at redline, the rolling speed and the new peak burst. The cards are placed by <see cref="HudWings"/>;
/// text is the renderer's: it draws the speed digits of <see cref="Odometer"/>, the unit, the title (crossfading over
/// <see cref="TitleProgress"/>) and the cards.
/// </summary>
public sealed class CharacterHud
{
    private struct Pulse
    {
        public double T;
        public double K;
    }

    private struct Ember
    {
        public double X;
        public double Y;
        public double Vy;
        public double Age;
        public double Life;
        public double Seed;
        public bool Front;
    }

    private readonly List<HudDot> _back = new(512);
    private readonly List<HudDot> _front = new(512);
    private readonly List<Pulse> _pulses = new(16);
    private readonly List<Ember> _embers = new(256);
    private Rng _rng;
    private Follow _peakFollow;
    private SecondOrder _width = new(2.5, 0.8, 0);
    private double _comet;
    private double _orbitPhase;
    private double _heat;
    private double _intensity;
    private double _time;

    public CharacterHud(ulong seed = 5, double unit = 1)
    {
        _rng = new Rng(seed);
        _peakFollow = new Follow(f: 2.0, z: 0.6, amplitude: 3.0 * unit, period: 2.9, phase: 0.7);
    }

    public Odometer Odometer { get; } = new();

    /// <summary>The title shown and the one it replaces while they crossfade (0 → 1).</summary>
    public string? Title { get; private set; }

    public string? PreviousTitle { get; private set; }

    public double TitleProgress { get; private set; } = 1;

    /// <summary>Seconds since the new-peak burst began, null when none shows.</summary>
    public double? PeakAge { get; private set; }

    public HudBody? Peak { get; private set; }

    /// <summary>Lights behind the character, rebuilt every frame (capacity reused).</summary>
    public ReadOnlySpan<HudDot> Back => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_back);

    /// <summary>Lights in front of the character, rebuilt every frame.</summary>
    public ReadOnlySpan<HudDot> Front => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_front);

    /// <summary>The speed's width spring (points), so the digits glide when their count changes.</summary>
    public double SpeedWidth { get; private set; }

    /// <summary>Multiplies the ember rate (0 turns embers off: Soft and Off effects; 1.6 at Max).</summary>
    public double EmberRate { get; set; } = 1;

    public bool HasEmbers => _embers.Count > 0;

    public bool HasPulses => _pulses.Count > 0;

    /// <summary>A response finished: a ripple runs out from the ring.</summary>
    public void AddPulse(double strength = 1) => _pulses.Add(new Pulse { T = 0, K = strength });

    /// <summary>A new peak: the gold pill pops above the speed.</summary>
    public void CelebratePeak()
    {
        PeakAge = 0;
        _peakFollow.Placed = false;
    }

    /// <summary>Advances the readout one frame: <paramref name="speed"/> the value to show, <paramref name="heat"/> 0…1, the director's intensity, the speed title text.</summary>
    public void Update(double dt, double time, double speed, double heat, double intensity, string title)
    {
        _comet = CharacterMath.Mod(_comet + dt / (6.0 - 5.3 * heat), 1.0);
        _orbitPhase += dt * (0.25 + 0.9 * heat);
        if (title != Title)
        {
            PreviousTitle = Title;
            Title = title;
            TitleProgress = 0;
        }

        TitleProgress = Math.Min(1.0, TitleProgress + dt / 0.35);
        for (var i = 0; i < _pulses.Count; i++)
        {
            var p = _pulses[i];
            p.T += dt;
            _pulses[i] = p;
        }

        _pulses.RemoveAll(p => p.T >= 0.9);
        if (PeakAge is { } age)
        {
            PeakAge = age + dt > 2.4 ? null : age + dt;
        }

        _heat = heat;
        _intensity = intensity;
        _time = time;
        Odometer.Update(dt, speed);
    }

    /// <summary>
    /// Rebuilds the lights for this frame and moves the new-peak pill toward <paramref name="peakAt"/> (above the speed).
    /// <paramref name="digitAdvance"/> is the renderer's width of one digit of the speed (points), for the width spring;
    /// <paramref name="agents"/> are the agent colors (0…255 RGB).
    /// </summary>
    public void Layout(double dt, HudLayout l, IReadOnlyList<(double R, double G, double B)> agents, double digitAdvance, (double X, double Y) peakAt)
    {
        var u = l.Unit;
        SpeedWidth = _width.Update(dt, digitAdvance * Odometer.Digits.Count);
        if (PeakAge is { } age)
        {
            var (px, py) = _peakFollow.Update(dt, _time, peakAt.X, peakAt.Y);
            var pop = age > 0 ? 1 + 0.25 * Math.Exp(-6 * age) * Math.Sin(14 * age) : 1;
            var grow = Math.Min(1.0, 0.6 + age * 3);
            Peak = new HudBody(px, py, pop * grow, Math.Min(1.0, age / 0.15) * Math.Min(1.0, (2.4 - age) / 0.4));
        }
        else
        {
            Peak = null;
        }

        _back.Clear();
        _front.Clear();
        var g = l.Ground;
        UpdateEmbers(dt, g, u);
        var c = HeatColor.Rgb(_heat);
        _back.Add(Dot(g.Cx, g.Cy - g.Ry * 0.1, g.Rx * 0.55, g.Ry * 0.9, (0.02 * 255, 0.02 * 255, 0.04 * 255), 0.55, 1, additive: false));
        _back.Add(Dot(g.Cx, g.Cy, g.Rx * 1.2, g.Ry * 1.8, c, 0.12 + 0.3 * _heat, 1));
        Ring(g, u);
        PulseRows(g, u);
        Satellites(l.Waist, u, agents);
        EmberRows(u);
        if (PeakAge is { } peakAge && Peak is { } p)
        {
            for (var i = 0; i < 24; i++)
            {
                var ang = 2 * Math.PI * i / 24;
                var dist = (40 + 160 * Math.Min(1.0, peakAge / 0.6)) * u;
                _front.Add(Dot(p.X + dist * Math.Cos(ang), p.Y + dist * 0.5 * Math.Sin(ang), 2.2 * u, 2.2 * u, (255, 200, 80), Math.Max(0.0, 0.8 - peakAge), 0.6));
            }
        }
    }

    private static HudDot Dot(double x, double y, double rx, double ry, (double R, double G, double B) c, double a, double soft, bool additive = true) =>
        new((float)x, (float)y, (float)rx, (float)ry, (float)(c.R / 255), (float)(c.G / 255), (float)(c.B / 255), (float)a, (float)soft, additive);

    private void Put(in HudDot d, bool front)
    {
        if (front)
        {
            _front.Add(d);
        }
        else
        {
            _back.Add(d);
        }
    }

    private void Ring((double Cx, double Cy, double Rx, double Ry) g, double u)
    {
        const int n = 96;
        var lit = _heat * n;
        for (var i = 0; i < n; i++)
        {
            var a = Math.PI / 2 + 2 * Math.PI * i / n;
            var s = Math.Sin(a);
            var x = g.Cx + g.Rx * Math.Cos(a);
            var y = g.Cy + g.Ry * s;
            var depth = 0.5 + 0.5 * (s + 1) / 2;
            var size = (2.2 + 1.6 * (s + 1) / 2) * u;
            var cm = CharacterMath.Mod((double)i / n - _comet + 0.5, 1.0) - 0.5;
            var comet = Math.Exp(-((cm * 18) * (cm * 18)));
            if (i < lit)
            {
                var leading = i == (int)lit;
                var c = leading ? HeatColor.WhiteHot : HeatColor.Rgb((double)i / n);
                var k = leading ? 1.0 : Math.Min(1.0, lit - i);
                Put(Dot(x, y, size * 3.2, size * 1.7, c, (0.16 + 0.3 * comet) * depth * k, 1), s >= 0);
                Put(Dot(x, y, size, size * 0.62, c, (0.75 + 0.25 * comet) * depth * k, 0.35), s >= 0);
            }
            else
            {
                Put(Dot(x, y, size * 0.75, size * 0.47, (255, 255, 255), (0.16 + 0.3 * comet * (0.4 + _heat)) * depth, 0.4), s >= 0);
            }
        }
    }

    private void PulseRows((double Cx, double Cy, double Rx, double Ry) g, double u)
    {
        var c = HeatColor.Rgb(_heat);
        foreach (var p in _pulses)
        {
            var k = p.T / 0.9;
            var ease = 1 - Math.Pow(1 - k, 3);
            var grow = 1 + 0.42 * ease;
            for (var i = 0; i < 96; i++)
            {
                var a = 2 * Math.PI * i / 96;
                var s = Math.Sin(a);
                Put(Dot(g.Cx + g.Rx * grow * Math.Cos(a), g.Cy + g.Ry * grow * s, 1.6 * u, 1.0 * u, c, 0.55 * (1 - k) * p.K, 0.5), s >= 0);
            }
        }
    }

    private void Satellites((double Cx, double Cy, double Rx, double Ry) w, double u, IReadOnlyList<(double R, double G, double B)> agents)
    {
        for (var k = 0; k < agents.Count; k++)
        {
            var color = agents[k];
            var baseAngle = _orbitPhase + 2 * Math.PI * k / Math.Max(1, agents.Count);
            for (var j = 0; j < 6; j++)
            {
                var a = baseAngle - j * 0.07;
                var s = Math.Sin(a);
                var x = w.Cx + w.Rx * Math.Cos(a);
                var y = w.Cy + w.Ry * s + -0.14 * w.Rx * Math.Cos(a);
                var depth = 0.6 + 0.4 * (s + 1) / 2;
                if (j == 0)
                {
                    Put(Dot(x, y, 18 * u * depth, 18 * u * depth, color, 0.45 * depth, 1), s >= 0);
                    var core = (color.R * 0.35 + 0.65 * 255, color.G * 0.35 + 0.65 * 255, color.B * 0.35 + 0.65 * 255);
                    Put(Dot(x, y, 4.5 * u * depth, 4.5 * u * depth, core, depth, 0.3), s >= 0);
                }
                else
                {
                    var r = (3.6 - 0.45 * j) * u * depth;
                    Put(Dot(x, y, r, r, color, (0.55 - 0.08 * j) * depth, 0.6), s >= 0);
                }
            }
        }
    }

    private void UpdateEmbers(double dt, (double Cx, double Cy, double Rx, double Ry) g, double u)
    {
        var rate = 70 * Math.Max(0.0, (_heat - 0.6) / 0.4) * _intensity * EmberRate;
        var n = (int)(rate * dt + _rng.Random());
        for (var i = 0; i < n; i++)
        {
            var a = _rng.Uniform(0, 2 * Math.PI);
            var vy = -_rng.Uniform(60, 160);
            var life = _rng.Uniform(0.7, 1.5);
            var seed = _rng.Uniform(0, 6.28);
            _embers.Add(new Ember { X = g.Cx + g.Rx * Math.Cos(a), Y = g.Cy + g.Ry * Math.Sin(a), Vy = vy, Age = 0, Life = life, Seed = seed, Front = Math.Sin(a) > 0 });
        }

        var kept = 0;
        for (var i = 0; i < _embers.Count; i++)
        {
            var e = _embers[i];
            e.Age += dt;
            if (e.Age < e.Life)
            {
                e.Y += e.Vy * u * dt;
                e.X += 18 * u * Math.Sin(e.Age * 6 + e.Seed) * dt;
                _embers[kept++] = e;
            }
        }

        _embers.RemoveRange(kept, _embers.Count - kept);
    }

    private void EmberRows(double u)
    {
        foreach (var e in _embers)
        {
            var k = e.Age / e.Life;
            Put(Dot(e.X, e.Y, 1.8 * u, 1.8 * u, HeatColor.Rgb(0.7 + 0.3 * (1 - k)), 0.8 * (1 - k), 0.6), e.Front);
        }
    }
}
