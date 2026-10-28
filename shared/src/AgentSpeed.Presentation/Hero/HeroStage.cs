using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Hero;

/// <summary>What the vehicle layer may do (DESIGN 4c "Effects intensity", "Accessibility and performance").</summary>
/// <param name="Requested">The user's Effects setting (the vehicle layer is not capped at Soft like the hero's ring).</param>
public readonly record struct HeroOptions(
    EffectsIntensity Requested,
    bool ReduceMotion = false,
    bool HighContrast = false,
    bool Dark = true,
    bool IncreasedContrast = false)
{
    /// <summary>Reduce Motion and high-contrast themes cap the layer at Soft.</summary>
    public EffectsIntensity Level => (ReduceMotion || HighContrast) && Requested > EffectsIntensity.Soft ? EffectsIntensity.Soft : Requested;

    /// <summary>Anything moves at all (Soft and up without Reduce Motion).</summary>
    public bool Animated => Level >= EffectsIntensity.Soft && !ReduceMotion;
}

public enum ActorTransition
{
    Stay,
    Up,
    Down,
    Fade,
    Wake,
    Cut,
}

/// <param name="BaseOffset">Where an interrupted actor stood (along its heading) when it became the outgoing one.</param>
public readonly record struct HeroActor(int Vehicle, double Start, ActorTransition Transition, int Levels = 1, double BaseOffset = 0,
    double BaseAlpha = 1);

/// <summary>Offset of an actor along its heading (canvas units), opacity, and the speed its motion ghosts trail at.</summary>
public readonly record struct ActorPlacement(double X, double Y, double Alpha, double Ghost, double HeadingX, double HeadingY, double Along);

/// <summary>Start-up choreography modifiers (DESIGN 4c "Start-up"); <see cref="None"/> leaves an actor untouched.</summary>
public readonly record struct StartupFx(
    double Lurch,
    double Pitch,
    double Lift,
    double HipLift,
    double Lights,
    double Flame,
    double Contrail,
    double Shock,
    double Hover,
    double Shake,
    double Glow)
{
    public static StartupFx None { get; } = new(0, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0);
}

/// <summary>
/// Where an actor's parts go this frame: unsprung parts translate by (<see cref="OffsetX"/>, <see cref="OffsetY"/>);
/// sprung parts also move by <see cref="SprungY"/> − OffsetY and rotate by <see cref="PitchDegrees"/> about
/// <see cref="Pivot"/>.
/// </summary>
public readonly record struct ActorMotion(double OffsetX, double OffsetY, double Bob, double Float, double Hover, double PitchDegrees,
    PointD Pivot)
{
    public double SprungY => OffsetY + Bob + Float - Hover;
}

/// <summary>The upgrade flash: radius 8 → 36 units and its opacity this frame.</summary>
public readonly record struct HeroFlash(PointD Center, double Radius, double Alpha);

/// <summary>The idle light band crossing the parked vehicle: its center x (canvas units) and strength.</summary>
public readonly record struct HeroSweep(double X, double Amount);

/// <summary>
/// Platform-neutral state of the dashboard hero vehicle (DESIGN 4c): the speed model, the title transitions, start-up,
/// idle and parking, and the particle and speed-line pools. Deterministic for a given sequence of inputs.
/// </summary>
public sealed class HeroStage
{
    public const double MaxStep = 0.1;
    public const double GroundSettle = 0.9;
    public const double WakeSettle = 0.5;
    public const double PreviousLifetime = 0.8;
    public const double StartupLifetime = 1.4;
    public const double FlashLifetime = 0.5;
    public const double SweepInterval = 6;
    public const double SweepDuration = 0.9;

    /// <summary>The light band starts this long into the gauge's idle sweep, so both cross the top together.</summary>
    public const double SweepDelay = 0.63;

    /// <summary>Parked (breathing, light band): about 15 fps.</summary>
    public const double ParkedFrameRate = 15;

    private readonly HeroRandom _random;
    private CriticalSpring _heat = new(0.6);
    private CriticalSpring _overdrive = new(0.6);
    private SettleSpring _ground = new(GroundSettle);
    private SettleSpring _wake = new(WakeSettle);
    private double _lastNow = double.NaN;
    private double _targetHeat;
    private double _targetOverdrive;
    private double _parkedAt;
    private double _bobPhase;
    private double _bobPhase2;
    private StartupState? _startup;
    private (double Start, double Strength)? _flash;
    private PaintKey _paintKey = new(-1, -1, -1, false, false);
    private (Rgba Color, Rgba Hot, Rgba Core) _paintCache;

