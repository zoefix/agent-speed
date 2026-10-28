using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Hero.Badge;

/// <summary>
/// Platform-neutral state of the dashboard hero's title badge (DESIGN 4c, Hero title badge): a medallion that ranks up
/// with the speed title of every title theme except Vehicles. Transitions, rank pips, springs, accumulated phases and
/// fixed pools (16 streaks, 64 particles); stepping never allocates and a seed replays exactly.
/// </summary>
public sealed partial class BadgeStage
{
    public const int PipCount = 13;
    public const int StreakCapacity = 16;
    public const int ParticleCapacity = 64;
    public const int MoteSlots = 12;
    public const double TransitionLifetime = 1.0;
    public const double RenameDuration = 0.25;

    /// <summary>A title change at most this old starts the transition where it happened (with the label slide).</summary>
    public const double ChangeWindow = 0.3;

    public const double MaxStep = 1.0 / 15;
    public const double WakeSettle = 0.5;
    public const double GroundSettle = 0.9;
    public const double OmegaSettle = 0.6;
    public const double PipDelay = 0.10;
    public const double PipStagger = 0.045;
    public const double StartupPipDelay = 0.12;
    public const double StartupPipStagger = 0.035;
    public const double ParkPipStagger = 0.03;
    public const double SweepInterval = 6;

    private static readonly int[] MoteOrderTable = [0, 6, 3, 9, 1, 7, 4, 10, 2, 8, 5, 11];

    private readonly BadgePip[] _pips = new BadgePip[PipCount];
    private readonly BadgeStreak[] _streaks = new BadgeStreak[StreakCapacity];
    private readonly BadgeParticle[] _particles = new BadgeParticle[ParticleCapacity];
    private readonly HeroRandom _random;
    private IReadOnlyList<string> _labels = [];
    private SettleSpring _wake = new(WakeSettle);
    private SettleSpring _ground = new(GroundSettle);
    private SettleSpring _omega = new(OmegaSettle);
    private double _lastTime;
    private double _streakTimer;
    private double _emberCarry;
    private double _parkedAt = double.NegativeInfinity;
    private double _lastHeat;
    private double _lastOverdrive;
    private double _lastRedline;
    private bool _inputsChanged;
    private bool _primed;

    public BadgeStage(uint seed = 0x6D2B79F5)
    {
        _random = new HeroRandom(seed);
        ResetPips(0);
    }

    /// <summary>Slots in fill order (0, 6, 3, 9, …): any mote count is spread evenly around the ring.</summary>
    public static ReadOnlySpan<int> MoteOrder => MoteOrderTable;

    public BadgeInput Input { get; private set; }

    public double Time { get; private set; }

    /// <summary>The level shown: 0 idle, 1–13.</summary>
    public int Level { get; private set; }

    /// <summary>The title shown (the label of <see cref="Level"/>).</summary>
    public string Text { get; private set; } = string.Empty;

    public BadgeTransition? Transition { get; private set; }

    /// <summary>The text a rename (theme or language change on the same level) crossfades from, or null.</summary>
    public string? RenameFrom { get; private set; }

    public double RenameStart { get; private set; } = double.NegativeInfinity;

    /// <summary>0 = parked (slate), 1 = awake (heat colors).</summary>
    public double Wake => HeroMath.Clamp01(_wake.Value);

    /// <summary>Badge ground speed <c>G</c> (units/s) that drives the streaks.</summary>
    public double Ground => _ground.Value;

    /// <summary>Angular speed of the orbit motes (rad/s).</summary>
    public double Omega => _omega.Value;

    public double MotePhase { get; private set; }

    public double HaloPhase { get; private set; }

    public double BreathPhase { get; private set; }

    /// <summary>Accumulated sheen cycles; the band crosses during the first 0.7 s of each.</summary>
    public double SheenPhase { get; private set; }

    public ReadOnlySpan<BadgePip> Pips => _pips;

