using System.Globalization;
using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// A character's particles (spec/CHARACTERS.md, Particles; characters/reference/effects.py): Claude's petals carried by the
/// wind from boost up, or the kind her manifest names drifting around her at a rate that follows her intensity. Every kind
/// is thrown out in puffs at take-offs and pose swaps. Lengths are in reference units (<c>unit</c> points each); the pool is
/// reused, so a warm frame allocates nothing.
/// </summary>
public sealed class CharacterParticles
{
    public enum Look
    {
        Petal,
        Ring,
        Star,
        Wisp,
        Chip,
        Feather,
        Dot,
        Glyph,
    }

    public sealed record Kind(Look Look, (double Base, double Gain, double Floor, double Power) Rate, bool Inverse, Origin2 Start,
        (double Low, double High) Vx, (double Low, double High) Vy, double Wind, (double Amplitude, double Frequency) Wobble,
        (double Low, double High) Size, double Grow, (double Low, double High) Life, (double Low, double High) Spin, string[] Glyphs, bool Glow)
    {
        public bool Additive => Glow || Look is Look.Star or Look.Wisp;
    }

    /// <summary>Where a kind starts in the stage area.</summary>
    public enum Origin2
    {
        Body,
        Top,
        Head,
        Feet,
    }

    public struct Particle
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Rotation;
        public double Spin;
        public double Size;
        public double Age;
        public double Life;
        public double Phase;
        public bool Front;
        public (float R, float G, float B) Color;
        public int Glyph;
        public bool Burst;
    }

    public const int Limit = 160;

    private static readonly IReadOnlyDictionary<string, Kind> Kinds = new Dictionary<string, Kind>(StringComparer.Ordinal)
    {
        ["bubbles"] = new(Look.Ring, (3, 16, 0, 1), false, Origin2.Body, (-15, 15), (-90, -40), 20, (14, 4), (6, 14), 0.4, (1.6, 2.8), (0, 0), [], false),
        ["sparkles"] = new(Look.Star, (4, 26, 0, 1.5), false, Origin2.Body, (-12, 12), (-12, 12), 0, (0, 0), (8, 16), 0, (0.8, 1.6), (-40, 40), [], false),
        ["stars"] = new(Look.Star, (3, 18, 0, 1.4), false, Origin2.Body, (-10, 10), (-30, -10), 10, (6, 2), (10, 18), 0, (1.4, 2.4), (-90, 90), [], false),
        ["notes"] = new(Look.Glyph, (3, 14, 0, 1), false, Origin2.Body, (-10, 10), (-60, -30), 30, (20, 3), (18, 28), 0, (1.8, 2.8), (0, 0), ["♪", "♫"], false),
        ["embers"] = new(Look.Dot, (0, 30, 0.2, 1.3), false, Origin2.Feet, (-20, 20), (-160, -80), 25, (18, 6), (2, 4), -0.5, (0.7, 1.5), (0, 0), [], true),
        ["foxfire"] = new(Look.Wisp, (2, 10, 0, 1), false, Origin2.Body, (-8, 8), (-45, -20), 8, (16, 2.5), (7, 12), -0.3, (1.4, 2.4), (0, 0), [], false),
        ["confetti"] = new(Look.Chip, (0, 40, 0.4, 1.5), false, Origin2.Top, (-40, 40), (60, 140), 60, (25, 5), (5, 9), 0, (1.6, 2.6), (-400, 400), [], false),
        ["zzz"] = new(Look.Glyph, (1.2, 0, 0, 1), true, Origin2.Head, (10, 22), (-30, -18), 0, (6, 1.5), (14, 18), 0.7, (2.2, 3), (-10, 10), ["Z", "z"], false),
        ["pages"] = new(Look.Chip, (0, 20, 0.2, 1.4), false, Origin2.Body, (120, 220), (-40, 30), 0, (18, 5), (10, 16), 0, (1.3, 2.4), (-260, 260), [], false),
        ["blossoms"] = new(Look.Chip, (2, 24, 0, 1.5), false, Origin2.Body, (100, 200), (-30, 30), 0, (18, 5), (8, 13), 0, (1.4, 2.6), (-200, 200), [], false),
        ["feathers"] = new(Look.Feather, (3, 18, 0, 1.4), false, Origin2.Top, (-20, 20), (25, 50), 40, (30, 1.8), (14, 24), 0, (2, 3.2), (-60, 60), [], false),
        ["snow"] = new(Look.Dot, (6, 20, 0, 1), false, Origin2.Top, (-10, 10), (30, 60), 30, (12, 2), (3, 6), 0, (2.5, 4), (0, 0), [], false),
    };

    private readonly List<Particle> _items = new(Limit);
    private (float R, float G, float B)[] _colors = [(0.85f, 0.47f, 0.34f)];
    private string? _packId;
    private Rng _rng;
    private double _carry;

    public CharacterParticles(ulong seed = 11)
    {
        _rng = new Rng(seed);
    }

    public ReadOnlySpan<Particle> Items => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_items);

    /// <summary>The kind's name, null for petals.</summary>
    public string? Name { get; private set; }

    public Kind? Current { get; private set; }

    public bool IsPetals => Current is null;

    /// <summary>Bursts and wind-blown petals move fast and need a fluid frame rate; the slow ambient drift does not.</summary>
    public bool IsLively
    {
        get
        {
            if (IsPetals)
            {
                return _items.Count > 0;
            }

            foreach (var p in _items)
            {
                if (p.Burst)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Colors of a kind for a character of <paramref name="accent"/>: tints of her accent, or the kind's own colors.</summary>
    public static (float R, float G, float B)[] Palette(string name, (float R, float G, float B) accent)
    {
        var light = (0.45f * accent.R + 0.55f, 0.45f * accent.G + 0.55f, 0.45f * accent.B + 0.55f);
        return name switch
        {
            "bubbles" => [(0.75f, 0.9f, 1)],
            "sparkles" => [light, (1, 0.97f, 0.85f)],
            "stars" => [light, (1, 0.9f, 0.55f)],
            "notes" or "zzz" => [light],
            "embers" => [(1, 0.55f, 0.2f), (1, 0.8f, 0.35f)],
            "foxfire" => [(0.45f, 0.72f, 1)],
            "confetti" => [accent, (1, 0.48f, 0.72f), (1, 0.83f, 0.35f), (0.48f, 0.88f, 0.76f)],
            "pages" => [(0.98f, 0.97f, 0.93f)],
            "blossoms" => [(1, 0.86f, 0.92f), (0.86f, 0.9f, 1), (1, 1, 1)],
            "feathers" => [accent, light],
            "snow" => [(1, 1, 1)],
            _ => [accent],
        };
    }

    /// <summary><c>#RRGGBB</c> as 0...1 RGB; Claude's orange when it does not parse.</summary>
    public static (float R, float G, float B) Rgb(string? hex)
    {
        var digits = hex is { Length: 7 } && hex[0] == '#' ? hex.AsSpan(1) : hex.AsSpan();
        if (digits.Length != 6 || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
        {
            return (0.85f, 0.47f, 0.34f);
        }

        return (((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    /// <summary>Takes the kind of the pack's manifest (<c>motion.particles</c>, petals by default); without the petal sprite, sparkles.</summary>
    public void Use(CharacterPack pack, bool hasSprite)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Id == _packId)
        {
            return;
        }

        Select(pack.Manifest.Motion?.Particles ?? "petals", pack.Manifest.Accent, hasSprite);
        _packId = pack.Id;
    }

    /// <summary>Switches to the kind named <paramref name="wanted"/> (unknown names are petals) and clears the pool.</summary>
    public void Select(string wanted, string accent, bool hasSprite)
    {
        Clear();
        var name = Kinds.ContainsKey(wanted) ? wanted : "petals";
        if (name == "petals" && !hasSprite)
        {
            name = "sparkles";
        }

        Current = Kinds.GetValueOrDefault(name);
        Name = Current is null ? null : name;
        _colors = Palette(name, Rgb(accent));
        _packId = null;
    }

    public void Clear()
    {
        _items.Clear();
        _carry = 0;
    }

    /// <summary>
    /// Advances the drift. <paramref name="area"/> is the stage the particles start in (x0, y0, x1, y1); <paramref name="rate"/>
    /// multiplies the emission and is 0 while particles are off.
    /// </summary>
    public void Update(double dt, double intensity, (double X0, double Y0, double X1, double Y1) area, double unit, double rate)
    {
        if (Current is not { } kind)
        {
            UpdatePetals(dt, intensity, area, unit, rate);
            return;
        }

        var r = kind.Rate;
        var perSecond = kind.Inverse ? r.Base * Math.Pow(1 - Math.Clamp(intensity, 0, 1), 2) : r.Base + r.Gain * Math.Pow(Math.Max(0, intensity - r.Floor), r.Power);
        _carry += perSecond * rate * dt;
        while (_carry >= 1)
        {
            _carry -= 1;
            var p = Spawn(kind, intensity, area, unit, null);
            if (_items.Count < Limit)
            {
                _items.Add(p);
            }
        }

        var amplitude = kind.Wobble.Amplitude * unit;
        var frequency = kind.Wobble.Frequency;
        var kept = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var p = _items[i];
            p.Age += dt;
            if (!(p.Age < p.Life))
            {
                continue;
            }

            p.X += (p.Vx + amplitude * Math.Sin(p.Age * frequency + p.Phase)) * dt;
            p.Y += p.Vy * dt;
            p.Rotation += p.Spin * dt;
            _items[kept++] = p;
        }

        _items.RemoveRange(kept, _items.Count - kept);
    }

    private Particle Spawn(Kind kind, double intensity, (double X0, double Y0, double X1, double Y1) area, double unit, (double X, double Y)? at)
    {
        var (x0, y0, x1, y1) = area;
        var w = x1 - x0;
        var h = y1 - y0;
        double x, y;
        if (at is { } point)
        {
            (x, y) = point;
        }
        else
        {
            switch (kind.Start)
            {
                case Origin2.Top:
                    x = _rng.Uniform(x0, x1);
                    y = y0 - 0.05 * h + _rng.Uniform(0, 0.25 * h);
                    break;
                case Origin2.Head:
                    x = _rng.Uniform(x0 + 0.35 * w, x0 + 0.65 * w);
                    y = _rng.Uniform(y0, y0 + 0.2 * h);
                    break;
                case Origin2.Feet:
                    x = _rng.Uniform(x0 + 0.25 * w, x0 + 0.75 * w);
                    y = y1 - _rng.Uniform(0, 0.1 * h);
                    break;
                default:
                    x = _rng.Uniform(x0 + 0.1 * w, x0 + 0.9 * w);
                    y = _rng.Uniform(y0 + 0.1 * h, y0 + 0.8 * h);
                    break;
            }
        }

        var vx = (_rng.Uniform(kind.Vx.Low, kind.Vx.High) + kind.Wind * (0.6 + intensity)) * unit;
        var vy = _rng.Uniform(kind.Vy.Low, kind.Vy.High) * unit;
        var size = _rng.Uniform(kind.Size.Low, kind.Size.High) * unit;
        var life = _rng.Uniform(kind.Life.Low, kind.Life.High);
        var spin = _rng.Uniform(kind.Spin.Low, kind.Spin.High);
        var rotation = _rng.Uniform(0, 360);
        var phase = _rng.Uniform(0, 6.28);
        var front = _rng.Random() < 0.55;
        var color = _colors[_rng.Index(_colors.Length)];
        var glyph = kind.Glyphs.Length == 0 ? 0 : _rng.Index(kind.Glyphs.Length);
        return new Particle
        {
            X = x, Y = y, Vx = vx, Vy = vy, Rotation = rotation, Spin = spin, Size = size, Age = 0, Life = life, Phase = phase, Front = front,
            Color = color, Glyph = glyph,
        };
    }

    /// <summary>A puff thrown out from (x, y).</summary>
    public void Burst(double x, double y, double unit, int count = 14)
    {
        if (Current is not { } kind)
        {
            BurstPetals(x, y, unit, count);
            return;
        }

        for (var i = 0; i < count && _items.Count < Limit; i++)
        {
            var p = Spawn(kind, 0.6, (x, y, x, y), unit, (x, y));
            var a = _rng.Uniform(0, 2 * Math.PI);
            var v = _rng.Uniform(0.3, 0.8) * 260 * unit;
            p.Vx = Math.Cos(a) * v;
            p.Vy = Math.Sin(a) * v * 0.6 - 40 * unit;
            p.Life *= 0.7;
            p.Burst = true;
            _items.Add(p);
        }
    }

    public double Alpha(in Particle p)
    {
        var k = p.Age / p.Life;
        var a = Math.Min(1, k / 0.15) * Math.Min(1, (1 - k) / 0.3);
        if (Current?.Look == Look.Star)
        {
            var s = Math.Sin(p.Age * 9 + p.Phase);
            a *= 0.55 + 0.45 * s * s;
        }

        return a;
    }

    public double SizeOf(in Particle p) => p.Size * (1 + (Current?.Grow ?? 0) * p.Age / p.Life);

    /// <summary>Adds the dots of the particles on one side of the character, at <paramref name="opacity"/> overall.</summary>
    public void Dots(bool front, double opacity, OrbDrawList list, OrbBlend blend)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (Current is not { } kind || kind.Look == Look.Glyph)
        {
            return;
        }

        foreach (var p in _items)
        {
            if (p.Front != front)
            {
                continue;
            }

            var a = (float)(Alpha(p) * opacity);
            var s = (float)SizeOf(p);
            var x = (float)p.X;
            var y = (float)p.Y;
            var c = p.Color;
            switch (kind.Look)
            {
                case Look.Ring:
                    list.AddCharacterDot(new OrbCharacterDot(x, y, s, s, c.R, c.G, c.B, 0.22f * a, 0.25f), blend);
                    list.AddCharacterDot(new OrbCharacterDot(x - 0.35f * s, y - 0.35f * s, 0.25f * s, 0.25f * s, 1, 1, 1, 0.8f * a, 0.5f), blend);
                    break;
                case Look.Star:
                    list.AddCharacterDot(new OrbCharacterDot(x, y, s, 0.16f * s, c.R, c.G, c.B, a, 0.6f), blend);
                    list.AddCharacterDot(new OrbCharacterDot(x, y, 0.16f * s, s, c.R, c.G, c.B, a, 0.6f), blend);
                    list.AddCharacterDot(new OrbCharacterDot(x, y, 0.6f * s, 0.6f * s, c.R, c.G, c.B, 0.35f * a, 1), blend);
                    break;
                case Look.Wisp:
                    list.AddCharacterDot(new OrbCharacterDot(x, y, s, 1.7f * s, c.R, c.G, c.B, 0.45f * a, 1), blend);
                    list.AddCharacterDot(new OrbCharacterDot(x, y + 0.2f * s, 0.4f * s, 0.6f * s, 0.9f, 0.97f, 1, 0.8f * a, 0.6f), blend);
                    break;
                case Look.Chip:
                    var flip = (float)Math.Abs(Math.Cos(p.Rotation * Math.PI / 180));
                    list.AddCharacterDot(new OrbCharacterDot(x, y, 0.5f * s, 0.5f * s * (0.15f + 0.85f * flip), c.R, c.G, c.B, a, 0.15f), blend);
                    break;
                case Look.Feather:
                    list.AddCharacterDot(new OrbCharacterDot(x, y, 0.3f * s, s, c.R, c.G, c.B, 0.85f * a, 0.5f), blend);
                    break;
                case Look.Dot:
                    list.AddCharacterDot(new OrbCharacterDot(x, y, s, s, c.R, c.G, c.B, a, 0.7f), blend);
                    break;
            }
        }
    }

    /// <summary>The glyph of a glyph particle (notes, zzz).</summary>
    public string Glyph(in Particle p) => Current is { Look: Look.Glyph } kind ? kind.Glyphs[p.Glyph] : string.Empty;

    /// <summary>The largest size a glyph of the kind reaches, in points, so one atlas entry serves every particle.</summary>
    public double GlyphCeiling(double unit) => Current is { } kind ? kind.Size.High * Math.Max(1, 1 + kind.Grow) * unit : 0;

    private void UpdatePetals(double dt, double intensity, (double X0, double Y0, double X1, double Y1) area, double unit, double rate)
    {
        _carry += 34 * Math.Pow(Math.Max(0, intensity - 0.15), 1.6) * rate * dt;
        while (_carry >= 1)
        {
            _carry -= 1;
            if (_items.Count >= Limit)
            {
                continue;
            }

            var (x0, y0, x1, y1) = area;
            var w = x1 - x0;
            var h = y1 - y0;
            var fromInside = _rng.Random() < 0.6;
            var x = fromInside ? _rng.Uniform(x0, x1) : x0 - 0.15 * w;
            var y = _rng.Uniform(y0 + 0.05 * h, y0 + 0.75 * h);
            var vx = _rng.Uniform(120, 260) * (0.6 + intensity) * unit;
            var vy = _rng.Uniform(-40, 30) * unit;
            var rotation = _rng.Uniform(0, 360);
            var spin = _rng.Uniform(-260, 260);
            var size = _rng.Uniform(16, 30) * unit;
            var life = _rng.Uniform(1.3, 2.4);
            var front = _rng.Random() < 0.55;
            var phase = _rng.Uniform(0, 6.28);
            _items.Add(new Particle
            {
                X = x, Y = y, Vx = vx, Vy = vy, Rotation = rotation, Spin = spin, Size = size, Age = 0, Life = life, Phase = phase, Front = front,
                Color = _colors[0],
            });
        }

        var kept = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var p = _items[i];
            p.Age += dt;
            if (!(p.Age < p.Life))
            {
                continue;
            }

            p.X += p.Vx * dt;
            p.Y += (p.Vy + 18 * unit * Math.Sin(p.Age * 5 + p.Phase)) * dt;
            p.Rotation += p.Spin * dt;
            _items[kept++] = p;
        }

        _items.RemoveRange(kept, _items.Count - kept);
    }

    private void BurstPetals(double x, double y, double unit, int count)
    {
        for (var i = 0; i < count && _items.Count < Limit; i++)
        {
            var a = _rng.Uniform(0, 2 * Math.PI);
            var v = _rng.Uniform(0.4, 1.0) * 260 * unit;
            var rotation = _rng.Uniform(0, 360);
            var spin = _rng.Uniform(-300, 300);
            var size = _rng.Uniform(14, 26) * unit;
            var life = _rng.Uniform(0.9, 1.6);
            var front = _rng.Random() < 0.6;
            var phase = _rng.Uniform(0, 6.28);
            _items.Add(new Particle
            {
                X = x, Y = y, Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v * 0.6 - 60 * unit, Rotation = rotation, Spin = spin, Size = size, Age = 0,
                Life = life, Phase = phase, Front = front, Color = _colors[0], Burst = true,
            });
        }
    }

    /// <summary>Adds the petals on one side as sprites sampling <paramref name="uv"/> of mesh <paramref name="mesh"/>'s atlas.</summary>
    public void Sprites(bool front, int mesh, System.Numerics.Vector4 uv, double opacity, OrbDrawList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        foreach (var p in _items)
        {
            if (p.Front != front)
            {
                continue;
            }

            var k = p.Age / p.Life;
            var a = Math.Min(1, k / 0.15) * Math.Min(1, (1 - k) / 0.3) * opacity;
            var squash = 0.55 + 0.45 * Math.Abs(Math.Cos(p.Rotation * Math.PI / 180 * 1.7));
            list.AddCharacterSprite(mesh, new OrbCharacterSprite
            {
                Geometry = new System.Numerics.Vector4((float)p.X, (float)p.Y, (float)(p.Size / 2), (float)(p.Size * squash / 2)),
                Uv = uv,
                Extra = new System.Numerics.Vector4((float)(p.Rotation * Math.PI / 180), (float)a, 0, 0),
            });
        }
    }
}