    public HeroStage(VehicleArt art, string? parkedKey = null, uint seed = 7)
    {
        Art = art ?? throw new ArgumentNullException(nameof(art));
        _random = new HeroRandom(seed);
        var index = art.IndexOf(VehicleArt.NormalizeKey(parkedKey));
        if (index < 0)
        {
            index = art.IdleFallback;
        }

        Current = new HeroActor(index, double.NegativeInfinity, ActorTransition.Stay);
        Distance = art.Vehicles[index].Motion.Rest;
        Stand = 1;
        Parked = true;
        _parkedAt = double.NegativeInfinity;
    }

    public VehicleArt Art { get; }

    public HeroOptions Options { get; private set; } = new(EffectsIntensity.Standard);

    public EffectsIntensity Level => Options.Level;

    public bool Animated => Options.Animated;

    public double Time { get; private set; }

    public double Redline { get; private set; } = HeatMath.DefaultRedline;

    /// <summary>The title the vehicle shows (the same event as the label).</summary>
    public SpeedTitle Title { get; private set; } = SpeedTitle.Idle;

    public SpeedTier Tier { get; private set; } = SpeedTier.Idle;

    public double Heat => Math.Clamp(_heat.Value, 0, 1);

    public double Overdrive => Math.Clamp(_overdrive.Value, 0, 1);

    /// <summary>Smoothed value <c>v_s</c> recovered from the heat and overdrive springs.</summary>
    public double SmoothedValue { get; private set; }

    /// <summary>Band progress <c>b</c> inside the displayed title.</summary>
    public double Band { get; private set; }

    /// <summary>Coasting factor <c>c</c>: the vehicle slows when the agents stop, while the title holds.</summary>
    public double Coast { get; private set; }

    /// <summary>Ground speed <c>G</c> in units/s.</summary>
    public double Ground => _ground.Value;

    /// <summary><c>a = dG/dt</c>.</summary>
    public double Acceleration => _ground.Velocity;

    public double GroundTarget { get; private set; }

    /// <summary>Travelled distance <c>D</c> (units): wheels, cranks, gait and scenery all derive from it.</summary>
    public double Distance { get; private set; }

    /// <summary>0 = parked (slate), 1 = awake (heat colors).</summary>
    public double Wake => Math.Clamp(_wake.Value, 0, 1);

    /// <summary>Walker: 0 walking, 1 standing.</summary>
    public double Stand { get; private set; }

    public bool Parked { get; private set; }

    public HeroActor Current { get; private set; }

    public HeroActor? Previous { get; private set; }

    public Vehicle CurrentVehicle => Art.Vehicles[Current.Vehicle];

    /// <summary>Key of the vehicle on stage (parked or moving).</summary>
    public string CurrentKey => CurrentVehicle.Key;

    public HeroParticlePool Particles { get; } = new();

    public HeroStreakPool Streaks { get; } = new();

    /// <summary>Start of the gauge's current idle sweep, so the vehicle's light band runs in step; NaN uses its own cadence.</summary>
    public double IdleSweepOrigin { get; set; } = double.NaN;

    public bool StartupRunning => _startup is not null;

    /// <summary>Advances the stage to <paramref name="now"/> (seconds).</summary>
    /// <param name="snap">Jump to the current state without transitions (the flyout was just opened).</param>
    public void Update(double now, double value, double redline, SpeedTier tier, SpeedTitle title, HeroOptions options, bool snap = false)
    {
        var first = double.IsNaN(_lastNow);
        var dt = first ? 0 : Math.Clamp(now - _lastNow, 0, MaxStep);
        _lastNow = now;
        Time = now;
        Options = options;
        Tier = tier;
        Redline = double.IsFinite(redline) && redline > 0 ? redline : HeatMath.DefaultRedline;
        value = double.IsFinite(value) && value > 0 ? value : 0;

        _targetHeat = HeatMath.Heat(value, Redline);
        _targetOverdrive = HeatMath.Overdrive(value, Redline);
        if (snap || first)
        {
            _heat.Snap(_targetHeat);
            _overdrive.Snap(_targetOverdrive);
        }
        else
        {
            _heat.Step(_targetHeat, dt);
            _overdrive.Step(_targetOverdrive, dt);
        }

        var h = Heat;
        var o = Overdrive;
        SmoothedValue = h < 0.999 ? Redline * Math.Pow(h, 1 / 0.6) : Redline * (1 + o);

        if (title != Title)
        {
            OnTitle(Title, title, snap);
        }

        var vehicle = CurrentVehicle;
        Band = Title == SpeedTitle.Idle ? 0 : BandProgress(Title, SmoothedValue, Redline, o);
        Coast = Title == SpeedTitle.Idle ? 0 : Math.Clamp(SmoothedValue / SpeedTitles.Threshold(Title, Redline), 0, 1);
        GroundTarget = Parked ? 0 : HeroMath.Lerp(vehicle.Motion.SpeedLow, vehicle.Motion.SpeedHigh, Band) * Math.Sqrt(Coast);

        var animated = options.Animated;
        _ground.Step(GroundTarget, dt);
        if (!animated || snap)
        {
            _ground.Snap(GroundTarget);
        }

        if (animated)
        {
            Distance += _ground.Value * dt;
            var rate = 2 * Math.PI * (1 + 0.5 * Band) * dt;
            _bobPhase = HeroMath.WrapRadians(_bobPhase + 1.7 * rate);
            _bobPhase2 = HeroMath.WrapRadians(_bobPhase2 + 3.3 * rate);
        }

        var wakeTarget = Parked ? 0 : 1;
        if (snap)
        {
            _wake.Snap(wakeTarget);
        }
        else
        {
            _wake.Step(wakeTarget, dt);
        }

        var standTarget = Parked && _ground.Value < 6 ? 1 : 0;
        Stand = snap ? standTarget : Stand + (standTarget - Stand) * (1 - Math.Exp(-dt / 0.18));

        if (Previous is { } previous && now - previous.Start > PreviousLifetime)
        {
            Previous = null;
        }

        if (_startup is { } startup && now - startup.Start > StartupLifetime)
        {
            _startup = null;
        }

        if (_flash is { } flash && now - flash.Start > FlashLifetime)
        {
            _flash = null;
        }

        if (animated)
        {
            Particles.Step(dt, _ground.Value);
            Streaks.Step(dt, StreakCap, vehicle, h, _ground.Value, _random);
            EmitContinuous(dt);
            if (_startup is not null)
            {
                StartupEvents();
            }
        }
        else
        {
            Particles.Clear();
            Streaks.Clear();
        }
    }