    public ReadOnlySpan<BadgeStreak> Streaks => _streaks;

    public ReadOnlySpan<BadgeParticle> Particles => _particles;

    /// <summary>The effects level after the Reduce Motion and high-contrast caps.</summary>
    public EffectsIntensity Fx => Input.Options.Level;

    /// <summary>Anything moves at all (Soft and up without Reduce Motion).</summary>
    public bool Moving => Input.Options.Animated;

    /// <summary>Halo, glow, motes, streaks and particles exist (not in a Windows high-contrast theme).</summary>
    public bool Decorated => !Input.Options.HighContrast;

    public bool Dark => Input.Options.Dark;

    public int AliveStreaks => CountAlive(_streaks);

    public int AliveParticles => CountAlive(_particles);

    /// <summary>The next step starts from the steady state of its input (a preview, the badge just became visible).</summary>
    public void Reset() => _primed = false;

    /// <summary>The label of <paramref name="level"/> in the labels last stepped with (idle first).</summary>
    public string Label(int level) => level >= 0 && level < _labels.Count ? _labels[level] : string.Empty;

    /// <summary>Advances to <see cref="BadgeInput.Time"/>.</summary>
    /// <param name="labels">The badge text of every level, idle first (indexed by <see cref="SpeedTitle"/>).</param>
    public void Step(in BadgeInput input, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        _labels = labels;
        Input = input;
        var level = Math.Clamp(input.Level, 0, PipCount);
        var text = Label(level);
        if (!_primed)
        {
            Prime(input, level, text);
            return;
        }

        var dt = input.Time - _lastTime;
        dt = double.IsFinite(dt) ? Math.Clamp(dt, 0, MaxStep) : 0;
        _lastTime = input.Time;
        Time = input.Time;
        _inputsChanged = Math.Abs(input.Heat - _lastHeat) > 1e-4 || Math.Abs(input.Overdrive - _lastOverdrive) > 1e-4
            || input.Redline != _lastRedline;
        _lastHeat = input.Heat;
        _lastOverdrive = input.Overdrive;
        _lastRedline = input.Redline;

        if (level != Level)
        {
            Begin(Level, level, input.TitleChangedAt);
        }
        else if (!string.Equals(Text, text, StringComparison.Ordinal))
        {
            RenameFrom = Fx > EffectsIntensity.Off ? Text : null;
            RenameStart = Time;
        }

        Text = text;
        if (Transition is { } transition && Time - transition.Start > TransitionLifetime)
        {
            Transition = null;
        }

        if (RenameFrom is not null && Time - RenameStart >= RenameDuration)
        {
            RenameFrom = null;
        }

        var active = Level > 0 ? 1.0 : 0.0;
        _wake.Step(active, dt);
        StepPips(dt);
        var h = HeroMath.Clamp01(input.Heat);
        var o = HeroMath.Clamp01(input.Overdrive);
        var moving = Moving;
        _ground.Step(active > 0 && moving ? GroundTarget(h, o) : 0, dt);
        _omega.Step(active > 0 && moving ? OmegaTarget(h, o) : 0, dt);
        MotePhase = HeroMath.WrapRadians(MotePhase + _omega.Value * dt);
        if (moving)
        {
            HaloPhase = HeroMath.WrapRadians(HaloPhase + 2 * Math.PI / HeroMath.Lerp(4, 0.7, h) * dt);
            BreathPhase = HeroMath.WrapRadians(BreathPhase + 2 * Math.PI / 4.8 * dt);
            SheenPhase += dt / SheenPeriod(h);
            if (SheenPhase > 1000)
            {
                SheenPhase -= 1000;
            }
        }

        if (moving && Decorated)
        {
            StepStreaks(dt, h);
            StepParticles(dt, o);
        }
        else
        {
            ClearPools();
        }
    }

