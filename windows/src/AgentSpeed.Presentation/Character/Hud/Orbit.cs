using System.Globalization;

namespace AgentSpeed.Presentation.Character;

/// <summary>
/// A Kepler orbit for a floating readout (orbits.py): an ellipse in a horizontal plane through the character, seen
/// slightly from above; faster near her, slower far out; the ellipse breathes, precesses and bobs.
/// </summary>
public readonly record struct Orbit(double A, double E, double Period, double Phase, double Omega, double Height, double Tilt = 0.16,
    double Precess = 0.012, double Seed = 0)
{
    /// <summary>Solves Kepler's equation E - e sin E = mean by six Newton steps.</summary>
    public static double EccentricAnomaly(double mean, double e)
    {
        var anomaly = e < 0.8 ? mean : Math.PI;
        for (var i = 0; i < 6; i++)
        {
            anomaly -= (anomaly - e * Math.Sin(anomaly) - mean) / (1 - e * Math.Cos(anomaly));
        }

        return anomaly;
    }

    /// <summary>Screen position and depth (-1 farthest behind, 1 nearest in front) at time t around (cx, cy).</summary>
    public (double X, double Y, double Depth) At(double t, double cx, double cy)
    {
        var s = Seed;
        var a = A * (1 + 0.07 * Math.Sin(0.11 * t + s) + 0.04 * Math.Sin(0.27 * t + 2.3 * s));
        var mean = 2 * Math.PI * t / Period + Phase;
        var anomaly = EccentricAnomaly(CharacterMath.Mod(mean, 2 * Math.PI), E);
        var px = a * (Math.Cos(anomaly) - E);
        var pz = a * Math.Sqrt(1 - E * E) * Math.Sin(anomaly);
        var w = Omega + Precess * t;
        var x = px * Math.Cos(w) - pz * Math.Sin(w);
        var z = px * Math.Sin(w) + pz * Math.Cos(w);
        var bob = 0.035 * A * Math.Sin(0.37 * t + 1.7 * s) + 0.02 * A * Math.Sin(0.91 * t + s);
        var y = cy + Height + z * Tilt + bob;
        return (cx + x, y, Math.Max(-1.0, Math.Min(1.0, z / (A * (1 + E)))));
    }

    /// <summary>The slow tumble of the text, in radians.</summary>
    public double Spin(double t) => 0.055 * Math.Sin(2 * Math.PI * t / 13.0 + Seed) + 0.03 * Math.Sin(2 * Math.PI * t / 5.3 + 2 * Seed);
}

/// <summary>A 2D point that trails its target on springs and bobs on its own phase.</summary>
public struct Follow
{
    private SecondOrder _x;
    private SecondOrder _y;
    private readonly double _amplitude;
    private readonly double _period;
    private readonly double _phase;

    public Follow(double f = 1.6, double z = 0.7, double amplitude = 5.0, double period = 3.6, double phase = 0)
    {
        _x = new SecondOrder(f, z, 0);
        _y = new SecondOrder(f, z, 0);
        _amplitude = amplitude;
        _period = period;
        _phase = phase;
        Placed = false;
    }

    public bool Placed { get; set; }

    public void Snap(double tx, double ty)
    {
        _x.Snap(tx);
        _y.Snap(ty);
        Placed = true;
    }

    public (double X, double Y) Update(double dt, double t, double tx, double ty)
    {
        if (!Placed)
        {
            Snap(tx, ty);
        }

        return (_x.Update(dt, tx) + 0.35 * _amplitude * Math.Sin(2 * Math.PI * t / (_period * 1.7) + _phase),
            _y.Update(dt, ty) + _amplitude * Math.Sin(2 * Math.PI * t / _period + _phase));
    }
}

/// <summary>
/// An integer readout whose digits roll (hud.py): sampled five times a second; a changed digit slides up out of view as
/// the new one slides in over 0.12 s.
/// </summary>
public sealed class Odometer
{
    public const double Hold = 0.2;

    /// <param name="Current">The digit shown (null before the first).</param>
    /// <param name="T">0 when the digit just changed, 1 when its roll is done.</param>
    public record struct Digit(char? Current, char? Previous, double T);

    private readonly List<Digit> _digits = new(12);
    private double _since = 1.0;
    private string _text = "0";
    private int _textOf = int.MinValue;

    public double Value { get; private set; }

    public IReadOnlyList<Digit> Digits => _digits;

    public int Held { get; private set; }

    public string Update(double dt, double target)
    {
        Value += (target - Value) * Math.Min(1.0, dt * 7.0);
        _since += dt;
        if (_since >= Hold)
        {
            Held = (int)CharacterMath.RoundHalfEven(Value);
            _since = 0;
        }

        if (Held != _textOf)
        {
            _text = Held.ToString(CultureInfo.InvariantCulture);
            _textOf = Held;
        }

        var text = _text;
        var count = text.Length;
        while (_digits.Count < count)
        {
            _digits.Insert(0, new Digit(null, null, 1));
        }

        while (_digits.Count > count)
        {
            _digits.RemoveAt(0);
        }

        for (var i = 0; i < count; i++)
        {
            var d = _digits[i];
            var ch = text[i];
            if (d.Current != ch)
            {
                d.Previous = d.Current;
                d.Current = ch;
                d.T = 0;
            }

            d.T = Math.Min(1.0, d.T + dt / 0.12);
            _digits[i] = d;
        }

        return text;
    }
}
