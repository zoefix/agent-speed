namespace AgentSpeed.Presentation.Character;

/// <summary>The effect tiers of METRICS 6 as the character's speed bands, in order.</summary>
public enum SpeedBand
{
    Idle,
    Cruise,
    Boost,
    Overdrive,
    Redline,
}

public static class SpeedBands
{
    public static IReadOnlyList<SpeedBand> All { get; } = [SpeedBand.Idle, SpeedBand.Cruise, SpeedBand.Boost, SpeedBand.Overdrive, SpeedBand.Redline];

    /// <summary>The band of a speed level 0...13.</summary>
    public static SpeedBand OfLevel(int level) => level >= 12 ? SpeedBand.Redline
        : level >= 9 ? SpeedBand.Overdrive
        : level >= 5 ? SpeedBand.Boost
        : level >= 1 ? SpeedBand.Cruise
        : SpeedBand.Idle;

    public static string Key(this SpeedBand band) => band switch
    {
        SpeedBand.Cruise => "cruise",
        SpeedBand.Boost => "boost",
        SpeedBand.Overdrive => "overdrive",
        SpeedBand.Redline => "redline",
        _ => "idle",
    };

    public static SpeedBand? Parse(string? key) => key switch
    {
        "idle" => SpeedBand.Idle,
        "cruise" => SpeedBand.Cruise,
        "boost" => SpeedBand.Boost,
        "overdrive" => SpeedBand.Overdrive,
        "redline" => SpeedBand.Redline,
        _ => null,
    };

    internal static double Bpm(this SpeedBand band) => band switch
    {
        SpeedBand.Cruise => 84,
        SpeedBand.Boost => 104,
        SpeedBand.Overdrive => 124,
        SpeedBand.Redline => 140,
        _ => 56,
    };
}

/// <summary>The beat clock: its tempo eases toward the band's, scaled by the character's tempo.</summary>
internal struct Beat
{
    private readonly double _tempo;

    public Beat(double tempo)
    {
        _tempo = tempo;
        Bpm = SpeedBand.Idle.Bpm() * tempo;
        Phase = 0;
    }

    public double Bpm { get; private set; }

    public double Phase { get; private set; }

    public void Update(double dt, SpeedBand band)
    {
        Bpm += (band.Bpm() * _tempo - Bpm) * Math.Min(1.0, dt * 1.2);
        Phase += dt * Bpm / 60;
    }
}

/// <summary>Body grooves per band on the beat clock: posture, weight shifts, bounce and arm swing (motions/rhythm.py).</summary>
internal static class Groove
{
    /// <summary>0 on the beat, 1 halfway to the next.</summary>
    private static double Up(double b) => Math.Abs(Math.Sin(Math.PI * b));

    /// <summary>1 right on the beat, quickly falling off.</summary>
    private static double Hit(double b) => Math.Pow(1 - Math.Abs(Math.Sin(Math.PI * b)), 4.0);