    /// <summary>Badge ground speed target <c>G* = lerp(60, 900, h) + 300 o</c> units/s.</summary>
    public static double GroundTarget(double heat, double overdrive) => HeroMath.Lerp(60, 900, heat) + 300 * overdrive;

    /// <summary>Mote angular speed target <c>2π (0.06 + 0.9 h + 0.5 o)</c> rad/s.</summary>
    public static double OmegaTarget(double heat, double overdrive) => 2 * Math.PI * (0.06 + 0.9 * heat + 0.5 * overdrive);

    /// <summary>The sheen crosses once every <c>max(1.4, 5 − 3.5 h)</c> s.</summary>
    public static double SheenPeriod(double heat) => Math.Max(1.4, 5 - 3.5 * heat);

    /// <summary>How often the badge needs frames: full while anything moves, parked ≈ 15 fps (30 during the light band).</summary>
    public BadgeFrames Demand
    {
        get
        {
            if (!_primed)
            {
                return BadgeFrames.Full;
            }

            if ((Transition is { } transition && Time - transition.Start < TransitionLifetime && Fx > EffectsIntensity.Off)
                || RenameFrom is not null || _inputsChanged || AliveStreaks > 0 || AliveParticles > 0
                || !_wake.IsSettled(Level > 0 ? 1 : 0, 0.002) || PipsSettling())
            {
                return BadgeFrames.Full;
            }

            // High contrast keeps only the title, rings and pips: nothing moves between transitions.
            if (!Moving || !Decorated)
            {
                return BadgeFrames.None;
            }

            if (Level > 0)
            {
                return BadgeFrames.Full;
            }

            return SweepWindow(0.07, 0.05) ? BadgeFrames.Sweep : BadgeFrames.Parked;
        }
    }

    private void Prime(in BadgeInput input, int level, string text)
    {
        _primed = true;
        _lastTime = input.Time;
        Time = input.Time;
        _lastHeat = input.Heat;
        _lastOverdrive = input.Overdrive;
        _lastRedline = input.Redline;
        _inputsChanged = false;
        Level = level;
        Text = text;
        Transition = null;
        RenameFrom = null;
        RenameStart = double.NegativeInfinity;
        var active = level > 0 ? 1.0 : 0.0;
        var h = HeroMath.Clamp01(input.Heat);
        var o = HeroMath.Clamp01(input.Overdrive);
        _wake = new SettleSpring(WakeSettle, active);
        _ground = new SettleSpring(GroundSettle, active > 0 && Moving ? GroundTarget(h, o) : 0);
        _omega = new SettleSpring(OmegaSettle, active > 0 && Moving ? OmegaTarget(h, o) : 0);
        _parkedAt = level == 0 ? input.Time : double.NegativeInfinity;
        ResetPips(level);
        ClearPools();
    }

    private void Begin(int from, int to, double changedAt)
    {
        var start = double.IsFinite(changedAt) && changedAt <= Time && changedAt > Time - ChangeWindow ? changedAt : Time;
        var kind = from == 0 ? BadgeTransitionKind.Startup
            : to == 0 ? BadgeTransitionKind.Park
            : to > from ? BadgeTransitionKind.Up : BadgeTransitionKind.Down;
        Transition = new BadgeTransition(from, to, start, kind, Math.Max(1, to - from));
        RenameFrom = null;
        Level = to;
        if (to == 0)
        {
            _parkedAt = start;
        }

        // Off cuts and Reduce Motion crossfades every pip at once; otherwise they pop (or go dark) in sequence.
        var staged = Moving;
        switch (kind)
        {
            case BadgeTransitionKind.Up or BadgeTransitionKind.Startup:
            {
                var startup = kind == BadgeTransitionKind.Startup;
                var delay = startup ? StartupPipDelay : PipDelay;
                var stagger = startup ? StartupPipStagger : PipStagger;
                for (var k = from + 1; k <= to; k++)
                {
                    _pips[k - 1].Pop = staged ? start + delay + stagger * (k - from - 1) : start;
                }

                if (!startup && staged && Decorated)
                {
                    Burst(Fx == EffectsIntensity.Soft ? 6 : Math.Min(18, 10 + 3 * (to - from - 1)), start);
                }

                break;
            }

            default:
                for (var k = to + 1; k <= from; k++)
                {
                    _pips[k - 1].Pop = staged && kind == BadgeTransitionKind.Park ? start + ParkPipStagger * (from - k) : start;
                }

                break;
        }
    }

