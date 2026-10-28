using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Orb.Styles;

public enum LedParticleKind : byte
{
    /// <summary>A streak from the lit arc (or the new-peak burst) in heat colors.</summary>
    Spark,

    /// <summary>A rising, flickering flame particle at the redline.</summary>
    Ember,

    /// <summary>A gold streak of the new-peak burst.</summary>
    Gold,
}

/// <summary>Position and velocity in S (from the disc center, y down) and S per second; size in points at S = 148.</summary>
public struct LedParticle
{
    public float X;
    public float Y;
    public float VelocityX;
    public float VelocityY;
    public float Age;
    public float Life;
    public float Size;
    public float Heat;
    public float Seed;
    public float Drag;
    public LedParticleKind Kind;

    /// <summary>The prototype's <c>life</c>: 1 when born, 0 when it dies.</summary>
    public readonly float Remaining => Life <= 0 ? 0 : Math.Clamp(1 - Age / Life, 0, 1);

    public readonly bool Streak => Kind != LedParticleKind.Ember;
}

/// <summary>
/// The <c>led</c> style's sparks, embers and new-peak burst (DESIGN 2.1, layer 5; prototype <c>emitSpark</c>,
/// <c>emitEmber</c>, <c>burst</c>): one fixed pool of <see cref="Capacity"/>, emission by accumulated rates, deterministic
/// for a seed, never allocating after construction.
/// </summary>
public sealed class LedParticles
{
    public const int Capacity = 640;
    public const int BurstCount = 160;

    /// <summary>Particles fade out by this radius (S) and die past it.</summary>
    public const double Bound = 0.74;

    public const double SparkRate = 120;
    public const double SparkBoost = 1.6;
    public const double EmberRate = 90;
    public const double EmberBoost = 1.4;
    public const double SpeedBoost = 1.3;

    private readonly LedParticle[] _items = new LedParticle[Capacity];
    private readonly HeroRandom _random;
    private EmissionDebt _sparks;
    private EmissionDebt _embers;

    public LedParticles(uint seed)
    {
        _random = new HeroRandom(seed);
    }

    public int Count { get; private set; }

    public ReadOnlySpan<LedParticle> Particles => _items.AsSpan(0, Count);

    /// <summary>Particles spawned since construction (tests).</summary>
    public long Spawned { get; private set; }

    /// <summary>Spark emission per second: <c>120 (h − 0.45) / 0.55 × lvl × 1.6</c> from <c>h ≥ 0.45</c>, times the LOD scale.</summary>
    public static double SparksPerSecond(double h, double level, double countScale) =>
        h >= 0.45 ? SparkRate * ((h - 0.45) / 0.55) * level * SparkBoost * countScale : 0;

    /// <summary>Ember emission per second at the redline tier: <c>90 max(o, 0.3) × lvl × 1.4</c>, times the LOD scale.</summary>
    public static double EmbersPerSecond(bool redline, double o, double level, double countScale) =>
        redline ? EmberRate * Math.Max(o, 0.3) * level * EmberBoost * countScale : 0;

    public void Clear()
    {
        Count = 0;
        _sparks.Reset();
        _embers.Reset();
    }

    /// <summary>Emits for <paramref name="dt"/> at the rates of the frame and moves every particle.</summary>
    /// <param name="h">Smoothed heat (sparks leave the arc up to it).</param>
    /// <param name="time">Shared clock (the embers' sideways sway).</param>
    public void Step(double dt, double time, double h, double o, double sparksPerSecond, double embersPerSecond)
    {
        if (!(dt > 0) || !double.IsFinite(dt))
        {
            return;
        }

        var sparks = _sparks.Take(sparksPerSecond, dt);
        for (var i = 0; i < sparks; i++)
        {
            EmitSpark(_random.Next() < 0.6, h, o);
        }

        var embers = _embers.Take(embersPerSecond, dt);
        for (var i = 0; i < embers; i++)
        {
            EmitEmber();
        }

        var fdt = (float)dt;
        var sway = (float)(0.3 * dt);
        var lift = (float)(0.12 * dt);
        for (var i = 0; i < Count;)
        {
            ref var p = ref _items[i];
            p.Age += fdt;
            if (p.Kind == LedParticleKind.Ember)
            {
                p.VelocityX += (float)Math.Sin(time * 3 + p.Seed) * sway;
                p.VelocityY -= lift;
            }

            var k = MathF.Exp(-p.Drag * fdt);
            p.VelocityX *= k;
            p.VelocityY *= k;
            p.X += p.VelocityX * fdt;
            p.Y += p.VelocityY * fdt;
            if (p.Age >= p.Life || p.X * p.X + p.Y * p.Y >= Bound * Bound || !float.IsFinite(p.X) || !float.IsFinite(p.Y))
            {
                _items[i] = _items[--Count];
                continue;
            }

            i++;
        }
    }

    /// <summary>The new-peak burst: <paramref name="count"/> streaks outward from 0.48 S, 30 % of them gold.</summary>
    public void Burst(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var a = _random.Next() * Math.PI * 2;
            var speed = 0.5 + _random.Next() * 1.3;
            Spawn(new LedParticle
            {
                X = (float)(Math.Cos(a) * 0.48),
                Y = (float)(Math.Sin(a) * 0.48),
                VelocityX = (float)(Math.Cos(a) * speed),
                VelocityY = (float)(Math.Sin(a) * speed),
                Life = (float)(0.7 + _random.Next() * 0.9),
                Size = (float)(1.4 + _random.Next() * 2.2),
                Heat = (float)(0.9 + _random.Next() * 0.1),
                Kind = _random.Next() < 0.3 ? LedParticleKind.Gold : LedParticleKind.Spark,
                Drag = 2.4f,
            });
        }
    }

    private void EmitSpark(bool fromTip, double h, double o)
    {
        var position = fromTip ? h : _random.Next() * h;
        var a = (135 + position * 270) * Math.PI / 180;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        var speed = (0.3 + _random.Next() * 0.6) * (1 + o * 0.8) * SpeedBoost;
        var tangential = (_random.Next() - 0.35) * 0.6 * SpeedBoost;
        Spawn(new LedParticle
        {
            X = (float)(cos * 0.41),
            Y = (float)(sin * 0.41),
            VelocityX = (float)(cos * speed - sin * tangential),
            VelocityY = (float)(sin * speed + cos * tangential),
            Life = (float)(0.5 + _random.Next() * 0.7),
            Size = (float)(1.2 + _random.Next() * 1.6),
            Heat = (float)Math.Min(1, position + 0.1),
            Kind = LedParticleKind.Spark,
            Drag = 2.2f,
        });
    }

    private void EmitEmber()
    {
        var a = (200 + _random.Next() * 140) * Math.PI / 180;
        Spawn(new LedParticle
        {
            X = (float)(Math.Cos(a) * 0.47),
            Y = (float)(Math.Sin(a) * 0.47),
            VelocityX = (float)((_random.Next() - 0.5) * 0.1),
            VelocityY = (float)-(0.12 + _random.Next() * 0.2),
            Life = (float)(0.9 + _random.Next() * 1.1),
            Size = (float)(1.2 + _random.Next() * 1.8),
            Heat = (float)(0.85 + _random.Next() * 0.15),
            Seed = (float)(_random.Next() * 100),
            Kind = LedParticleKind.Ember,
            Drag = 0.4f,
        });
    }

    private void Spawn(in LedParticle particle)
    {
        if (Count >= Capacity)
        {
            return;
        }

        _items[Count++] = particle;
        Spawned++;
    }
}
