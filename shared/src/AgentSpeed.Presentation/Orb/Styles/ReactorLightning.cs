using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>Lightning bolts and their sparks (DESIGN 2a.4, layer 8).</summary>
public sealed partial class ReactorModel
{
    public const int MaxBolts = 4;
    public const int BoltPointCount = 9;
    public const int MaxSparks = 64;
    public const int StandardSparks = 40;
    public const int SparksPerBolt = 6;
    public const double BoltRefresh = 0.05;

    /// <summary>A bolt flash brightens the corona by this much, fading over <see cref="BoltFlashDuration"/>.</summary>
    public const double BoltFlashBoost = 0.18;
    public const double BoltFlashDuration = 0.15;

    /// <summary>At most this many corona flashes per second.</summary>
    public const int MaxBoltFlashesPerSecond = 3;

    private const int SparkShades = 32;
    private static readonly Rgba[] DarkSparkShades = BuildSparkShades();

    private readonly Bolt[] _bolts = new Bolt[MaxBolts];
    private readonly ReactorPoint[] _boltPoints = new ReactorPoint[MaxBolts * BoltPointCount];
    private readonly ReactorBolt[] _boltView = new ReactorBolt[MaxBolts];
    private readonly Spark[] _sparks = new Spark[MaxSparks];
    private readonly ReactorSpark[] _sparkView = new ReactorSpark[MaxSparks];
    private readonly double[] _boltFlashTimes = new double[MaxBoltFlashesPerSecond];

    private EmissionDebt _boltDebt;
    private int _boltViewCount;
    private int _sparkCount;
    private int _boltFlashIndex;
    private double _boltFlashStart = double.NegativeInfinity;

    /// <summary>Bolts alive this frame.</summary>
    public ReadOnlySpan<ReactorBolt> Bolts => _boltView.AsSpan(0, _boltViewCount);

    /// <summary>All bolt polylines, <see cref="BoltPointCount"/> points per slot (S units from the center).</summary>
    public ReadOnlySpan<ReactorPoint> BoltPoints => _boltPoints;

    public ReadOnlySpan<ReactorSpark> Sparks => _sparkView.AsSpan(0, _sparkCount);

    /// <summary>Bolts alive at most: 3 (Max 4).</summary>
    public int BoltCap => _effects == EffectsIntensity.Max ? MaxBolts : 3;

    /// <summary>Spark pool: 40 (Max 64).</summary>
    public int SparkCap => _effects == EffectsIntensity.Max ? MaxSparks : StandardSparks;

    /// <summary>Bolts spawned since construction (tests and benchmarks).</summary>
    internal long EmittedBolts { get; private set; }

    internal long OutwardBolts { get; private set; }

    /// <summary>Corona flashes caused by bolts since construction.</summary>
    internal long BoltFlashes { get; private set; }

    /// <summary>The 9-point polyline of <paramref name="bolt"/>.</summary>
    public ReadOnlySpan<ReactorPoint> BoltPath(in ReactorBolt bolt) => _boltPoints.AsSpan(bolt.PathOffset, BoltPointCount);

    private bool LightningOn => Active && !Still && _effects >= EffectsIntensity.Standard && _tier == SpeedTier.Redline;

    private void ClearLightning()
    {
        for (var i = 0; i < MaxBolts; i++)
        {
            _bolts[i].Alive = false;
        }

        _sparkCount = 0;
        _boltViewCount = 0;
        _boltDebt.Reset();
        _boltFlashStart = double.NegativeInfinity;
    }

    private void StepLightning(double o, double dt)
    {
        for (var slot = 0; slot < MaxBolts; slot++)
        {
            ref var b = ref _bolts[slot];
            if (!b.Alive)
            {
                continue;
            }

            var age = _time - b.Start;
            if (age >= b.Life || !LightningOn)
            {
                b.Alive = false;
                continue;
            }

            var step = (int)Math.Floor(age / BoltRefresh);
            if (step != b.Step)
            {
                b.Step = step;
                b.Version++;
                BuildPath(slot);
            }
        }

        if (LightningOn)
        {
            var n = _boltDebt.Take((1.5 + 6 * o) * (_effects == EffectsIntensity.Max ? 1.6 : 1), dt);
            for (var i = 0; i < n; i++)
            {
                SpawnBolt(o);
            }
        }
        else
        {
            _boltDebt.Reset();
        }

        var drag = Math.Exp(-3 * dt);
        for (var i = 0; i < _sparkCount;)
        {
            ref var p = ref _sparks[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _sparks[i] = _sparks[--_sparkCount];
                continue;
            }

            p.Vx *= drag;
            p.Vy *= drag;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            i++;
        }
    }

