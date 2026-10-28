using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>One character cell of the odometer: center and size in S (from the disc center, y down).</summary>
public readonly record struct LedDigitCell(double X, double Y, double Width, double Height);

/// <summary>A flash over a digit that lightning leaves: radial glow of <see cref="Radius"/> (S), peak by power, life T.</summary>
public readonly record struct LedZap(double X, double Y, double Radius, double Age, double Life, double Power)
{
    /// <summary>The fade <c>(1 − t/T)²</c>.</summary>
    public double Fade => Math.Pow(Math.Clamp(1 - Age / Life, 0, 1), 2);
}

/// <summary>
/// Digit lightning (DESIGN 2.1; prototype <c>zap</c>, <c>makeBolt</c>, <c>shapeBolt</c>, <c>jag</c>): when the odometer changes a
/// digit, bolts crackle off it. Per changed digit a cooldown, a chance and a power decide the flash and 1–3 bolts (at most
/// <see cref="MaxBolts"/> alive); each bolt is a midpoint-displaced path with one or two forks, re-shaped every 45 ms. Fixed
/// pools, deterministic for a seed.
/// </summary>
public sealed class LedLightning
{
    public const int MaxBolts = 16;
    public const int MaxZaps = 24;
    public const int MaxCells = 16;
    public const int MainDepth = 5;
    public const int ForkDepth = 4;
    public const int MainPoints = (1 << MainDepth) + 1;
    public const int ForkPoints = (1 << ForkDepth) + 1;
    public const int MaxForks = 2;
    public const double Cooldown = 0.11;
    public const double Reshape = 0.045;
    public const double MainRoughness = 0.3;
    public const double ForkRoughness = 0.35;
    public const double Decay = 0.55;

    /// <summary>A bolt reaches the LED ring at this radius (S).</summary>
    public const double RingRadius = 0.39;

    private readonly Bolt[] _bolts = new Bolt[MaxBolts];
    private readonly LedPoint[] _main = new LedPoint[MaxBolts * MainPoints];
    private readonly LedPoint[] _forks = new LedPoint[MaxBolts * MaxForks * ForkPoints];
    private readonly LedZap[] _zaps = new LedZap[MaxZaps];
    private readonly double[] _lastZap = new double[MaxCells];
    private readonly HeroRandom _random;

    public LedLightning(uint seed)
    {
        _random = new HeroRandom(seed);
        Array.Fill(_lastZap, double.NegativeInfinity);
    }

    public int BoltCount { get; private set; }

    public int ZapCount { get; private set; }

    /// <summary>Bolts spawned since construction (tests).</summary>
    public long Spawned { get; private set; }

    public ReadOnlySpan<LedZap> Zaps => _zaps.AsSpan(0, ZapCount);

    /// <summary>Chance that a changed digit crackles: <c>min(1, 0.3 + 0.3 s + 0.4 h)</c>, <c>s</c> = the digits to its right.</summary>
    public static double Chance(int significance, double h) => Math.Min(1, 0.3 + 0.3 * significance + 0.4 * h);

    /// <summary>Power <c>(0.55 + 0.22 s)(0.6 + 0.6 h + 0.6 o) · lvl</c>.</summary>
    public static double Power(int significance, double h, double o, double level) =>
        (0.55 + 0.22 * significance) * (0.6 + 0.6 * h + 0.6 * o) * level;

    /// <summary>Bolts per crackling digit: <c>1 + [h &gt; 0.55] + [redline, 60 %]</c>; the LOD halves them (rounding up).</summary>
    public static int BoltsPerDigit(double h, bool redline, bool redlineRoll, double countScale)
    {
        var n = 1 + (h > 0.55 ? 1 : 0) + (redline && redlineRoll ? 1 : 0);
        return countScale < 1 ? (n + 1) / 2 : n;
    }

    /// <summary>At most 16 bolts alive (8 below S 120).</summary>
    public static int BoltCap(double countScale) => countScale < 1 ? MaxBolts / 2 : MaxBolts;

