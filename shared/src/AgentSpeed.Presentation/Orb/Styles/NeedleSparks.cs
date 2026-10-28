using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// The peg sparks of the <c>needle</c> (DESIGN 2a.2, Motion): a fixed pool sized for Max, compact so the live sparks
/// are one contiguous span. Positions in S from the disc center, velocities in S/s. Never allocates after construction.
/// </summary>
internal sealed class NeedleSparks
{
    public const int StandardCapacity = 48;
    public const int MaxCapacity = 80;
    public const double MinSpeed = 0.35;
    public const double MaxSpeed = 0.9;
    public const double SpreadRadians = Math.PI / 6;
    public const double OutwardSpeed = 0.2;
    public const double Gravity = 1.1;
    public const double Drag = 2;
    public const double MinLife = 0.22;
    public const double MaxLife = 0.55;
    public const double MinWidth = 0.8;
    public const double MaxWidth = 1.6;
    public const double StreakSeconds = 0.025;

    /// <summary>Sparks leaving the 1.5 S window are dropped.</summary>
    public const double Bound = 0.75;

    private readonly Spark[] _items = new Spark[MaxCapacity];
    private readonly NeedleSpark[] _view = new NeedleSpark[MaxCapacity];
    private readonly double _contactX;
    private readonly double _contactY;
    private readonly double _contactRadians;
    private double _debt;

    public NeedleSparks()
    {
        _contactRadians = StyleMath.DialDegrees(NeedleGeometry.ContactDial) / StyleMath.DegreesPerRadian;
        _contactX = NeedleGeometry.ContactRadius * Math.Cos(_contactRadians);
        _contactY = NeedleGeometry.ContactRadius * Math.Sin(_contactRadians);
    }

    public int Count { get; private set; }

    public ReadOnlySpan<NeedleSpark> View => _view.AsSpan(0, Count);

    public void Clear()
    {
        Count = 0;
        _debt = 0;
    }

    public void StopEmitting() => _debt = 0;

    /// <summary>Advances the live sparks by <paramref name="dt"/>, then emits <paramref name="rate"/> per second.</summary>
    public void Step(double dt, double rate, int capacity, HeroRandom random)
    {
        if (dt <= 0)
        {
            return;
        }

        Move(0, dt);

        if (rate <= 0)
        {
            _debt = 0;
            return;
        }

        _debt += rate * dt;
        var born = (int)_debt;
        _debt -= born;
        capacity = Math.Min(capacity, MaxCapacity);
        for (var j = 0; j < born; j++)
        {
            if (Count >= capacity)
            {
                continue;
            }

            Spawn(random);

            // Spread the births over the frame so 30 and 72 fps look alike.
            AdvanceOne(Count - 1, dt * (born - j - 0.5) / born);
        }
    }

    /// <summary>Fills the public view: streak, width (DIP), color and alpha with the readout-zone fade.</summary>
    public void Publish(NeedlePalette palette, double sizeScale)
    {
        for (var i = 0; i < Count; i++)
        {
            ref readonly var p = ref _items[i];
            var u = p.Life <= 0 ? 1 : StyleMath.Clamp01(p.Age / p.Life);
            var alpha = (1 - u) * ReadoutZone.Fade(p.X, p.Y);
            _view[i] = new NeedleSpark(
                (float)p.X,
                (float)p.Y,
                (float)(-p.Vx * StreakSeconds),
                (float)(-p.Vy * StreakSeconds),
                (float)(p.Size * (1 - 0.5 * u) * sizeScale),
                palette.Spark(u),
                (float)alpha);
        }
    }

    private void Spawn(HeroRandom random)
    {
        var speed = Range(random, MinSpeed, MaxSpeed);
        var direction = _contactRadians + Math.PI / 2 + Range(random, -SpreadRadians, SpreadRadians);
        _items[Count++] = new Spark
        {
            X = _contactX,
            Y = _contactY,
            Vx = Math.Cos(direction) * speed + Math.Cos(_contactRadians) * OutwardSpeed,
            Vy = Math.Sin(direction) * speed + Math.Sin(_contactRadians) * OutwardSpeed,
            Age = 0,
            Life = Range(random, MinLife, MaxLife),
            Size = Range(random, MinWidth, MaxWidth),
        };
    }

    private void Move(int from, double dt)
    {
        var i = from;
        while (i < Count)
        {
            if (AdvanceOne(i, dt))
            {
                i++;
            }
        }
    }

    /// <returns>False when the spark died and the slot now holds the last one.</returns>
    private bool AdvanceOne(int i, double dt)
    {
        ref var p = ref _items[i];
        p.Age += dt;
        p.Vy += Gravity * dt;
        var drag = Math.Exp(-Drag * dt);
        p.Vx *= drag;
        p.Vy *= drag;
        p.X += p.Vx * dt;
        p.Y += p.Vy * dt;
        if (p.Age < p.Life && Math.Abs(p.X) < Bound && Math.Abs(p.Y) < Bound)
        {
            return true;
        }

        _items[i] = _items[Count - 1];
        Count--;
        return false;
    }

    private static double Range(HeroRandom random, double min, double max) => min + (max - min) * random.Next();

    private struct Spark
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Age;
        public double Life;
        public double Size;
    }
}