    /// <summary>Band progress <c>b = clamp(ln(v / T_k) / ln(T_(k+1) / T_k), 0, 1)</c>; the rocket uses overdrive.</summary>
    public static double BandProgress(SpeedTitle title, double value, double redline, double overdrive)
    {
        if (title == SpeedTitle.Rocket)
        {
            return Math.Clamp(overdrive, 0, 1);
        }

        if (title == SpeedTitle.Idle)
        {
            return 0;
        }

        var lo = SpeedTitles.Threshold(title, redline);
        var hi = SpeedTitles.Threshold(title + 1, redline);
        return Math.Clamp(Math.Log(Math.Max(value, 1e-6) / lo) / Math.Log(hi / lo), 0, 1);
    }

    /// <summary>Live speed-line count for the level (Soft 0.6 N ≤ 5, Standard N ≤ 9, Max 1.4 N ≤ 14).</summary>
    public int StreakCap
    {
        get
        {
            if (Level == EffectsIntensity.Off || Parked || !Animated || Options.HighContrast)
            {
                return 0;
            }

            var low = CurrentVehicle.Motion.SpeedLow;
            var n = HeroMath.RoundHalfUp((2 + 8 * Heat) * Math.Clamp(_ground.Value / low, 0, 1));
            return Level switch
            {
                EffectsIntensity.Soft => Math.Min(5, HeroMath.RoundHalfUp(n * 0.6)),
                EffectsIntensity.Standard => Math.Min(9, n),
                _ => Math.Min(14, HeroMath.RoundHalfUp(n * 1.4)),
            };
        }
    }

    /// <summary>Ground distance a vehicle is drawn at: <c>D</c> while animated, its rest pose otherwise.</summary>
    public double DistanceFor(Vehicle vehicle) => Animated ? Distance : vehicle.Motion.Rest;

    /// <summary>Clockwise spin angle (radians, 0..2π) of a spinning part.</summary>
    public double SpinAngle(Vehicle vehicle, SpinSpec spin) => HeroMath.WrapRadians(DistanceFor(vehicle) / spin.Radius);

    /// <summary>Spoke blur 0..1 (<c>smoothstep((f − 14) / 12)</c>, f = spoke frequency).</summary>
    public double SpokeBlur(SpinSpec spin)
    {
        if (!Animated)
        {
            return 0;
        }

        var frequency = _ground.Value / (2 * Math.PI * spin.Radius) * spin.Symmetry;
        return HeroMath.Smooth((frequency - 14) / 12);
    }

    /// <summary>Rotation of the spoke motion arcs: clockwise at 0.6 rev/s.</summary>
    public double SpokeArcAngle => HeroMath.WrapRadians(Time * 2 * Math.PI * 0.6);

