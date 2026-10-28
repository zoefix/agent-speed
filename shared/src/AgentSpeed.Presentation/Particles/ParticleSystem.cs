using AgentSpeed.Presentation.Gauge;

namespace AgentSpeed.Presentation.Particles;

public enum ParticleKind : byte
{
    Spark,
    Ember,
}

/// <summary>Position and velocity are in units of the orb diameter S relative to the disc center; size is in DIP.</summary>
public struct Particle
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
    public ParticleKind Kind;

    public readonly float Progress => Life <= 0 ? 1 : Math.Clamp(Age / Life, 0, 1);
}

public readonly record struct EmissionParameters(
    double Heat,
    double Overdrive,
    double SparkRate,
    double EmberRate,
    double LeadingDegrees,
    double ArcStartDegrees);

/// <summary>Fixed-size particle pool (sparks and embers, DESIGN section 2.5). Never allocates after construction.</summary>
public sealed class ParticleSystem
{
    public const int DefaultCapacity = 400;
    public const double Bound = 0.74;

    private readonly Particle[] _items;
    private readonly Random _random;
    private double _sparkDebt;
    private double _emberDebt;

    public ParticleSystem(int capacity = DefaultCapacity, int? seed = null)
    {
        if (capacity is <= 0 or > DefaultCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _items = new Particle[capacity];
        _random = seed is null ? new Random() : new Random(seed.Value);
    }

    public int Capacity => _items.Length;

    public int Count { get; private set; }

    public ReadOnlySpan<Particle> Particles => _items.AsSpan(0, Count);

    public void Clear()
    {
        Count = 0;
        _sparkDebt = 0;
        _emberDebt = 0;
    }

    public bool TryAdd(in Particle particle)
    {
        if (Count >= _items.Length)
        {
            return false;
        }

        _items[Count++] = particle;
        return true;
    }

    /// <summary>Emits according to the rates (particles per second) accumulated over <paramref name="dt"/>.</summary>
    public void Emit(in EmissionParameters parameters, double dt)
    {
        if (dt <= 0 || !double.IsFinite(dt))
        {
            return;
        }

        dt = Math.Min(dt, 0.1);
        _sparkDebt += Math.Max(parameters.SparkRate, 0) * dt;
        _emberDebt += Math.Max(parameters.EmberRate, 0) * dt;

        while (_sparkDebt >= 1)
        {
            _sparkDebt -= 1;
            TryAdd(CreateSpark(parameters));
        }

        while (_emberDebt >= 1)
        {
            _emberDebt -= 1;
            TryAdd(CreateEmber(parameters));
        }

        if (parameters.SparkRate <= 0)
        {
            _sparkDebt = 0;
        }

        if (parameters.EmberRate <= 0)
        {
            _emberDebt = 0;
        }
    }

    public void Step(double dt)
    {
        if (dt <= 0 || !double.IsFinite(dt))
        {
            return;
        }

        dt = Math.Min(dt, 0.1);
        var sparkDrag = (float)Math.Exp(-2.2 * dt);
        var emberDrag = (float)Math.Exp(-0.8 * dt);
        var fdt = (float)dt;

        var i = 0;
        while (i < Count)
        {
            ref var p = ref _items[i];
            p.Age += fdt;

            if (p.Kind == ParticleKind.Spark)
            {
                p.VelocityX *= sparkDrag;
                p.VelocityY *= sparkDrag;
            }
            else
            {
                p.VelocityX = p.VelocityX * emberDrag + MathF.Sin(p.Age * 7.3f + p.Seed * 6.283f) * 0.22f * fdt;
                p.VelocityY = p.VelocityY * emberDrag - 0.32f * fdt;
            }

            p.X += p.VelocityX * fdt;
            p.Y += p.VelocityY * fdt;

            var outOfBounds = p.X * p.X + p.Y * p.Y > Bound * Bound;
            if (p.Age >= p.Life || outOfBounds || !float.IsFinite(p.X) || !float.IsFinite(p.Y))
            {
                _items[i] = _items[Count - 1];
                Count--;
                continue;
            }

            i++;
        }
    }

    private Particle CreateSpark(in EmissionParameters parameters)
    {
        var fromLeading = _random.NextDouble() < 0.6 || parameters.LeadingDegrees <= parameters.ArcStartDegrees;
        var angle = fromLeading
            ? parameters.LeadingDegrees + Range(-1.5, 1.5)
            : Range(parameters.ArcStartDegrees, parameters.LeadingDegrees);
        var radius = GaugeLayout.Radius + Range(-0.015, 0.03);
        var position = GaugeLayout.Polar(radius, angle);

        var radians = angle * Math.PI / 180;
        var radial = Range(0.18, 0.46) * (0.8 + 0.4 * parameters.Heat);
        var tangential = Range(0.06, 0.24);
        var vx = Math.Cos(radians) * radial - Math.Sin(radians) * tangential;
        var vy = Math.Sin(radians) * radial + Math.Cos(radians) * tangential;
        var fraction = (angle - GaugeLayout.StartDegrees) / GaugeLayout.SweepDegrees;

        return new Particle
        {
            X = (float)position.X,
            Y = (float)position.Y,
            VelocityX = (float)vx,
            VelocityY = (float)vy,
            Age = 0,
            Life = (float)Range(0.5, 1.2),
            Size = (float)Range(1.2, 2.8),
            Heat = (float)Math.Clamp(fraction, 0, 1),
            Seed = (float)_random.NextDouble(),
            Kind = ParticleKind.Spark,
        };
    }

    private Particle CreateEmber(in EmissionParameters parameters)
    {
        var angle = Range(185, 355);
        var position = GaugeLayout.Polar(Range(0.44, 0.52), angle);
        return new Particle
        {
            X = (float)position.X,
            Y = (float)position.Y,
            VelocityX = (float)Range(-0.05, 0.05),
            VelocityY = (float)-Range(0.08, 0.22),
            Age = 0,
            Life = (float)Range(0.8, 1.6),
            Size = (float)Range(1.6, 3.4),
            Heat = (float)Range(0.84, 1.0),
            Seed = (float)_random.NextDouble(),
            Kind = ParticleKind.Ember,
        };
    }

    private double Range(double min, double max) => min + (max - min) * _random.NextDouble();
}