    private void StepPips(double dt)
    {
        var instant = Fx == EffectsIntensity.Off;
        var reduce = Input.Options.ReduceMotion;
        for (var k = 1; k <= PipCount; k++)
        {
            ref var pip = ref _pips[k - 1];
            var on = k <= Level;
            if (Time < pip.Pop)
            {
                continue;
            }

            if (instant || (on && !reduce))
            {
                pip.Lit = on ? 1 : 0;
                continue;
            }

            // Only the part of this step after the pip's moment counts.
            var elapsed = double.IsFinite(pip.Pop) ? Math.Min(dt, Time - pip.Pop) : dt;
            var rate = reduce ? 1 / 0.3 : 1 / 0.35;
            pip.Lit = HeroMath.Clamp01(pip.Lit + (on ? 1 : -1) * rate * elapsed);
        }
    }

    private bool PipsSettling()
    {
        for (var k = 1; k <= PipCount; k++)
        {
            ref readonly var pip = ref _pips[k - 1];
            if (Time < pip.Pop || pip.Lit != (k <= Level ? 1 : 0))
            {
                return true;
            }

            // A pip that just popped is still springing to full size.
            if (Moving && k <= Level && Time - pip.Pop < 1)
            {
                return true;
            }
        }

        return false;
    }

    private void ResetPips(int level)
    {
        for (var k = 1; k <= PipCount; k++)
        {
            _pips[k - 1] = new BadgePip { Lit = k <= level ? 1 : 0, Pop = double.NegativeInfinity };
        }
    }

    private void StepStreaks(double dt, double h)
    {
        var g = _ground.Value;
        var count = StreakCount;
        for (var i = 0; i < _streaks.Length; i++)
        {
            ref var streak = ref _streaks[i];
            if (!streak.Alive)
            {
                continue;
            }

            streak.X -= streak.Speed * dt;
            streak.Age += dt;
            if (streak.X + streak.Length < -2)
            {
                streak.Alive = false;
            }
        }

        _streakTimer -= dt;
        if (count <= 0 || _streakTimer > 0)
        {
            return;
        }

        var slot = FreeSlot(_streaks);
        if (slot < 0)
        {
            return;
        }

        _streakTimer = 0.9 / count * (0.5 + _random.Next());
        var upper = _random.Next() < 0.5;
        var length = (6 + 20 * h) * (0.7 + 0.6 * _random.Next()) + 1.5 * g / 60;
        var y = upper ? 16 + 17 * _random.Next() : 67 + 17 * _random.Next();
        var speed = 1.25 * Math.Max(g, 40) * (0.85 + 0.3 * _random.Next());
        var alpha = (0.16 + 0.32 * h) * (0.7 + 0.6 * _random.Next()) * Wake;
        _streaks[slot] = new BadgeStreak { Alive = true, X = 100 + length, Y = y, Length = length, Speed = speed, Alpha = alpha };
    }

    /// <summary>
    /// Live streak count: <c>(2 + 8 h) · clamp(G / 60)</c>, × 0.6 at Soft and × 1.4 at Max, capped at 5 / 9 / 14; none
    /// with Off, Reduce Motion or a high-contrast theme.
    /// </summary>
    public int StreakCount
    {
        get
        {
            if (!Moving || !Decorated)
            {
                return 0;
            }

            var fx = Fx;
            var scale = fx == EffectsIntensity.Max ? 1.4 : fx == EffectsIntensity.Soft ? 0.6 : 1;
            var n = HeroMath.RoundHalfUp((2 + 8 * HeroMath.Clamp01(Input.Heat)) * HeroMath.Clamp01(_ground.Value / 60) * scale);
            var cap = fx switch
            {
                EffectsIntensity.Soft => 5,
                EffectsIntensity.Standard => 9,
                EffectsIntensity.Max => 14,
                _ => 0,
            };
            return Math.Min(cap, n);
        }
    }

