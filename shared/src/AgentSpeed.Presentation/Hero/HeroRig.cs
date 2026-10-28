using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Hero;

/// <summary>How a generated limb is painted: far limbs at 42 % rider opacity, near limbs as riders, pedals as cranks.</summary>
public enum LimbInk : byte
{
    Far,
    Near,
    Crank,
}

/// <summary>One generated polyline; its point buffer is reused from frame to frame.</summary>
public sealed class Limb
{
    private readonly PointD[] _points = new PointD[4];

    public LimbInk Ink { get; internal set; }

    public int Count { get; private set; }

    public ReadOnlySpan<PointD> Points => _points.AsSpan(0, Count);

    public PointD this[int index] => _points[index];

    internal void Set(LimbInk ink, PointD a, PointD b)
    {
        Ink = ink;
        _points[0] = a;
        _points[1] = b;
        Count = 2;
    }

    internal void Set(LimbInk ink, PointD a, PointD b, PointD c)
    {
        Set(ink, a, b);
        _points[2] = c;
        Count = 3;
    }

    internal void Set(LimbInk ink, PointD a, PointD b, PointD c, PointD d)
    {
        Set(ink, a, b, c);
        _points[3] = d;
        Count = 4;
    }
}

/// <summary>
/// A rig pose in canvas units: <see cref="Limbs"/> in draw order (the first <see cref="FarCount"/> go behind the parts,
/// the rest in front), the walker's head, and the offset the pedalling rider's own parts follow.
/// </summary>
public sealed class RigPose
{
    public const int MaxLimbs = 5;

    private readonly Limb[] _limbs = [new(), new(), new(), new(), new()];

    public int FarCount { get; private set; }

    public int Count { get; private set; }

    public Limb this[int index] => _limbs[index];

    public bool HasHead { get; private set; }

    public CircleD Head { get; private set; }

    /// <summary>Hip offset (stand-up shift, bob) that the rider's torso, arm and head follow (pedal rig).</summary>
    public PointD RiderOffset { get; private set; }

    /// <summary>
    /// The walker (DESIGN 4c "Walker rig"). <paramref name="stand"/> blends from walking (0) to the standing pose (1).
    /// </summary>
    public void Walk(WalkRigSpec rig, double distance, double ground, double stand)
    {
        var p = Fraction(distance / rig.Cycle);
        var stance = rig.Stance;
        var bob = -rig.Bob * Math.Cos(4 * Math.PI * (p - stance / 2)) * (1 - stand);
        var standHip = ground - 0.6 - (rig.Thigh + rig.Shin) * 0.97;
        var hip = new PointD(rig.Hip.X, HeroMath.Lerp(rig.Hip.Y + bob, standHip, stand));
        var shoulder = new PointD(hip.X + rig.Torso.X, hip.Y + rig.Torso.Y);

        Leg(_limbs[0], LimbInk.Far, rig, hip, Fraction(p + 0.5), -1.6, ground, stand);
        Arm(_limbs[1], LimbInk.Far, rig, shoulder, Fraction(p + 0.5), -4, stand);
        _limbs[2].Set(LimbInk.Near, hip, shoulder);
        Leg(_limbs[3], LimbInk.Near, rig, hip, p, 1.6, ground, stand);
        Arm(_limbs[4], LimbInk.Near, rig, shoulder, p, 4, stand);
        FarCount = 2;
        Count = 5;
        HasHead = true;
        Head = new CircleD(shoulder.X + rig.Head.X, shoulder.Y + rig.Head.Y, rig.Head.Radius);
        RiderOffset = default;
    }