    public ActorPlacement Place(in HeroActor actor, bool outgoing)
    {
        var tau = Time - actor.Start;
        double along = 0;
        double alpha;
        double ghost = 0;
        if (outgoing)
        {
            switch (actor.Transition)
            {
                case ActorTransition.Up:
                {
                    var u = tau / 0.34;
                    along = 90 * HeroMath.EaseInCubic(u);
                    alpha = 1 - HeroMath.Smooth((tau - 0.12) / 0.18);
                    ghost = u is >= 0 and < 1 ? 90 * 3 * u * u / 0.34 : 0;
                    break;
                }

                case ActorTransition.Down:
                    along = -46 * HeroMath.EaseInOutSine(tau / 0.55);
                    alpha = 1 - HeroMath.Smooth(tau / 0.45);
                    break;
                case ActorTransition.Cut:
                    alpha = 0;
                    break;
                case ActorTransition.Wake:
                    alpha = 1 - HeroMath.Smooth(tau / 0.2);
                    break;
                default:
                    alpha = 1 - HeroMath.Smooth(tau / 0.3);
                    break;
            }

            along += actor.BaseOffset;
            alpha *= actor.BaseAlpha;
        }
        else
        {
            switch (actor.Transition)
            {
                case ActorTransition.Up:
                {
                    var s = tau - 0.12;
                    along = s <= 0 ? -62 : HeroMath.SpringAt(s, -62, 0, 140, 0.5, 0.8);
                    alpha = HeroMath.Smooth(s / 0.14);
                    ghost = s > 0 ? Math.Max(0, HeroMath.SpringVelocityAt(s, -62, 0, 140, 0.5, 0.8)) : 0;
                    break;
                }

                case ActorTransition.Down:
                {
                    var s = tau - 0.1;
                    along = 8 * (1 - HeroMath.EaseOutCubic(s / 0.55));
                    alpha = HeroMath.Smooth(s / 0.35);
                    break;
                }

                case ActorTransition.Fade:
                    alpha = HeroMath.Smooth(tau / 0.3);
                    break;
                case ActorTransition.Wake:
                    alpha = HeroMath.Smooth((tau - 0.12) / 0.28);
                    break;
                default:
                    alpha = 1;
                    break;
            }
        }

        var (hx, hy) = HeadingVector(Art.Vehicles[actor.Vehicle]);
        return new ActorPlacement(along * hx, along * hy, Math.Clamp(alpha, 0, 1), ghost, hx, hy, along);
    }

    /// <summary>Start-up modifiers for an actor; only the current vehicle has a start-up.</summary>
    public StartupFx Startup(in HeroActor actor, bool outgoing)
    {
        if (outgoing || _startup is not { } startup || startup.Vehicle != actor.Vehicle)
        {
            return StartupFx.None;
        }

        var tau = Time - startup.Start;
        if (tau < 0)
        {
            return StartupFx.None with { Flame = 0, Contrail = 0, Shock = 0, Hover = 0 };
        }

        double Bump(double a, double p, double e) => HeroMath.Bump(tau, a, p, e);
        var fx = StartupFx.None;
        switch (Art.Vehicles[actor.Vehicle].Title)
        {
            case SpeedTitle.Bicycle:
                fx = fx with { HipLift = 1.3 * Bump(0, 0.18, 0.6), Lurch = 1.6 * Bump(0, 0.22, 0.9) };
                break;
            case SpeedTitle.EBike:
                fx = fx with { HipLift = 1.3 * Bump(0, 0.18, 0.6), Lurch = 1.6 * Bump(0, 0.22, 0.9), Lights = Bump(0, 0.08, 0.5) };
                break;
            case SpeedTitle.Motorcycle:
                fx = fx with
                {
                    Pitch = -3 * Bump(0.12, 0.34, 0.8),
                    Shake = 0.35 * (1 - HeroMath.Smooth(tau / 0.3)),
                    Lurch = 1.4 * Bump(0.1, 0.35, 0.9),
                };
                break;
            case SpeedTitle.Car or SpeedTitle.SportsCar:
                fx = fx with
                {
                    Lights = Math.Max(Bump(0, 0.04, 0.12), Bump(0.16, 0.2, 0.3)),
                    Pitch = -1.3 * Bump(0.05, 0.25, 0.7),
                    Lurch = 1.2 * Bump(0.08, 0.3, 0.9),
                };
                break;
            case SpeedTitle.RaceCar:
                fx = fx with { Pitch = -1.1 * Bump(0, 0.2, 0.6), Lurch = 2.0 * Bump(0.05, 0.28, 0.9) };
                break;
            case SpeedTitle.BulletTrain:
                fx = fx with { Lights = Bump(0, 0.15, 0.7), Lurch = 1.0 * Bump(0.1, 0.45, 1.1) };
                break;
            case SpeedTitle.Maglev:
                fx = fx with { Hover = HeroMath.Smooth((tau - 0.2) / 0.5), Lights = Bump(0, 0.15, 0.7), Glow = Bump(0, 0.3, 0.8) };
                break;
            case SpeedTitle.Jetliner:
                fx = fx with { Contrail = HeroMath.Smooth(tau / 1.2), Lift = -2 * Bump(0.1, 0.5, 1.2), Lights = Bump(0, 0.2, 0.8) };
                break;
            case SpeedTitle.FighterJet or SpeedTitle.Hypersonic:
                fx = fx with
                {
                    Flame = tau < 0.35 ? HeroMath.SpringAt(tau, 0, 1, 9, 0.32, 0.55) : 1,
                    Glow = Bump(0, 0.06, 0.35),
                    Shock = HeroMath.Smooth((tau - 0.1) / 0.4),
                    Lurch = 1.4 * Bump(0, 0.2, 0.8),
                };
                break;
            case SpeedTitle.Rocket:
                fx = fx with
                {
                    Flame = tau < 0.4 ? HeroMath.SpringAt(tau, 0, 1, 8, 0.36, 0.5) : 1,
                    Glow = Bump(0, 0.06, 0.4),
                    Shake = 0.8 * (1 - HeroMath.Smooth(tau / 0.6)),
                    Lift = 6 * (1 - HeroMath.EaseInCubic(tau / 0.9)),
                };
                break;
        }

        return fx;
    }

