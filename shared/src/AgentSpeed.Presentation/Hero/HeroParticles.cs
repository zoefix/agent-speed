using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Hero;

public enum HeroParticleKind : byte
{
    /// <summary>Dust, exhaust and tire smoke: an outline circle that grows while it fades.</summary>
    Puff,

    /// <summary>Rocket ignition exhaust ring.</summary>
    Ring,

    /// <summary>E-bike power-on electric arc around the hub motor.</summary>
    Arc,

    /// <summary>Sports-car exhaust flame pop.</summary>
    Pop,

    /// <summary>A short streak (velocity × 0.022 s) from the flame core to the stroke color.</summary>
    Spark,
}

/// <summary>A particle in canvas units (100 × 100, y down).</summary>
public struct HeroParticle
{
    public bool Alive;
    public HeroParticleKind Kind;
    public double X;
    public double Y;
    public double VelocityX;
    public double VelocityY;
    public double Age;
    public double Life;
    public double Size;
    public double Grow;
    public double Angle;
    public double Seed;
    public double Delay;
    public double Gravity;

    /// <summary>0..1 through its life once its delay has passed; negative while still delayed.</summary>
    public readonly double Progress => Age < Delay ? -1 : Math.Clamp((Age - Delay) / Life, 0, 1);
}

/// <summary>Fixed pool of 64 hero particles (DESIGN 4c "Particle streams"); never allocates after construction.</summary>
public sealed class HeroParticlePool
{
    public const int Capacity = 64;

    private readonly HeroParticle[] _items = new HeroParticle[Capacity];
    private double _debt;

    public ReadOnlySpan<HeroParticle> Items => _items;