    /// <summary>
    /// The pedalling rider (DESIGN 4c "Pedal rig"): crank angle <c>φ = D / crank.radius</c>; <paramref name="lift"/> is the
    /// start-up stand on the pedals (hip up by it and forward by 0.6 of it).
    /// </summary>
    public void Pedal(PedalRigSpec rig, double distance, double lift)
    {
        var phi = distance / rig.CrankRadius;
        var sin = Math.Sin(phi);
        var cos = Math.Cos(phi);
        var nearPedal = new PointD(rig.CrankPivot.X - rig.CrankLength * sin, rig.CrankPivot.Y + rig.CrankLength * cos);
        var farPedal = new PointD(rig.CrankPivot.X + rig.CrankLength * sin, rig.CrankPivot.Y - rig.CrankLength * cos);
        var bob = rig.Bob * Math.Cos(2 * phi) - lift;
        var shift = lift * 0.6;
        var hip = new PointD(rig.Hip.X + shift, rig.Hip.Y + bob);
        var half = rig.Pedal / 2;

        _limbs[0].Set(LimbInk.Far, hip, Knee(hip, farPedal, rig.Thigh, rig.Shin), farPedal);
        _limbs[1].Set(LimbInk.Far, new PointD(farPedal.X - half, farPedal.Y), new PointD(farPedal.X + half, farPedal.Y));
        _limbs[2].Set(LimbInk.Crank, new PointD(nearPedal.X - half, nearPedal.Y), new PointD(nearPedal.X + half, nearPedal.Y));
        _limbs[3].Set(LimbInk.Near, hip, Knee(hip, nearPedal, rig.Thigh, rig.Shin), nearPedal);
        FarCount = 2;
        Count = 4;
        HasHead = false;
        Head = default;
        RiderOffset = new PointD(shift, bob);
    }

    /// <summary>Two-bone IK; the knee (or elbow) lies on the +x side of the hip–foot line (cross &lt; 0, y down).</summary>
    public static PointD Knee(PointD hip, PointD foot, double l1, double l2)
    {
        var dx = foot.X - hip.X;
        var dy = foot.Y - hip.Y;
        var d = Math.Sqrt(dx * dx + dy * dy);
        d = Math.Min(Math.Max(Math.Max(d, Math.Abs(l1 - l2) + 1e-3), 1e-3), l1 + l2 - 1e-3);
        var baseAngle = Math.Atan2(dy, dx);
        var alpha = Math.Acos(Math.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1));
        for (var sign = 1; sign >= -1; sign -= 2)
        {
            var a = baseAngle + sign * alpha;
            var knee = new PointD(hip.X + l1 * Math.Cos(a), hip.Y + l1 * Math.Sin(a));
            if (dx * (knee.Y - hip.Y) - dy * (knee.X - hip.X) < 0)
            {
                return knee;
            }
        }

        return new PointD(hip.X, hip.Y + l1);
    }

    private static double Fraction(double x) => (x % 1 + 1) % 1;

    private static void Leg(Limb limb, LimbInk ink, WalkRigSpec rig, PointD hip, double phase, double spread, double ground, double stand)
    {
        var a = rig.Cycle * rig.Stance / 2;
        double fx;
        double fy;
        if (phase < rig.Stance)
        {
            fx = hip.X + a - 2 * a * (phase / rig.Stance);
            fy = ground - 0.6;
        }
        else
        {
            var s = (phase - rig.Stance) / (1 - rig.Stance);
            fx = hip.X - a + 2 * a * HeroMath.Smooth(s);
            fy = ground - 0.6 - rig.Lift * Math.Sin(Math.PI * s);
        }

        var foot = new PointD(HeroMath.Lerp(fx, hip.X + spread, stand), HeroMath.Lerp(fy, ground - 0.6, stand));
        limb.Set(ink, hip, Knee(hip, foot, rig.Thigh, rig.Shin), foot, new PointD(foot.X + rig.Foot, foot.Y));
    }

    private static void Arm(Limb limb, LimbInk ink, WalkRigSpec rig, PointD shoulder, double phase, double rest, double stand)
    {
        var swing = -rig.ArmSwing * Math.Cos(2 * Math.PI * phase);
        var theta = HeroMath.Lerp(swing, rest, stand) * Math.PI / 180;
        var elbow = new PointD(shoulder.X + rig.UpperArm * Math.Sin(theta), shoulder.Y + rig.UpperArm * Math.Cos(theta));
        var t2 = theta + rig.Elbow * HeroMath.Lerp(1, 0.5, stand) * Math.PI / 180;
        limb.Set(ink, shoulder, elbow, new PointD(elbow.X + rig.Forearm * Math.Sin(t2), elbow.Y + rig.Forearm * Math.Cos(t2)));
    }
}