    /// <summary>Digits to the right of <paramref name="index"/>.</summary>
    public static int Significance(string text, int index)
    {
        var count = 0;
        for (var i = index + 1; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The bolt's opacity this frame (with its flicker).</summary>
    public double Alpha(int bolt) => _bolts[bolt].Alpha;

    /// <summary>Stroke width <c>w = 0.0032 (0.7 + 0.5 p)</c> in S.</summary>
    public double Width(int bolt) => _bolts[bolt].Width;

    public int ForkCount(int bolt) => _bolts[bolt].Forks;

    public ReadOnlySpan<LedPoint> MainPath(int bolt) => _main.AsSpan(bolt * MainPoints, MainPoints);

    public ReadOnlySpan<LedPoint> ForkPath(int bolt, int fork) => _forks.AsSpan((bolt * MaxForks + fork) * ForkPoints, ForkPoints);

    public void Clear()
    {
        BoltCount = 0;
        ZapCount = 0;
        Array.Fill(_lastZap, double.NegativeInfinity);
    }

    /// <summary>
    /// The odometer changed from <paramref name="previous"/> to <paramref name="text"/>: crackle off the changed digits. A change
    /// of length changes every digit. <paramref name="force"/> skips the cooldown and the chance (developer captures).
    /// </summary>
    /// <param name="cells">The cells of <paramref name="text"/>, one per character.</param>
    public void Trigger(string text, string previous, ReadOnlySpan<LedDigitCell> cells, double now, double h, double o, bool redline,
        double level, double countScale, bool force = false)
    {
        var same = text.Length == previous.Length;
        var cap = BoltCap(countScale);
        for (var i = 0; i < text.Length && i < cells.Length && i < MaxCells; i++)
        {
            if (!char.IsAsciiDigit(text[i]) || (same && text[i] == previous[i]))
            {
                continue;
            }

            var significance = Significance(text, i);
            if (!force && (now - _lastZap[i] < Cooldown || _random.Next() > Chance(significance, h)))
            {
                continue;
            }

            _lastZap[i] = now;
            var power = Power(significance, h, o, level);
            AddZap(new LedZap(cells[i].X, cells[i].Y, cells[i].Width * 0.9, 0, 0.16 + 0.06 * power, power));
            var n = BoltsPerDigit(h, redline, redline && _random.Next() < 0.6, countScale);
            for (var k = 0; k < n && BoltCount < cap; k++)
            {
                MakeBolt(text, cells, i, power, redline);
            }
        }
    }

    /// <summary>Ages the bolts and flashes, re-shapes bolts every 45 ms and draws this frame's flicker.</summary>
    public void Step(double dt)
    {
        if (!(dt > 0))
        {
            return;
        }

        for (var i = 0; i < BoltCount;)
        {
            ref var b = ref _bolts[i];
            b.Age += dt;
            b.Regen -= dt;
            if (b.Regen <= 0)
            {
                Shape(i);
                b.Regen = Reshape;
            }

            if (b.Age >= b.Life)
            {
                RemoveBolt(i);
                continue;
            }

            i++;
        }

        for (var i = 0; i < ZapCount;)
        {
            var z = _zaps[i] with { Age = _zaps[i].Age + dt };
            if (z.Age >= z.Life)
            {
                _zaps[i] = _zaps[--ZapCount];
                continue;
            }

            _zaps[i++] = z;
        }

        for (var i = 0; i < BoltCount; i++)
        {
            ref var b = ref _bolts[i];
            b.Alpha = Math.Min(1, (1 - b.Age / b.Life) * 1.6) * (_random.Next() < 0.22 ? 0.4 : 1);
        }
    }

    private void MakeBolt(string text, ReadOnlySpan<LedDigitCell> cells, int index, double power, bool redline)
    {
        var cell = cells[index];
        var gh = cell.Height * 0.36;
        var gw = cell.Width * 0.42;
        var side = _random.Next() < 0.5 ? -1 : 1;
        var x0 = cell.X + (_random.Next() - 0.5) * gw * 1.4;
        var y0 = cell.Y + side * gh;
        var neighbor = index + (_random.Next() < 0.5 ? -1 : 1);
        var roll = _random.Next();
        double x1;
        double y1;
        if (redline && roll < 0.3)
        {
            var a = Math.Atan2(y0, x0 + (_random.Next() - 0.5) * 0.3);
            x1 = Math.Cos(a) * RingRadius;
            y1 = Math.Sin(a) * RingRadius;
        }
        else if (neighbor >= 0 && neighbor < cells.Length && neighbor < text.Length && char.IsAsciiDigit(text[neighbor]) && roll < 0.6)
        {
            x1 = cells[neighbor].X + (_random.Next() - 0.5) * gw;
            y1 = cells[neighbor].Y + side * gh * (0.6 + _random.Next() * 0.5);
        }
        else
        {
            var a = side * Math.PI / 2 + (_random.Next() - 0.5) * 1.7;
            var length = 0.05 + 0.07 * power;
            x1 = x0 + Math.Cos(a) * length;
            y1 = y0 + Math.Sin(a) * length;
        }

        var slot = BoltCount++;
        _bolts[slot] = new Bolt
        {
            X0 = x0,
            Y0 = y0,
            X1 = x1,
            Y1 = y1,
            Length = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)),
            Forks = power > 0.9 ? 2 : 1,
            Life = 0.14 + 0.1 * _random.Next() + 0.05 * power,
            Width = 0.0032 * (0.7 + 0.5 * power),
            Regen = Reshape,
            Alpha = 1,
        };
        Spawned++;
        Shape(slot);
    }

