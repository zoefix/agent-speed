namespace AgentSpeed.Presentation.Character;

/// <summary>
/// A second-order follower: y + k1 y' + k2 y'' = x + k3 x', k1 = z / (pi f), k2 = 1 / (2 pi f)^2, k3 = r z / (2 pi f);
/// k2 is raised when needed to keep the semi-implicit Euler step stable.
/// </summary>
public struct SecondOrder
{
    private readonly double _k1;
    private readonly double _k2;
    private readonly double _k3;
    private double _xp;
    private double _yd;

    public SecondOrder(double f, double z, double r, double x0 = 0)
    {
        _k1 = z / (Math.PI * f);
        var w = 2 * Math.PI * f;
        _k2 = 1 / (w * w);
        _k3 = r * z / (2 * Math.PI * f);
        _xp = x0;
        Y = x0;
        _yd = 0;
    }

    public SecondOrder(SpringTuning tuning, double x0 = 0)
        : this(tuning.Frequency, tuning.Damping, tuning.Response, x0)
    {
    }

    public double Y { get; private set; }

    public void Snap(double x)
    {
        _xp = x;
        Y = x;
        _yd = 0;
    }

    public double Update(double dt, double x)
    {
        if (!(dt > 0))
        {
            return Y;
        }

        var xd = (x - _xp) / dt;
        _xp = x;
        var k2 = Math.Max(Math.Max(_k2, dt * dt / 2 + dt * _k1 / 2), dt * _k1);
        Y += dt * _yd;
        _yd += dt * (x + _k3 * xd - Y - _k1 * _yd) / k2;
        return Y;
    }
}

public readonly record struct SpringTuning(double Frequency, double Damping, double Response)
{
    /// <summary>(prefix or exact id, tuning); the first match wins, null leaves a parameter raw. "@" matches any id holding it.</summary>
    private static readonly (string Key, SpringTuning? Spec)[] Table =
    [
        ("SkirtFlare", new(2.0, 0.45, 0)),
        ("TailSwish", new(2.4, 0.5, 0)), ("Tail", null), ("WingFlap", new(3.0, 0.5, 0)),
        ("EarTwitch", new(7.0, 0.4, 0)),
        ("Var.", null), ("Mouth.", null), ("EyeOpen.", null), ("EyeBall", null), ("Breath", null), ("Wind", null),
        ("Spin", null), ("PosY", null), ("Squash", null), ("Hair", null), ("Skirt", null),
        ("Lift.", new(6.0, 0.85, 0)), ("Bend.", new(6.0, 0.85, 0)),
        ("@", new(3.2, 0.55, 0)),
        ("Finger.", new(5.0, 0.55, 0)), ("Spread.", new(3.5, 0.6, 0)),
        ("Wrist.", new(3.2, 0.55, 0)),
        ("Arm.", new(2.6, 0.55, 0)), ("Forearm.", new(2.8, 0.5, 0)),
        ("Shoulder.", new(3.0, 0.6, 0)),
        ("Angle", new(2.2, 0.72, 0)), ("BodyAngleZ", new(1.8, 0.65, 0)),
        ("BodySway", new(1.8, 0.65, 0)),
        ("Hip", new(1.9, 0.62, 0)), ("Lean", new(2.0, 0.6, 0)), ("PosX", new(2.4, 0.8, 0)),
        ("Foot", new(6.0, 0.7, 0)),
    ];

    /// <summary>The spring a parameter rides on, or null when it stays raw.</summary>
    public static SpringTuning? Of(string id)
    {
        foreach (var (key, spec) in Table)
        {
            if (key == "@" ? id.Contains('@', StringComparison.Ordinal) : id.StartsWith(key, StringComparison.Ordinal))
            {
                return spec;
            }
        }

        return null;
    }
}
