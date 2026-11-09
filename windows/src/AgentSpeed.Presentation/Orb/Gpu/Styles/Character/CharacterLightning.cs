using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// Lightning on the <c>character</c> speed (DESIGN 2a.6): when digits of the readout change, an electric crackle runs
/// around each changed digit, and when more than the last digit changes an arc also jumps over the changed digits. Bolts
/// flicker (a new shape 30 times a second), fade over their life and make the changed digits flash. Strength follows the
/// speed's heat. Paths are built in the speed body's local points (digit cells centered on y = 0) into a reused buffer.
/// </summary>
public sealed class CharacterLightning
{
    public enum Strength
    {
        Off,
        FlashOnly,
        Soft,
        Standard,
        Max,
    }

    /// <param name="Low">Changed digits, counted from the last one (0 = units): low…high.</param>
    public readonly record struct Strike(int Low, int High, double Start, double Life, double Power, ulong Seed, bool Bridge);

    private readonly List<Strike> _strikes = new(8);
    private readonly List<(double X, double Y)> _path = new(16);
    private readonly List<char> _lastText = new(8);
    private ulong _count;

    public IReadOnlyList<Strike> Strikes => _strikes;

    public double Time { get; private set; }

    /// <summary>Path points of the last <see cref="Bolt"/> call (local points).</summary>
    public IReadOnlyList<(double X, double Y)> Path => _path;

    public bool IsActive => _strikes.Count > 0;

    /// <summary>Advances time and starts strikes for the digits that differ from the last call (most significant first).</summary>
    public void Update(double dt, IReadOnlyList<Odometer.Digit> digits, double heat, Strength strength)
    {
        ArgumentNullException.ThrowIfNull(digits);
        Time += dt;
        for (var i = _strikes.Count - 1; i >= 0; i--)
        {
            if (Time - _strikes[i].Start >= _strikes[i].Life)
            {
                _strikes.RemoveAt(i);
            }
        }
        var low = int.MaxValue;
        var high = -1;
        var n = digits.Count;
        for (var i = 0; i < n; i++)
        {
            var fromRight = n - 1 - i;
            var now = digits[i].Current;
            char? before = fromRight < _lastText.Count ? _lastText[_lastText.Count - 1 - fromRight] : null;
            if (now is not null && before != now && (before is not null || _lastText.Count > 0))
            {
                low = Math.Min(low, fromRight);
                high = Math.Max(high, fromRight);
            }
        }

        _lastText.Clear();
        foreach (var digit in digits)
        {
            if (digit.Current is { } c)
            {
                _lastText.Add(c);
            }
        }

        if (high < 0 || strength == Strength.Off)
        {
            return;
        }

        unchecked
        {
            _count++;
            var power = 0.35 + 0.65 * Math.Clamp(heat, 0, 1);
            var bridge = high > low && strength is Strength.Standard or Strength.Max;
            var life = (high > 0 ? 0.3 : 0.2) * (strength == Strength.Soft ? 0.7 : 1);
            if (_strikes.Count >= 8)
            {
                _strikes.RemoveAt(0);
            }

            _strikes.Add(new Strike(low, high, Time, life, power, 0x5EED_0000UL + _count * 0x9E37_79B9UL, bridge));
        }
    }

    /// <summary>0…1: how much the digit <paramref name="fromRight"/> flashes now.</summary>
    public double Flash(int fromRight)
    {
        var best = 0.0;
        foreach (var strike in _strikes)
        {
            if (fromRight >= strike.Low && fromRight <= strike.High)
            {
                best = Math.Max(best, Envelope(strike) * strike.Power);
            }
        }

        return best;
    }

    /// <summary>The strike's alpha now: an instant flash, a flicker per 1/30 s step and a fade over its life.</summary>
    public double Envelope(in Strike strike)
    {
        var age = Time - strike.Start;
        if (age < 0 || age >= strike.Life)
        {
            return 0;
        }

        var rng = new Rng(unchecked(strike.Seed ^ ((ulong)(age * 30) * 0xBF58_476D_1CE4_E5B9UL)));
        var flicker = 0.55 + 0.45 * rng.Random();
        return flicker * Math.Pow(1 - age / strike.Life, 1.4);
    }

    /// <summary>How many bolts a strike draws: one crackle per changed digit (at most three), plus the arc over them.</summary>
    public static int BoltCount(in Strike strike, Strength strength)
    {
        if (strength is Strength.Off or Strength.FlashOnly)
        {
            return 0;
        }

        var crackles = Math.Min(strike.High - strike.Low + 1, 3) + (strength == Strength.Max && strike.Power > 0.7 ? 1 : 0);
        return crackles + (strike.Bridge ? 1 : 0);
    }

    /// <summary>
    /// Builds bolt <paramref name="index"/> of a strike into <see cref="Path"/>, for a readout of <paramref name="digitCount"/>
    /// digits <paramref name="advance"/> wide with glyphs <paramref name="cap"/> tall. False when there is no such bolt.
    /// </summary>
    public bool Bolt(in Strike strike, int index, Strength strength, int digitCount, double advance, double cap)
    {
        _path.Clear();
        if (index >= BoltCount(strike, strength) || digitCount <= 0)
        {
            return false;
        }

        var age = Time - strike.Start;
        var rng = new Rng(unchecked((strike.Seed + (ulong)(index + 1) * 0x94D0_49BB_1331_11EBUL) ^ ((ulong)(Math.Max(0, age) * 30) * 0xD6E8_FEB8_6659_FD93UL)));
        double Center(int fromRight)
        {
            var i = Math.Clamp(digitCount - 1 - fromRight, 0, digitCount - 1);
            return -digitCount * advance / 2 + advance * (i + 0.5);
        }

        var crackles = BoltCount(strike, strength) - (strike.Bridge ? 1 : 0);
        if (index < crackles)
        {
            var span = strike.High - strike.Low + 1;
            var fromRight = strike.Low + (index % span);
            var cx = Center(fromRight);
            var rx = advance * 0.6;
            var ry = cap * 0.8;
            var start = rng.Uniform(0, 2 * Math.PI);
            var sweep = rng.Uniform(1.2, 2.2) * (rng.Random() < 0.5 ? -1 : 1);
            const int points = 9;
            for (var k = 0; k < points; k++)
            {
                var f = (double)k / (points - 1);
                var a = start + sweep * f;
                var jag = k == 0 || k == points - 1 ? 0 : rng.Uniform(-0.2, 0.2);
                var r = 1 + jag;
                _path.Add((cx + Math.Cos(a) * rx * r, Math.Sin(a) * ry * r));
            }

            return true;
        }

        var left = Center(strike.High) - advance * 0.5;
        var right = Center(strike.Low) + advance * 0.5;
        var lift = cap * rng.Uniform(0.95, 1.2);
        const int count = 12;
        for (var k = 0; k < count; k++)
        {
            var f = (double)k / (count - 1);
            var x = left + (right - left) * f;
            var y = -cap * 0.2 - 4 * f * (1 - f) * lift;
            var jag = k == 0 || k == count - 1 ? 0 : rng.Uniform(-0.22, 0.22) * cap;
            _path.Add((x + rng.Uniform(-0.08, 0.08) * advance, y + jag));
        }

        return true;
    }
}