    private void Shape(int slot)
    {
        ref readonly var b = ref _bolts[slot];
        var main = _main.AsSpan(slot * MainPoints, MainPoints);
        Jag(main, b.X0, b.Y0, b.X1, b.Y1, MainRoughness, MainDepth);
        for (var f = 0; f < b.Forks; f++)
        {
            var start = main[3 + (int)Math.Floor(_random.Next() * (MainPoints - 8))];
            var a = Math.Atan2(b.Y1 - b.Y0, b.X1 - b.X0) + (_random.Next() - 0.5) * 1.9;
            var length = b.Length * (0.2 + _random.Next() * 0.3);
            Jag(_forks.AsSpan((slot * MaxForks + f) * ForkPoints, ForkPoints), start.X, start.Y, start.X + Math.Cos(a) * length,
                start.Y + Math.Sin(a) * length, ForkRoughness, ForkDepth);
        }
    }

    /// <summary>Midpoint displacement in place: <c>2^depth + 1</c> points, each level displaces by ± half the roughness, × 0.55.</summary>
    private void Jag(Span<LedPoint> points, double x0, double y0, double x1, double y1, double roughness, int depth)
    {
        var n = points.Length - 1;
        points[0] = new LedPoint((float)x0, (float)y0);
        points[n] = new LedPoint((float)x1, (float)y1);
        var displacement = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) * roughness;
        for (var level = 0; level < depth; level++)
        {
            var stride = n >> level;
            var half = stride / 2;
            for (var i = 0; i < n; i += stride)
            {
                var a = points[i];
                var b = points[i + stride];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length == 0)
                {
                    length = 1;
                }

                var offset = (_random.Next() - 0.5) * displacement;
                points[i + half] = new LedPoint((float)((a.X + b.X) / 2.0 - dy / length * offset), (float)((a.Y + b.Y) / 2.0 + dx / length * offset));
            }

            displacement *= Decay;
        }
    }

    private void AddZap(in LedZap zap)
    {
        if (ZapCount == MaxZaps)
        {
            Array.Copy(_zaps, 1, _zaps, 0, MaxZaps - 1);
            ZapCount--;
        }

        _zaps[ZapCount++] = zap;
    }

    private void RemoveBolt(int index)
    {
        BoltCount--;
        for (var i = index; i < BoltCount; i++)
        {
            _bolts[i] = _bolts[i + 1];
            Array.Copy(_main, (i + 1) * MainPoints, _main, i * MainPoints, MainPoints);
            Array.Copy(_forks, (i + 1) * MaxForks * ForkPoints, _forks, i * MaxForks * ForkPoints, MaxForks * ForkPoints);
        }
    }

    private struct Bolt
    {
        public double X0;
        public double Y0;
        public double X1;
        public double Y1;
        public double Length;
        public int Forks;
        public double Age;
        public double Life;
        public double Width;
        public double Regen;
        public double Alpha;
    }
}