    /// <summary>Suspension, float, hover, pitch, lurch and start-up offsets of an actor (DESIGN 4c "Continuous motion").</summary>
    public ActorMotion Motion(Vehicle vehicle, in ActorPlacement placement, in StartupFx fx)
    {
        var animated = Animated;
        var m = vehicle.Motion;
        var b = Band;
        var wake = Wake;
        var t = Time;
        var lurch = animated ? Math.Clamp(0.008 * Acceleration, -1.2, 1.2) + fx.Lurch : 0;
        double bob = 0, floatY = 0, pitch = 0, hover = 0;
        if (animated)
        {
            if (m.Bob > 0)
            {
                bob = m.Bob * (0.6 * Math.Sin(_bobPhase) + 0.4 * Math.Sin(_bobPhase2 + 1.3)) * (0.4 + 0.6 * b) * wake;
            }

            if (m.Float > 0)
            {
                floatY = m.Float * Math.Sin(2 * Math.PI * 0.32 * t) * (0.3 + 0.7 * wake);
                pitch = 0.8 * (m.Float / 0.7) * Math.Cos(2 * Math.PI * 0.32 * t) * wake;
            }

            if (vehicle.Scenery == SceneryKind.Road && vehicle.HasWheels)
            {
                pitch += Math.Clamp(-0.0035 * Acceleration, -1, 1) + fx.Pitch;
            }
        }

        if (m.Hover > 0)
        {
            hover = m.Hover * fx.Hover * wake * (animated ? 0.75 + 0.25 * Math.Sin(2 * Math.PI * 0.7 * t) : 1);
        }

        // The lurch surges along the direction of travel (the rocket: up-right).
        var ox = placement.X + lurch * placement.HeadingX;
        var oy = placement.Y + lurch * placement.HeadingY;
        if (vehicle.Heading is { } degrees && degrees != 0)
        {
            var heading = degrees * Math.PI / 180;
            ox -= Math.Cos(heading) * fx.Lift;
            oy -= Math.Sin(heading) * fx.Lift;
            if (animated && m.Float > 0)
            {
                var n = m.Float * Math.Sin(2 * Math.PI * 0.55 * t);
                ox += -Math.Sin(heading) * n;
                oy += Math.Cos(heading) * n;
            }

            floatY = 0;
            pitch = 0;
        }
        else
        {
            oy += fx.Lift;
        }

        return new ActorMotion(ox, oy, bob, floatY, hover, pitch, vehicle.PitchPivot);
    }

    /// <summary>Poses a rigged vehicle's limbs into <paramref name="pose"/>; false when the vehicle has no rig.</summary>
    public bool PoseRig(Vehicle vehicle, in StartupFx fx, RigPose pose)
    {
        if (vehicle.Walk is { } walk)
        {
            pose.Walk(walk, DistanceFor(vehicle), vehicle.GroundY ?? 79, Stand);
            return true;
        }

        if (vehicle.Pedal is { } pedal)
        {
            pose.Pedal(pedal, DistanceFor(vehicle), fx.HipLift);
            return true;
        }

        return false;
    }

    /// <summary>Flame scale along and across <c>nozzle.dir</c>; length 0 hides the flame.</summary>
    public (double Length, double Width) FlameScale(in StartupFx fx)
    {
        var t = Time;
        var animated = Animated;
        var flicker = animated
            ? 1 + 0.1 * (Math.Sin(2 * Math.PI * 17 * t) + Math.Sin(2 * Math.PI * 23 * t + 1.1) + Math.Sin(2 * Math.PI * 31 * t + 2.3)) / 3
            : 1;
        var length = (0.55 + 0.45 * Band + 0.35 * Overdrive) * flicker * fx.Flame * Wake;
        var width = (1 + (animated ? 0.06 * Math.Sin(2 * Math.PI * 13 * t) : 0)) * Math.Min(1, 0.6 + 0.4 * length);
        return (length, width);
    }