    public int AliveCount
    {
        get
        {
            var count = 0;
            foreach (ref readonly var p in _items.AsSpan())
            {
                if (p.Alive)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public void Clear()
    {
        for (var i = 0; i < _items.Length; i++)
        {
            _items[i].Alive = false;
        }

        _debt = 0;
    }

    /// <summary>A start-up burst of up to <paramref name="count"/> particles (capped at <paramref name="cap"/>).</summary>
    public void Burst(HeroParticleKind kind, PointD at, PointD direction, int count, int cap, HeroRandom random)
    {
        var n = Math.Min(count, cap);
        var heading = Math.Atan2(direction.Y, direction.X);
        for (var i = 0; i < n; i++)
        {
            var index = Allocate();
            if (index < 0)
            {
                return;
            }

            ref var p = ref _items[index];
            p = default;
            p.Alive = true;
            p.Kind = kind;
            p.X = at.X;
            p.Y = at.Y;
            switch (kind)
            {
                case HeroParticleKind.Puff:
                {
                    var speed = 8 + 14 * random.Next();
                    var angle = heading + (random.Next() - 0.5) * 0.9;
                    p.VelocityX = Math.Cos(angle) * speed;
                    p.VelocityY = Math.Sin(angle) * speed - 3 * random.Next();
                    p.Life = 0.5 + 0.35 * random.Next();
                    p.Size = 0.8 + 0.5 * random.Next();
                    p.Grow = 2.6 + 1.6 * random.Next();
                    break;
                }

                case HeroParticleKind.Arc:
                    p.Angle = random.Next() * Math.PI * 2;
                    p.Life = 0.16 + 0.14 * random.Next();
                    p.Size = 3.8 + 2 * random.Next();
                    p.Seed = random.Next();
                    p.Delay = i * 0.03;
                    break;
                case HeroParticleKind.Pop:
                    p.VelocityX = direction.X * 4;
                    p.Life = 0.22;
                    p.Size = 1;
                    p.Angle = heading;
                    break;
                case HeroParticleKind.Ring:
                {
                    var side = i % 2 == 1 ? 1 : -1;
                    var angle = heading + side * (0.55 + 0.45 * random.Next());
                    var speed = 22 + 14 * random.Next();
                    p.VelocityX = Math.Cos(angle) * speed;
                    p.VelocityY = Math.Sin(angle) * speed;
                    p.Life = 0.45 + 0.2 * random.Next();
                    p.Size = 1;
                    p.Grow = 3 + 2 * random.Next();
                    break;
                }

                default:
                    p.Life = 0.3;
                    break;
            }
        }
    }

    /// <summary>A continuous spark stream at <paramref name="rate"/> per second (fractions carry to the next frame).</summary>
    public void Stream(PointD at, PointD direction, double rate, double speed, double spread, double gravity, double life, double dt,
        HeroRandom random)
    {
        _debt += rate * dt;
        var heading = Math.Atan2(direction.Y, direction.X);
        while (_debt >= 1)
        {
            _debt -= 1;
            var index = Allocate();
            if (index < 0)
            {
                return;
            }

            var angle = heading + (random.Next() - 0.5) * spread;
            var v = speed * (0.6 + 0.6 * random.Next());
            ref var p = ref _items[index];
            p = default;
            p.Alive = true;
            p.Kind = HeroParticleKind.Spark;
            p.X = at.X;
            p.Y = at.Y;
            p.VelocityX = Math.Cos(angle) * v;
            p.VelocityY = Math.Sin(angle) * v;
            p.Life = life * (0.7 + 0.6 * random.Next());
            p.Gravity = gravity;
            p.Size = 1;
        }
    }

    /// <summary>Ages and moves every particle; puffs drag and drift back with the road (ground speed <paramref name="ground"/>).</summary>
    public void Step(double dt, double ground)
    {
        for (var i = 0; i < _items.Length; i++)
        {
            ref var p = ref _items[i];
            if (!p.Alive)
            {
                continue;
            }

            p.Age += dt;
            if (p.Age > p.Life + p.Delay)
            {
                p.Alive = false;
                continue;
            }

            if (p.Kind is HeroParticleKind.Puff or HeroParticleKind.Ring)
            {
                var drag = Math.Exp(-dt * 3);
                p.VelocityX *= drag;
                p.VelocityY *= drag;
                p.VelocityX -= 0.25 * ground * dt * 0.6;
            }
            else if (p.Kind == HeroParticleKind.Spark)
            {
                p.VelocityY += p.Gravity * dt;
            }

            p.X += p.VelocityX * dt;
            p.Y += p.VelocityY * dt;
        }
    }

    private int Allocate()
    {
        for (var i = 0; i < _items.Length; i++)
        {
            if (!_items[i].Alive)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>An ambient speed line in canvas units, moving along (<see cref="DirectionX"/>, <see cref="DirectionY"/>).</summary>
public struct HeroStreak
{
    public bool Alive;
    public bool Burst;
    public double X;
    public double Y;
    public double DirectionX;
    public double DirectionY;
    public double Length;
    public double Speed;
    public double Alpha;
    public double Age;
}

/// <summary>Fixed pool of 16 speed lines (DESIGN 4c "Speed lines").</summary>
public sealed class HeroStreakPool
{
    public const int Capacity = 16;

    private readonly HeroStreak[] _items = new HeroStreak[Capacity];
    private double _timer;

    public ReadOnlySpan<HeroStreak> Items => _items;

    public int AliveCount
    {
        get
        {
            var count = 0;
            foreach (ref readonly var s in _items.AsSpan())
            {
                if (s.Alive)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public void Clear()
    {
        for (var i = 0; i < _items.Length; i++)
        {
            _items[i].Alive = false;
        }

        _timer = 0;
    }

    /// <summary>
    /// Spawns one line. Lines whose height crosses the vehicle peel off its tail; rocket lines run against the heading
    /// across the whole disc.
    /// </summary>
    public void Spawn(bool burst, Vehicle vehicle, double heat, double ground, HeroRandom random)
    {
        var index = -1;
        for (var i = 0; i < _items.Length; i++)
        {
            if (!_items[i].Alive)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        ref var s = ref _items[index];
        s = default;
        s.Alive = true;
        s.Burst = burst;
        s.Length = (6 + 20 * heat) * (0.7 + 0.6 * random.Next()) * (burst ? 1.4 : 1);
        s.Speed = 1.25 * Math.Max(ground, 40) * (0.85 + 0.3 * random.Next()) * (burst ? 1.3 : 1);
        s.Alpha = (0.16 + 0.32 * heat) * (0.7 + 0.6 * random.Next()) * (burst ? 1.4 : 1);
        if (vehicle.Heading is { } degrees && degrees != 0)
        {
            var heading = degrees * Math.PI / 180;
            var lateral = (random.Next() - 0.5) * 80;
            var ux = Math.Cos(heading);
            var uy = Math.Sin(heading);
            s.DirectionX = -ux;
            s.DirectionY = -uy;
            s.X = 50 + ux * 52 - uy * lateral;
            s.Y = 50 + uy * 52 + ux * lateral;
            return;
        }

        s.DirectionX = -1;
        s.DirectionY = 0;
        var y = 16 + 68 * random.Next();
        s.Y = y;
        var bounds = vehicle.Bounds;
        s.X = y > bounds.Y - 2 && y < bounds.Bottom + 2 ? bounds.X - 2 : 102 + s.Length * random.Next() * 0.5;
    }

    /// <summary>Moves the lines and tops the pool up to <paramref name="cap"/> at the cadence of DESIGN 4c.</summary>
    public void Step(double dt, int cap, Vehicle vehicle, double heat, double ground, HeroRandom random)
    {
        var alive = 0;
        for (var i = 0; i < _items.Length; i++)
        {
            ref var s = ref _items[i];
            if (!s.Alive)
            {
                continue;
            }

            s.Age += dt;
            s.X += s.DirectionX * s.Speed * dt;
            s.Y += s.DirectionY * s.Speed * dt;
            if (s.X < -s.Length - 4 || s.X > 140 || s.Y < -40 || s.Y > 140)
            {
                s.Alive = false;
            }
            else
            {
                alive++;
            }
        }

        _timer -= dt;
        if (alive < cap && _timer <= 0)
        {
            Spawn(false, vehicle, heat, ground, random);
            _timer = 0.9 / Math.Max(cap, 1) * (0.5 + random.Next());
        }
    }
}