    private void SpawnBolt(double o)
    {
        var slot = -1;
        var alive = 0;
        for (var i = 0; i < MaxBolts; i++)
        {
            if (_bolts[i].Alive)
            {
                alive++;
            }
            else if (slot < 0)
            {
                slot = i;
            }
        }

        if (slot < 0 || alive >= BoltCap)
        {
            return;
        }

        var theta = _random.Next() * Tau;
        var outward = o > 0.3 && _random.Next() < 0.4;
        var r1 = outward ? _random.Range(ReactorGeometry.BoltOuterMin, ReactorGeometry.BoltOuterMax) : ReactorGeometry.BoltInnerEnd;
        var theta1 = theta + _random.Range(-0.5, 0.5) * (outward ? 0.6 : 1);
        ref var b = ref _bolts[slot];
        b.Alive = true;
        b.Start = _time;
        b.Life = _random.Range(0.09, 0.18);
        b.Theta0 = theta;
        b.Theta1 = theta1;
        b.R0 = outward ? ReactorGeometry.BoltOuterStart : ReactorGeometry.BoltInnerStart;
        b.R1 = r1;
        b.Outward = outward;
        b.Seed = (uint)(_random.Next() * uint.MaxValue);
        b.Step = 0;
        b.Version++;
        BuildPath(slot);
        EmittedBolts++;
        if (outward)
        {
            OutwardBolts++;
        }

        var cap = SparkCap;
        for (var k = 0; k < SparksPerBolt && _sparkCount < cap; k++)
        {
            ref var p = ref _sparks[_sparkCount++];
            p = default;
            p.X = r1 * Math.Cos(theta1);
            p.Y = r1 * Math.Sin(theta1);
            var speed = _random.Range(0.2, 0.6);
            var direction = theta1 + _random.Range(-1.2, 1.2);
            p.Vx = Math.Cos(direction) * speed;
            p.Vy = Math.Sin(direction) * speed;
            p.Life = _random.Range(0.15, 0.4);
            p.Radius = _random.Range(0.8, 1.5) * 0.6 / 148;
        }

        // Corona flashes: at most three in any second.
        if (_time - _boltFlashTimes[_boltFlashIndex] >= 1)
        {
            _boltFlashTimes[_boltFlashIndex] = _time;
            _boltFlashIndex = (_boltFlashIndex + 1) % MaxBoltFlashesPerSecond;
            _boltFlashStart = _time;
            BoltFlashes++;
        }
    }

    /// <summary>8 segments from (r0, θ0) to (r1, θ1) with a perpendicular jitter of ±0.048 S sin(π u), fixed for 50 ms.</summary>
    private void BuildPath(int slot)
    {
        ref readonly var b = ref _bolts[slot];
        var random = new XorShift32(unchecked((int)(b.Seed ^ (uint)(b.Step + 1) * 0x9E3779B1u)));
        var offset = slot * BoltPointCount;
        for (var i = 0; i < BoltPointCount; i++)
        {
            var u = i / (double)(BoltPointCount - 1);
            var theta = b.Theta0 + (b.Theta1 - b.Theta0) * u;
            var r = b.R0 + (b.R1 - b.R0) * u;
            var jitter = i == 0 || i == BoltPointCount - 1 ? 0 : (random.Next() - 0.5) * 0.096 * Math.Sin(Math.PI * u);
            var cos = Math.Cos(theta);
            var sin = Math.Sin(theta);
            _boltPoints[offset + i] = new ReactorPoint(r * cos - sin * jitter, r * sin + cos * jitter);
        }
    }

    private void FillLightning()
    {
        var n = 0;
        for (var slot = 0; slot < MaxBolts; slot++)
        {
            ref readonly var b = ref _bolts[slot];
            if (!b.Alive)
            {
                continue;
            }

            var u = (_time - b.Start) / b.Life;
            var alpha = u < 0.2 ? 1 : Math.Max(0, 1 - (u - 0.2) / 0.8);
            _boltView[n++] = new ReactorBolt(alpha, b.Outward, slot * BoltPointCount, b.Version);
        }

        _boltViewCount = n;
        for (var i = 0; i < _sparkCount; i++)
        {
            ref readonly var p = ref _sparks[i];
            var u = Math.Clamp(p.Age / p.Life, 0, 1);
            var color = _dark ? DarkSparkShades[(int)Math.Round(u * (SparkShades - 1))] : EnergyPalette.Amber;
            _sparkView[i] = new ReactorSpark(p.X, p.Y, p.Radius, color, (1 - u) * ReadoutZone.Fade(p.X, p.Y));
        }
    }

    private static Rgba[] BuildSparkShades()
    {
        var shades = new Rgba[SparkShades];
        var tail = HeatPalette.Sample(0.9);
        for (var i = 0; i < SparkShades; i++)
        {
            shades[i] = OkLab.Mix(HeatPalette.WhiteHot, tail, i / (double)(SparkShades - 1));
        }

        return shades;
    }

    private struct Bolt
    {
        public bool Alive;
        public bool Outward;
        public double Start;
        public double Life;
        public double Theta0;
        public double Theta1;
        public double R0;
        public double R1;
        public uint Seed;
        public int Step;
        public int Version;
    }

    private struct Spark
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Age;
        public double Life;
        public double Radius;
    }
}