    /// <summary>Unflickered flame length, for the shock diamonds.</summary>
    public double SteadyFlameLength(in StartupFx fx) => (0.55 + 0.45 * Band + 0.35 * Overdrive) * fx.Flame;

    /// <summary>Shock cone vertical scale about the nose and its opacity factor.</summary>
    public (double Scale, double Opacity) ShockCone(in StartupFx fx) => (1.15 - 0.35 * Band,
        fx.Shock * (0.4 + 0.6 * Band) * Wake * (Animated ? 0.7 + 0.3 * Math.Sin(2 * Math.PI * 11 * Time) : 1));

    /// <summary>Opacity factor of a <c>light</c> part (lights wake up and flare during start-ups).</summary>
    public double LightFactor(in StartupFx fx) => 0.7 + 0.3 * Wake + 0.6 * fx.Lights;

    /// <summary>Static speed marks with Off and Reduce Motion: 1–3 by band progress, none when parked.</summary>
    public int StaticMarks => Parked ? 0 : Band < 0.34 ? 1 : Band < 0.67 ? 2 : 3;

    /// <summary>Camera shake offset of the layer content (redline, Standard and Max) plus start-up shakes.</summary>
    public PointD Shake
    {
        get
        {
            var amplitude = 0.0;
            if (Level >= EffectsIntensity.Standard && Tier == SpeedTier.Redline && !Parked && Animated)
            {
                amplitude = (0.2 + 0.5 * Overdrive) * (Level == EffectsIntensity.Max ? 1.4 : 1);
            }

            amplitude += Startup(Current, false).Shake;
            if (amplitude <= 0)
            {
                return default;
            }

            var t = Time;
            return new PointD(
                amplitude * (0.5 * Math.Sin(2 * Math.PI * 23 * t) + 0.3 * Math.Sin(2 * Math.PI * 31 * t + 1) + 0.2 * Math.Sin(2 * Math.PI * 41 * t + 2)),
                amplitude * (0.5 * Math.Sin(2 * Math.PI * 19 * t + 0.5) + 0.3 * Math.Sin(2 * Math.PI * 29 * t + 2) + 0.2 * Math.Sin(2 * Math.PI * 37 * t + 1)));
        }
    }

    /// <summary>Glow blur σ in canvas units (× 1.2 at Max).</summary>
    public double GlowSigma => (1.0 + 1.4 * Heat + 0.6 * Overdrive) * (Level == EffectsIntensity.Max ? 1.2 : 1);

    /// <summary>Opacity of the blurred glow copy (may exceed 1 briefly; clamp when drawing); none in high contrast.</summary>
    public double GlowOpacity
    {
        get
        {
            if (Options.HighContrast)
            {
                return 0;
            }

            var dark = Options.Dark;
            var h = Heat;
            var o = Overdrive;
            var glow = Level == EffectsIntensity.Off
                ? dark ? 0.3 : 0.12
                : dark ? 0.3 + 0.45 * h + 0.15 * o : 0.14 + 0.18 * h;
            glow *= Wake * (1 + Startup(Current, false).Glow) * (Options.IncreasedContrast ? 0.5 : 1);
            if (_flash is { } flash && Time >= flash.Start)
            {
                glow *= 1 + 0.6 * (1 - Math.Clamp((Time - flash.Start) / 0.4, 0, 1));
            }

            return glow;
        }
    }

    /// <summary>The upgrade flash on the incoming vehicle's center, or null.</summary>
    public HeroFlash? Flash
    {
        get
        {
            if (_flash is not { } flash || Time < flash.Start)
            {
                return null;
            }

            var u = Math.Clamp((Time - flash.Start) / 0.35, 0, 1);
            var alpha = (Options.Dark ? 0.42 : 0.26) * flash.Strength * (1 - u) * (1 - u) * (Level == EffectsIntensity.Soft ? 0.6 : 1);
            return alpha <= 0.001 ? null : new HeroFlash(CurrentVehicle.Bounds.Center, 8 + 28 * HeroMath.EaseOutCubic(u), alpha);
        }
    }

    /// <summary>The idle light band (Soft and up, parked), in step with the gauge's idle sweep; null between sweeps.</summary>
    public HeroSweep? Sweep
    {
        get
        {
            if (!Animated || !Parked)
            {
                return null;
            }

            var amount = 1 - Wake;
            if (amount < 0.05)
            {
                return null;
            }

            double u;
            if (double.IsFinite(IdleSweepOrigin))
            {
                u = (Time - IdleSweepOrigin - SweepDelay) / SweepDuration;
            }
            else
            {
                var since = Time - _parkedAt;
                if (!double.IsFinite(since) || since < SweepInterval)
                {
                    return null;
                }

                u = (since % SweepInterval - SweepDelay) / SweepDuration;
            }

            if (u is < 0 or >= 1)
            {
                return null;
            }

            return new HeroSweep(-10 + 120 * (0.5 - 0.5 * Math.Cos(Math.PI * u)), amount);
        }
    }