    public static void Emit(SpeedBand band, double b, double t, Slots s, Slots.Side o, Slots.Side k, bool rest, List<(int Slot, double Value)> output)
    {
        output.Clear();
        switch (band)
        {
            case SpeedBand.Idle when rest:
            {
                var shift = Math.Sin(2 * Math.PI * t / 7.5);
                output.Add((s.AngleY, -0.08 + 0.05 * Math.Sin(t * 0.9)));
                output.Add((s.AngleZ, 0.1 * Math.Sin(t * 0.5)));
                output.Add((s.AngleX, -0.15));
                output.Add((s.EyeBallX, 0.3 * Math.Sin(t * 0.7)));
                output.Add((s.EyeBallY, -0.1));
                output.Add((k.Arm, 0.06));
                output.Add((s.Hip, 0.25 * shift));
                output.Add((s.BodyAngleZ, -0.12 * shift));
                output.Add((s.Lean, 0.04 * shift));
                break;
            }

            case SpeedBand.Idle:
            {
                var shift = Math.Sin(2 * Math.PI * t / 7.5);
                output.Add((s.AngleY, -0.55 + 0.05 * Math.Sin(t * 0.9)));
                output.Add((s.AngleZ, -0.35 + 0.05 * Math.Sin(t * 0.6)));
                output.Add((s.AngleX, -0.22));
                output.Add((s.EyeBallX, -0.4 + 0.25 * Math.Sin(t * 1.3)));
                output.Add((s.EyeBallY, -0.7));
                output.Add((k.Arm, 0.15));
                output.Add((k.Forearm, -0.1));
                output.Add((s.Hip, 0.28 * shift));
                output.Add((s.BodyAngleZ, -0.15 * shift));
                output.Add((s.Lean, 0.05 * shift));
                break;
            }

            case SpeedBand.Cruise:
            {
                var sn = Math.Sin(Math.PI * b);
                output.Add((s.Hip, 0.35 * sn));
                output.Add((s.BodyAngleZ, -0.22 * sn));
                output.Add((s.AngleZ, 0.12 + 0.14 * Math.Sin(Math.PI * b + 0.7)));
                output.Add((s.AngleX, -0.3));
                output.Add((s.AngleY, 0.04 * Math.Cos(2 * Math.PI * b)));
                output.Add((s.EyeBallX, -0.35));
                output.Add((o.Arm, 0.08 * sn));
                output.Add((k.Arm, 0.04 * sn));
                output.Add((s.SkirtFlare, 0.05));
                break;
            }

            case SpeedBand.Boost:
            {
                var sn = Math.Sin(Math.PI * b);
                var u = Up(b);
                var h = Hit(b);
                output.Add((s.PosY, 0.035 * u));
                output.Add((s.Squash, 0.1 * u - 0.22 * h));
                output.Add((s.Hip, 0.45 * sn));
                output.Add((s.BodyAngleZ, -0.3 * sn));
                output.Add((s.AngleZ, -0.25 * sn));
                output.Add((s.AngleY, 0.08 - 0.12 * h));
                output.Add((s.AngleX, -0.25));
                output.Add((o.Arm, 0.05 + 0.25 * sn));
                output.Add((o.Forearm, -0.12 - 0.1 * sn));
                output.Add((k.Arm, 0.12 * sn));
                output.Add((o.Shoulder, 0.25 * sn));
                output.Add((k.Shoulder, -0.25 * sn));
                output.Add((s.SkirtFlare, 0.12 + 0.12 * u));
                output.Add((s.FootPoint, 0.3 * u));
                break;
            }

            case SpeedBand.Overdrive:
            {
                var sn = Math.Sin(Math.PI * b);
                var u = Up(b);
                var h = Hit(b);
                output.Add((s.PosY, 0.06 * u));
                output.Add((s.Squash, 0.15 * u - 0.35 * h));
                output.Add((s.Hip, 0.5 * sn));
                output.Add((s.BodyAngleZ, -0.4 * sn));
                output.Add((s.Lean, 0.12 * sn));
                output.Add((s.AngleZ, -0.35 * sn));
                output.Add((s.AngleY, 0.2 - 0.15 * h));
                output.Add((s.AngleX, -0.35));
                output.Add((o.Arm, 0.1 + 0.35 * sn));
                output.Add((o.Forearm, -0.3 - 0.2 * sn));
                output.Add((k.Arm, 0.08 + 0.2 * sn));
                output.Add((o.Shoulder, 0.35 * sn));
                output.Add((k.Shoulder, -0.35 * sn));
                output.Add((s.SkirtFlare, 0.25 + 0.15 * u));
                output.Add((s.FootPoint, 0.5 * u));
                output.Add((s.FootLift, 0.25 * u));
                break;
            }

            case SpeedBand.Redline:
            {
                var sn = Math.Sin(Math.PI * b);
                var u = Up(b);
                var h = Hit(b);
                output.Add((s.PosY, 0.1 * u));
                output.Add((s.Squash, 0.2 * u - 0.45 * h));
                output.Add((s.Hip, 0.35 * sn));
                output.Add((s.BodyAngleZ, -0.35 * sn));
                output.Add((s.Lean, 0.1 * sn));
                output.Add((s.AngleZ, 0.35 * sn));
                output.Add((s.AngleY, 0.35 - 0.2 * h));
                output.Add((s.AngleX, -0.45));
                output.Add((o.Arm, 0.4 + 0.12 * sn));
                output.Add((o.Forearm, -0.55 - 0.1 * sn));
                output.Add((k.Arm, 0.25 + 0.08 * sn));
                output.Add((o.Shoulder, 0.35 + 0.2 * sn));
                output.Add((k.Shoulder, 0.25 - 0.2 * sn));
                output.Add((s.SkirtFlare, 0.4 + 0.25 * u));
                output.Add((s.FootPoint, 0.8 * u));
                output.Add((s.FootLift, 0.4 * u));
                break;
            }
        }
    }
}