    private void Burst(int count, double start)
    {
        var made = 0;
        for (var i = 0; i < _particles.Length && made < count; i++)
        {
            if (_particles[i].Alive)
            {
                continue;
            }

            var angle = _random.Next() * 2 * Math.PI;
            var speed = 40 + 50 * _random.Next();
            _particles[i] = new BadgeParticle
            {
                Alive = true,
                X = 50 + Math.Cos(angle) * 12,
                Y = 50 + Math.Sin(angle) * 8,
                VelocityX = Math.Cos(angle) * speed,
                VelocityY = Math.Sin(angle) * speed,
                Age = Time - start - 0.08,
                Life = 0.35 + 0.25 * _random.Next(),
                Drag = 2.5,
            };
            made++;
        }
    }

    private void StepParticles(double dt, double o)
    {
        if (Fx >= EffectsIntensity.Standard && Level > 0 && Input.Tier >= SpeedTier.Overdrive)
        {
            _emberCarry += (6 + 26 * o) * (Fx == EffectsIntensity.Max ? 1.6 : 1) * dt;
            while (_emberCarry >= 1)
            {
                _emberCarry -= 1;
                var slot = FreeSlot(_particles);
                if (slot < 0)
                {
                    _emberCarry = 0;
                    break;
                }

                var angle = (30 + 120 * _random.Next()) * Math.PI / 180;
                var rise = 18 + 16 * _random.Next();
                _particles[slot] = new BadgeParticle
                {
                    Alive = true,
                    Ember = true,
                    X = 50 + Math.Cos(angle) * 33,
                    Y = 50 + Math.Sin(angle) * 33,
                    VelocityY = -rise,
                    Life = 0.6 + 0.5 * _random.Next(),
                    Seed = _random.Next() * 10,
                };
            }
        }
        else
        {
            _emberCarry = 0;
        }

        for (var i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (!p.Alive)
            {
                continue;
            }

            p.Age += dt;
            if (p.Age >= p.Life)
            {
                p.Alive = false;
                continue;
            }

            if (p.Age < 0)
            {
                continue;
            }

            if (p.Ember)
            {
                p.VelocityX = 6 * Math.Sin(p.Seed + 7 * p.Age);
            }

            var drag = Math.Exp(-p.Drag * dt);
            p.VelocityX *= drag;
            p.VelocityY *= drag;
            p.X += p.VelocityX * dt;
            p.Y += p.VelocityY * dt;
        }
    }

    private void ClearPools()
    {
        for (var i = 0; i < _streaks.Length; i++)
        {
            _streaks[i].Alive = false;
        }

        for (var i = 0; i < _particles.Length; i++)
        {
            _particles[i].Alive = false;
        }

        _streakTimer = 0;
        _emberCarry = 0;
    }

    private static int FreeSlot(BadgeStreak[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (!items[i].Alive)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FreeSlot(BadgeParticle[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (!items[i].Alive)
            {
                return i;
            }
        }

        return -1;
    }

    private static int CountAlive(BadgeStreak[] items)
    {
        var count = 0;
        foreach (ref readonly var item in items.AsSpan())
        {
            count += item.Alive ? 1 : 0;
        }

        return count;
    }

    private static int CountAlive(BadgeParticle[] items)
    {
        var count = 0;
        foreach (ref readonly var item in items.AsSpan())
        {
            count += item.Alive ? 1 : 0;
        }

        return count;
    }
}