    /// <summary>Stroke, accent and core colors (slate while parked) and the parked breathing presence.</summary>
    public HeroPaint Paint()
    {
        var dark = Options.Dark;
        var ic = Options.IncreasedContrast;
        var key = new PaintKey(Quantize(Heat), Quantize(Overdrive), Quantize(Wake), dark, ic);
        if (key != _paintKey)
        {
            _paintKey = key;
            var h = key.Heat / 128.0;
            var o = key.Overdrive / 128.0;
            var w = key.Wake / 128.0;
            var idle = HeroPalette.Stroke(0, dark, ic);
            _paintCache = (
                OkLab.Mix(idle, HeroPalette.Stroke(h, dark, ic), w),
                OkLab.Mix(idle, HeroPalette.Hot(h, o, dark, ic), w),
                HeroPalette.Core(o, dark));
        }

        var breath = Animated ? 0.86 + 0.14 * Math.Sin(2 * Math.PI * Time / 4.8) : 1;
        var idleOpacity = (dark ? 0.55 : 0.5) * breath;
        var presence = idleOpacity + (1 - idleOpacity) * Wake;
        return new HeroPaint(_paintCache.Color, _paintCache.Hot, _paintCache.Core, presence, dark, Options.HighContrast);
    }

    /// <summary>How often the layer needs frames (DESIGN 4c "Accessibility and performance").</summary>
    public FrameDemand Demand
    {
        get
        {
            var settling = !_heat.IsSettled(_targetHeat, 0.0005) || !_overdrive.IsSettled(_targetOverdrive, 0.0005)
                || !_wake.IsSettled(Parked ? 0 : 1, 0.002) || Previous is not null;
            if (!Animated)
            {
                return settling ? FrameDemand.Full : FrameDemand.None;
            }

            if (settling || !Parked || _ground.Value > 0.01 || Math.Abs(Acceleration) > 0.01 || _startup is not null || _flash is not null
                || Particles.AliveCount > 0 || Streaks.AliveCount > 0 || Math.Abs(Stand - (_ground.Value < 6 ? 1 : 0)) > 0.002)
            {
                return FrameDemand.Full;
            }

            return FrameDemand.Medium;
        }
    }

    /// <summary>
    /// Frame rate while moving: the largest integer divisor of the display refresh rate that is still at least 60
    /// (144 Hz → 72, 120 Hz → 60, 60 Hz → 60); a display below 60 Hz runs at its own rate.
    /// </summary>
    public static double MovingFrameRate(int refreshHz)
    {
        if (refreshHz <= 60)
        {
            return Math.Max(refreshHz, 1);
        }

        for (var divisor = refreshHz / 60; divisor > 1; divisor--)
        {
            if (refreshHz % divisor == 0)
            {
                return refreshHz / divisor;
            }
        }

        return refreshHz;
    }

    private static (double X, double Y) HeadingVector(Vehicle vehicle)
    {
        if (vehicle.Heading is not { } degrees)
        {
            return (1, 0);
        }

        var a = degrees * Math.PI / 180;
        return (Math.Cos(a), Math.Sin(a));
    }

    private static int Quantize(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 128);

    private void OnTitle(SpeedTitle from, SpeedTitle to, bool snap)
    {
        Title = to;
        if (to == SpeedTitle.Idle)
        {
            Parked = true;
            _parkedAt = Time;
            return;
        }

        var index = VehicleArt.IndexFor(to);
        if (snap)
        {
            Parked = false;
            Current = new HeroActor(index, Time, ActorTransition.Stay);
            Previous = null;
            _startup = null;
            _flash = null;
            Particles.Clear();
            Streaks.Clear();
            return;
        }

        var off = Level == EffectsIntensity.Off;
        var reduce = Options.ReduceMotion;
        if (from == SpeedTitle.Idle)
        {
            Parked = false;
            var same = index == Current.Vehicle;
            if (!same)
            {
                Previous = Outgoing(off ? ActorTransition.Cut : reduce ? ActorTransition.Fade : ActorTransition.Wake);
            }

            Current = new HeroActor(index, Time, same || off ? ActorTransition.Stay : reduce ? ActorTransition.Fade : ActorTransition.Wake);
            _startup = !reduce && !off ? new StartupState(index, Time + (same ? 0.15 : 0.30)) : null;
            return;
        }

        var kind = off ? ActorTransition.Cut : reduce ? ActorTransition.Fade : to > from ? ActorTransition.Up : ActorTransition.Down;
        var levels = Math.Abs((int)to - (int)from);
        Previous = Outgoing(kind);
        Current = new HeroActor(index, Time, kind, levels);
        _startup = null;
        if (kind == ActorTransition.Up)
        {
            _flash = (Time + 0.32, Math.Min(1.6, 1 + 0.2 * (levels - 1)));
            if (Animated && !Options.HighContrast)
            {
                for (var i = 0; i < 4; i++)
                {
                    Streaks.Spawn(true, Art.Vehicles[index], Heat, _ground.Value, _random);
                }
            }
        }
    }

