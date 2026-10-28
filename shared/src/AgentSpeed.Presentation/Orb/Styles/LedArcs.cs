using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>A point of an arc or bolt path in S from the disc center (y down).</summary>
public readonly record struct LedPoint(float X, float Y);

/// <summary>
/// Electric arcs at the redline tier (DESIGN 2.1; prototype <c>makeArc</c>, <c>drawArc</c>): jagged discharges from a random
/// lit point of the gauge (0.41 S) out to 0.50–0.60 S, a 10-point polyline re-jittered every rendered frame. A fixed pool;
/// the paths are rebuilt in place.
/// </summary>
public sealed class LedArcs
{
    public const int Capacity = 8;
    public const int PointCount = 10;

    /// <summary>Jitter of the interior points, ± half of this (S).</summary>
    public const double Jitter = 0.045;

    private readonly Arc[] _arcs = new Arc[Capacity];
    private readonly LedPoint[] _points = new LedPoint[Capacity * PointCount];
    private readonly HeroRandom _random;
    private double _timer;

    public LedArcs(uint seed)
    {
        _random = new HeroRandom(seed);
    }

    public int Count { get; private set; }

    /// <summary>Arcs spawned since construction (tests).</summary>
    public long Spawned { get; private set; }

    /// <summary>Seconds between two arcs before the random factor 0.5…1.5: <c>(0.28 − 0.2 o) / lvl</c>.</summary>
    public static double Interval(double o, double level) => (0.28 - 0.2 * o) / level;

    /// <summary>The path of arc <paramref name="index"/> (0 ≤ index &lt; <see cref="Count"/>).</summary>
    public ReadOnlySpan<LedPoint> Path(int index) => _points.AsSpan(index * PointCount, PointCount);

    /// <summary>The arc's opacity, <c>1 − age / life</c>.</summary>
    public double Fade(int index) => Math.Clamp(1 - _arcs[index].Age / _arcs[index].Life, 0, 1);

    public void Clear()
    {
        Count = 0;
        _timer = 0;
    }

    /// <param name="active">Redline tier at Standard or Max.</param>
    /// <param name="h">Smoothed heat: arcs start on the lit part of the gauge.</param>
    public void Step(double dt, bool active, double h, double o, double level)
    {
        if (!(dt > 0))
        {
            return;
        }

        if (active)
        {
            _timer -= dt;
            if (_timer <= 0)
            {
                Spawn(h);
                _timer = Interval(o, level) * (0.5 + _random.Next());
            }
        }

        for (var i = 0; i < Count;)
        {
            ref var arc = ref _arcs[i];
            arc.Age += dt;
            if (arc.Age >= arc.Life)
            {
                RemoveAt(i);
                continue;
            }

            i++;
        }

        for (var i = 0; i < Count; i++)
        {
            Shape(i);
        }
    }

    private void Spawn(double h)
    {
        var a0 = (135 + _random.Next() * h * 270) * Math.PI / 180;
        var arc = new Arc
        {
            A0 = a0,
            A1 = a0 + (_random.Next() - 0.5) * 0.9,
            R0 = 0.41,
            R1 = 0.5 + _random.Next() * 0.1,
            Life = 0.09 + _random.Next() * 0.1,
        };
        Spawned++;
        if (Count == Capacity)
        {
            RemoveAt(0);
        }

        _arcs[Count] = arc;
        Shape(Count);
        Count++;
    }

    /// <summary>The prototype jitters x and y by the same amount, along the diagonal.</summary>
    private void Shape(int index)
    {
        ref readonly var arc = ref _arcs[index];
        var offset = index * PointCount;
        const int steps = PointCount - 1;
        for (var i = 0; i <= steps; i++)
        {
            var k = i / (double)steps;
            var a = arc.A0 + (arc.A1 - arc.A0) * k;
            var r = arc.R0 + (arc.R1 - arc.R0) * k;
            var j = i == 0 || i == steps ? 0 : (_random.Next() - 0.5) * Jitter;
            _points[offset + i] = new LedPoint((float)(Math.Cos(a) * r + j), (float)(Math.Sin(a) * r + j));
        }
    }

    private void RemoveAt(int index)
    {
        Count--;
        for (var i = index; i < Count; i++)
        {
            _arcs[i] = _arcs[i + 1];
            Array.Copy(_points, (i + 1) * PointCount, _points, i * PointCount, PointCount);
        }
    }

    private struct Arc
    {
        public double A0;
        public double A1;
        public double R0;
        public double R1;
        public double Age;
        public double Life;
    }
}