    /// <summary>The current actor becomes the outgoing one from where it stands (a transition may still be running).</summary>
    private HeroActor Outgoing(ActorTransition kind)
    {
        var placement = Place(Current, false);
        return new HeroActor(Current.Vehicle, Time, kind, 1, placement.Along, placement.Alpha);
    }

    private void StartupEvents()
    {
        var startup = _startup!.Value;
        var tau = Time - startup.Start;
        var vehicle = Art.Vehicles[startup.Vehicle];
        var cap = Level >= EffectsIntensity.Standard ? int.MaxValue : 6;
        var fired = startup.Fired;

        bool Once(int bit, double at)
        {
            var mask = 1 << bit;
            if (tau < at || (fired & mask) != 0)
            {
                return false;
            }

            fired |= mask;
            return true;
        }

        void Burst(HeroParticleKind kind, EmitterSpec? emitter, int count)
        {
            if (emitter is not null)
            {
                Particles.Burst(kind, emitter.At, emitter.Direction, count, cap, _random);
            }
        }

        var dust = vehicle.Emitter(EmitterKind.Dust);
        if (dust is not null && Once(0, 0.05))
        {
            Burst(HeroParticleKind.Puff, dust, dust.Count);
        }

        var exhaust = vehicle.Emitter(EmitterKind.Exhaust);
        switch (vehicle.Title)
        {
            case SpeedTitle.EBike when Once(1, 0.02):
                var arc = vehicle.Emitter(EmitterKind.Arc);
                Burst(HeroParticleKind.Arc, arc, arc?.Count ?? 0);
                break;
            case SpeedTitle.Motorcycle:
                if (Once(2, 0))
                {
                    Burst(HeroParticleKind.Puff, exhaust, exhaust?.Count ?? 0);
                }

                if (Once(3, 0.18))
                {
                    Burst(HeroParticleKind.Puff, exhaust, exhaust?.Count ?? 0);
                }

                break;
            case SpeedTitle.Car when Once(4, 0.1):
                Burst(HeroParticleKind.Puff, exhaust, exhaust?.Count ?? 0);
                break;
            case SpeedTitle.SportsCar when Once(5, 0.12):
                Burst(HeroParticleKind.Pop, exhaust, 1);
                break;
            case SpeedTitle.RaceCar when Once(6, 0.02):
                if (vehicle.Part("rearTire")?.Circle is { } tire && vehicle.GroundY is { } ground)
                {
                    Particles.Burst(HeroParticleKind.Puff, new PointD(tire.X, ground), new PointD(-1, -0.35), 5, cap, _random);
                }

                break;
            case SpeedTitle.Rocket when Once(7, 0):
                Burst(HeroParticleKind.Ring, vehicle.Emitter(EmitterKind.Plume), 6);
                break;
        }

        for (var i = 0; i < 3; i++)
        {
            if (Once(8 + i, 0.1 + 0.07 * i) && !Options.HighContrast)
            {
                Streaks.Spawn(true, vehicle, Heat, _ground.Value, _random);
            }
        }

        _startup = startup with { Fired = fired };
    }

    private void EmitContinuous(double dt)
    {
        if (Level < EffectsIntensity.Standard || Parked)
        {
            return;
        }

        var rate = Level == EffectsIntensity.Max ? 1.6 : 1;
        var vehicle = CurrentVehicle;
        switch (vehicle.Title)
        {
            case SpeedTitle.Rocket when vehicle.Emitter(EmitterKind.Sparks) is { } sparks:
                Particles.Stream(sparks.At, sparks.Direction, (26 + 60 * Overdrive) * rate, 70, 0.55, 0, 0.32, dt, _random);
                break;
            case SpeedTitle.RaceCar when Band > 0.55 && vehicle.Emitter(EmitterKind.Sparks) is { } floor:
                Particles.Stream(floor.At, floor.Direction, 30 * (Band - 0.55) / 0.45 * rate, 120, 0.5, 260, 0.28, dt, _random);
                break;
            case SpeedTitle.FighterJet or SpeedTitle.Hypersonic when Tier >= SpeedTier.Overdrive
                                                                    && vehicle.Emitter(EmitterKind.Afterburner) is { } burner:
                Particles.Stream(burner.At, burner.Direction, (6 + 14 * Band) * rate, 60, 0.3, 0, 0.25, dt, _random);
                break;
        }
    }

    private readonly record struct StartupState(int Vehicle, double Start, int Fired = 0);

    private readonly record struct PaintKey(int Heat, int Overdrive, int Wake, bool Dark, bool IncreasedContrast);
}
