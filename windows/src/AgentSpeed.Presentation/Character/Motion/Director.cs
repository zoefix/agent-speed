namespace AgentSpeed.Presentation.Character;

/// <summary>Something the director wants the scene to show, e.g. a puff of petals at a jump's take-off.</summary>
public enum DirectorEvent
{
    Petals,
}

/// <summary>
/// Turns the speed level into parameter values (motions/director.py): a groove per band on a beat clock, one action at a
/// time from the band's pool, hand acting, breathing, glances, blinks and wind, then springs. Side steps move the
/// character around its spot, away from the orb, and she stays where she lands.
/// </summary>
public sealed class CharacterDirector
{
    private static readonly IReadOnlyDictionary<SpeedBand, string[]> Pools = new Dictionary<SpeedBand, string[]>
    {
        [SpeedBand.Idle] = ["open", "chin", "hairtuck", "hug", "yawn", "stretch", "lookaround", "tilt", "foottap", "ponder", "step", "bow"],
        [SpeedBand.Cruise] = ["wave", "hug", "present", "hairtuck", "hum", "nod", "tilt", "giggle", "sway", "tiptoe", "curtsy", "step", "peek", "cuddle"],
        [SpeedBand.Boost] = ["idea", "peace", "hip", "wave", "hop", "sway", "tiptoe", "step", "spin", "giggle", "doubletake", "hooray", "eureka", "hops"],
        [SpeedBand.Overdrive] = ["fist", "present", "hip", "idea", "hops", "run", "spin", "step", "doubletake", "cheer", "proud", "hop"],
        [SpeedBand.Redline] = ["fist", "raise", "peace", "jumpjoy", "run", "spin", "hops", "cheer", "hooray", "jump"],
    };

    private static readonly string[] Celebrations = ["jumpjoy", "spin", "cheer", "hop"];

    private static readonly IReadOnlyDictionary<SpeedBand, (double Low, double High)> Gaps = new Dictionary<SpeedBand, (double, double)>
    {
        [SpeedBand.Idle] = (2.4, 4.4),
        [SpeedBand.Cruise] = (0.9, 2.2),
        [SpeedBand.Boost] = (0.7, 1.8),
        [SpeedBand.Overdrive] = (0.4, 1.3),
        [SpeedBand.Redline] = (0.3, 1.0),
    };

    private static readonly string[] Hard = ["Var.", "Lift.", "Bend.", "Spin", "PosX"];
    private const double Stage = 0.55;

    /// <summary>Actions that leave the ground or travel, skipped while <see cref="Grounded"/> (Reduce Motion).</summary>
    private static readonly HashSet<string> Airborne = new(StringComparer.Ordinal) { "hop", "jump", "hops", "spin", "step", "run", "jumpjoy", "pounce" };

    private sealed class GrooveLayer(SpeedBand band, double w, double target)
    {
        public SpeedBand Band { get; } = band;

        public double W { get; set; } = w;

        public double Target { get; set; } = target;
    }

    private sealed class Playing(CharacterMotion motion, bool[] hard)
    {
        public CharacterMotion Motion { get; } = motion;

        public bool[] Hard { get; } = hard;

        public double T { get; set; }

        public double W { get; set; }

        public int Fired { get; set; }
    }

    private readonly Slots _slots;
    private readonly double[] _grooveScale;
    private readonly int[] _frozen;
    private readonly int[] _armSlots;
    private readonly int[] _headSlots;
    private readonly List<GrooveLayer> _grooves = [];
    private readonly List<string> _recent = [];
    private readonly List<HandActor> _hands = [];
    private readonly bool[] _present;
    private readonly List<(int Slot, double Value)> _grooveOut = new(32);
    private readonly double[] _grooveSum;
    private readonly double[] _grooveWeight;
    private readonly bool[] _grooveSeen;
    private readonly List<int> _grooveTouched = new(32);
    private readonly SecondOrder[] _springs;
    private readonly bool[] _hasSpring;
    private readonly bool[] _springLive;
    private bool _armsHeld;
    private bool _headHeld;
    private Rng _rng;
    private Beat _beat;
    private Playing? _gesture;
    private double _nextGesture = 2.0;
    private double _blinkAt = 1.2;
    private double? _blinkT;
    private (double X, double Y) _gaze;
    private (double X, double Y) _gazeTarget;
    private double _gazeAt = 0.8;
    private double _breathPhase;

    public CharacterDirector(IEnumerable<CharacterParam> parameters, ulong seed = 7, CharacterSide output = CharacterSide.Right,
        CharacterSide book = CharacterSide.Left, double orb = -1, bool celebrate = true, MotionProfile? profile = null)
    {
        Space = new ParamSpace(parameters);
        _slots = new Slots(Space);
        Profile = profile ?? new MotionProfile();
        var space = Space;
        _grooveScale = space.Names.Select(Profile.GrooveScale).ToArray();
        _frozen = Profile.Seated ? MotionProfile.SeatedFreeze.Where(space.IsParam).Select(n => space.Slot(n)!.Value).ToArray() : [];
        _armSlots = Enumerable.Range(0, space.Count)
            .Where(i => space.Params[i] is not null && MotionProfile.ArmParams.Any(p => space.Names[i].StartsWith(p, StringComparison.Ordinal)))
            .ToArray();
        _armsHeld = Profile.PoseHandsFor(SpeedBand.Idle) is { } idleSet && MotionProfile.TwoHanded.Contains(idleSet);
        _headSlots = MotionProfile.HeadParams.Where(space.IsParam).Select(n => space.Slot(n)!.Value).ToArray();
        _headHeld = Profile.PoseHandsFor(SpeedBand.Idle) is { } headSet && MotionProfile.AtMouth.Contains(headSet);
        _beat = new Beat(Profile.Tempo);
        Out = output;
        Book = book;
        Orb = orb;
        Celebrate = celebrate;
        _rng = new Rng(seed);
        Values = new double[space.Count];
        _present = new bool[space.Count];
        _grooveSum = new double[space.Count];
        _grooveWeight = new double[space.Count];
        _grooveSeen = new bool[space.Count];
        _springs = new SecondOrder[space.Count];
        _hasSpring = new bool[space.Count];
        for (var i = 0; i < space.Count; i++)
        {
            if (space.Springs[i] is { } tuning)
            {
                _springs[i] = new SecondOrder(tuning);
                _hasSpring[i] = true;
            }
        }

        _springLive = new bool[space.Count];
        var k = 0;
        foreach (var side in CharacterSides.All)
        {
            if (space.IsParam($"Finger.{side.Key()}.0"))
            {
                var hand = new HandActor(side, side == book, k * 1.7, ref _rng)
                {
                    FreeSets = Profile.Hands,
                };
                hand.SetMode(Profile.PoseHandsFor(SpeedBand.Idle));
                _hands.Add(hand);
            }

            k++;
        }
    }

    public ParamSpace Space { get; }

    public MotionProfile Profile { get; }

    /// <summary>The band of the pose on screen, for its hand set.</summary>
    public SpeedBand PoseBand { get; private set; } = SpeedBand.Idle;

    public CharacterSide Out { get; private set; }

    public CharacterSide Book { get; private set; }

    public double Orb { get; }

    public bool Celebrate { get; }

    /// <summary>Skips the actions that leave the ground (DESIGN 2a.6, Reduce Motion).</summary>
    public bool Grounded { get; set; }

    public string? LastGesture { get; private set; }

    public double StageX { get; private set; }

    public List<DirectorEvent> Events { get; } = new(8);

    public SpeedBand? Band { get; private set; }

    public int Level { get; private set; }

    public double Intensity { get; private set; }

    public double Time { get; private set; }

    /// <summary>Actions started so far (gestures and hand fidgets); starting one allocates its curves.</summary>
    public int MotionStarts { get; private set; }

    /// <summary>The parameters of the last update, by slot.</summary>
    public double[] Values { get; }

    /// <summary>Whether a gesture is playing, and its name.</summary>
    public string? ActiveGesture => _gesture is null ? null : LastGesture;

    /// <summary>Which arm swings free and which holds the object, for a new pose (of <paramref name="poseBand"/>, for its hand set).</summary>
    public void SetSides(CharacterSide output, CharacterSide book, SpeedBand? poseBand = null)
    {
        Out = output;
        Book = book;
        if (poseBand is { } pb)
        {
            PoseBand = pb;
        }

        var set = Profile.PoseHandsFor(PoseBand);
        foreach (var hand in _hands)
        {
            hand.Holds = hand.Side == book;
            hand.SetMode(set);
        }

        _armsHeld = set is not null && MotionProfile.TwoHanded.Contains(set);
        _headHeld = set is not null && MotionProfile.AtMouth.Contains(set);
        foreach (var g in _grooves)
        {
            g.Target = 0;
        }

        _grooves.Add(new GrooveLayer(Band ?? SpeedBand.Idle, 0, 1));
    }

    private bool Can(params string[] names) => names.All(Space.IsParam);

    private MotionSpec? Spec(string gid)
    {
        if (Gestures.Arm.Contains(gid) || Gestures.Combos.Contains(gid))
        {
            return null;
        }

        if (Grounded && Airborne.Contains(gid))
        {
            return null;
        }

        if (gid == "jumpjoy")
        {
            return Can("PosY", "Squash") ? Moves.Jump(Out, Book, arms: true) : null;
        }

        if (gid == "step")
        {
            if (!Can("PosX", "PosY"))
            {
                return null;
            }

            var away = -Orb * Stage;
            var targets = new[] { 0.0, 0.45 * away, away }.Where(t => Math.Abs(t - StageX) > 0.1).ToArray();
            return Moves.Sidestep(Out, Book, _rng.Choice(targets) - StageX);
        }

        if (gid == "peek")
        {
            return Can("Lean") ? Moves.Peek(Out, Book, Orb) : null;
        }

        if (gid == "pounce")
        {
            return Can("PosY", "Squash") ? Moves.Pounce(Out, Book, Orb) : null;
        }

        if (Moves.Profile.TryGetValue(gid, out var needs))
        {
            return needs.Any(Space.IsParam) ? Moves.ProfileMove(gid, Out, Book) : null;
        }

        if (Moves.Names.Contains(gid))
        {
            return Can("PosY", "Squash") ? Moves.Make(gid, Out, Book) : null;
        }

        if (Gestures.Head.Contains(gid))
        {
            return Gestures.HeadGesture(gid);
        }

        if (Gestures.Face.Contains(gid) && Profile.Signature.Contains(gid))
        {
            return Gestures.FaceGesture(gid);
        }

        return null;
    }

    private bool StartGesture(string gid)
    {
        if (!Profile.Allows(gid) || Spec(gid) is not { } spec)
        {
            return false;
        }

        var space = Space;
        var motion = spec.Bind(space.Slot);
        var hard = motion.Entries.Select(e => Hard.Any(h => e.Name.StartsWith(h, StringComparison.Ordinal))).ToArray();
        _gesture = new Playing(motion, hard);
        MotionStarts++;
        LastGesture = gid;
        _recent.Add(gid);
        if (_recent.Count > 3)
        {
            _recent.RemoveRange(0, _recent.Count - 3);
        }

        return true;
    }

    private bool RestIdle => Profile.Idle == "rest";

    private void PlayGroove(SpeedBand band)
    {
        foreach (var g in _grooves)
        {
            g.Target = 0;
        }

        _grooves.Add(new GrooveLayer(band, _grooves.Count == 0 ? 1 : 0, 1));
    }

    public void SetLevel(int level)
    {
        var band = SpeedBands.OfLevel(level);
        if (Band is not { } current)
        {
            Band = band;
            PlayGroove(band);
        }
        else if (band != current)
        {
            var rising = band > current;
            Band = band;
            PlayGroove(band);
            if (rising && Celebrate && (_gesture is null || _gesture.T > 0.5 * _gesture.Motion.Duration))
            {
                foreach (var gid in _rng.Sample(Celebrations, Celebrations.Length))
                {
                    if (StartGesture(gid))
                    {
                        break;
                    }
                }
            }

            _nextGesture = Time + 1.5;
        }

        Level = level;
    }

    private void BlendGrooves(double dt)
    {
        foreach (var g in _grooves)
        {
            g.W += Math.Max(-dt / 0.5, Math.Min(dt / 0.5, g.Target - g.W));
        }

        _grooves.RemoveAll(g => !(g.W > 1e-3 || g.Target > 0));
        _grooveTouched.Clear();
        var o = _slots.SideOf(Out);
        var k = _slots.SideOf(Book);
        foreach (var layer in _grooves)
        {
            Groove.Emit(layer.Band, _beat.Phase, Time, _slots, o, k, RestIdle, _grooveOut);
            foreach (var (slot, raw) in _grooveOut)
            {
                var v = raw * _grooveScale[slot];
                if (!_grooveSeen[slot])
                {
                    _grooveSeen[slot] = true;
                    _grooveTouched.Add(slot);
                    _grooveSum[slot] = 0;
                    _grooveWeight[slot] = 0;
                }

                _grooveSum[slot] += v * layer.W;
                _grooveWeight[slot] += layer.W;
            }
        }

        foreach (var slot in _grooveTouched)
        {
            var wsum = _grooveWeight[slot];
            var w = Math.Min(1.0, wsum);
            Write(slot, Values[slot] * (1 - w) + _grooveSum[slot] / Math.Max(wsum, 1e-6) * w);
            _grooveSeen[slot] = false;
        }
    }

    private void Write(int slot, double v)
    {
        Values[slot] = v;
        _present[slot] = true;
    }

    /// <summary>Adds an idle motion of an appendage, scaled down while an action drives the same parameter, within its range.</summary>
    private void AddIdle(int slot, double v)
    {
        if (Space.Params[slot] is not { } p)
        {
            return;
        }

        var share = 1.0;
        if (_gesture is { } g)
        {
            foreach (var entry in g.Motion.Entries)
            {
                if (entry.Slot == slot)
                {
                    share = 1 - g.W;
                    break;
                }
            }
        }

        Write(slot, Math.Min(Math.Max(Values[slot] + share * v, p.Min), p.Max));
    }

    private void ApplyGesture(double dt)
    {
        if (_gesture is not { } g)
        {
            if (Time >= _nextGesture)
            {
                var band = Band!.Value;
                var pool = Profile.Pool(Pools[band], band).Where(a => !_recent.Contains(a)).ToList();
                _rng.Shuffle(pool);
                foreach (var gid in pool)
                {
                    if (StartGesture(gid))
                    {
                        break;
                    }
                }

                _nextGesture = Time + 1.0;
            }

            return;
        }

        g.T += dt;
        var m = g.Motion;
        g.W = Math.Min(Math.Min(1.0, g.T / m.Fade), Math.Max(0.0, (m.Duration - g.T) / m.Fade));
        var local = m.LocalTime(g.T);
        for (var i = 0; i < m.Entries.Length; i++)
        {
            var entry = m.Entries[i];
            var v = entry.Curve.Evaluate(local);
            Write(entry.Slot, g.Hard[i] ? v : Values[entry.Slot] * (1 - g.W) + v * g.W);
        }

        while (g.Fired < m.Events.Length && m.Events[g.Fired].Time <= g.T)
        {
            if (m.Events[g.Fired].Name == "petals")
            {
                Events.Add(DirectorEvent.Petals);
            }

            g.Fired++;
        }

        if (g.T >= m.Duration)
        {
            StageX += m.Travel;
            _gesture = null;
            var gap = Gaps[Band!.Value];
            _nextGesture = Time + _rng.Uniform(gap.Low * Profile.Gap, gap.High * Profile.Gap);
        }
    }

    /// <summary>Advances one frame; the parameters land in <see cref="Values"/> (read the rig's own through <see cref="Space"/>).</summary>
    public void Update(double dt)
    {
        Time += dt;
        Intensity += (Level / 13.0 - Intensity) * Math.Min(1.0, dt * 2.5);
        var s = Intensity;
        var t = Time;
        _beat.Update(dt, Band ?? SpeedBand.Idle);
        for (var i = 0; i < Space.Count; i++)
        {
            if (Space.Params[i] is { } p)
            {
                Values[i] = p.Default;
                _present[i] = true;
            }
            else
            {
                Values[i] = 0;
                _present[i] = false;
            }
        }

        BlendGrooves(dt);
        ApplyGesture(dt);
        Write(_slots.PosX, Values[_slots.PosX] + StageX);
        if (_armsHeld)
        {
            foreach (var slot in _armSlots)
            {
                Write(slot, Space.Params[slot]!.Default);
            }
        }

        foreach (var hand in _hands)
        {
            var side = _slots.SideOf(hand.Side);
            var arm = 0.0;
            arm += Values[side.Arm];
            arm += Values[side.Forearm];
            arm += Values[side.Lift];
            arm += Values[side.Bend];
            var idle = hand.Action is null;
            hand.Update(dt, Time, Band ?? SpeedBand.Idle, 60 / _beat.Bpm, arm, ref _rng);
            if (idle && hand.Action is not null)
            {
                MotionStarts++;
            }

            for (var i = 0; i < 5; i++)
            {
                Write(side.Fingers[i], Values[side.Fingers[i]] + hand.Fingers[i]);
            }

            Write(side.Spread, Values[side.Spread] + hand.Spread);
            Write(side.Wrist, Values[side.Wrist] + hand.Wrist);
        }

        _breathPhase += dt / (4.2 - 2.4 * s);
        Write(_slots.Breath, Math.Max(Values[_slots.Breath], 0.5 - 0.5 * Math.Cos(2 * Math.PI * _breathPhase)));
        var amp = 0.06 + 0.12 * s;
        Write(_slots.AngleX, Values[_slots.AngleX] + amp * (Math.Sin(t * 0.83) + 0.5 * Math.Sin(t * 1.91 + 1.3)));
        Write(_slots.AngleY, Values[_slots.AngleY] + amp * 0.7 * Math.Sin(t * 1.17 + 0.4));
        Write(_slots.AngleZ, Values[_slots.AngleZ] + amp * 0.8 * Math.Sin(t * 0.61 + 2.1));
        Write(_slots.BodySway, Values[_slots.BodySway] + (0.15 + 0.3 * s) * Math.Sin(t * (0.9 + 1.6 * s)));
        Write(_slots.Wind, Math.Min(1.0, Math.Pow(s, 1.2) * 1.05));
        if (Space.IsParam(_slots.TailSwish))
        {
            AddIdle(_slots.TailSwish, (0.18 + 0.3 * s) * Profile.Sway * Math.Sin(Math.PI * _beat.Phase + 0.5));
        }

        if (Space.IsParam(_slots.WingFlap))
        {
            AddIdle(_slots.WingFlap, 0.12 * Math.Sin(2 * Math.PI * _breathPhase));
        }

        foreach (var slot in _frozen)
        {
            Write(slot, Space.Params[slot]!.Default);
        }

        if (_headHeld)
        {
            foreach (var slot in _headSlots)
            {
                Write(slot, Space.Params[slot]!.Default);
            }
        }

        ApplySprings(dt);

        _gazeAt -= dt;
        if (_gazeAt <= 0)
        {
            var gx = _rng.Uniform(-0.5, 0.5);
            var gy = _rng.Uniform(-0.35, 0.35);
            _gazeTarget = (gx, gy);
            _gazeAt = _rng.Uniform(0.9, 2.6) * (1.2 - 0.6 * s);
        }

        var k = Math.Min(1.0, dt * 14);
        _gaze = (_gaze.X + (_gazeTarget.X - _gaze.X) * k, _gaze.Y + (_gazeTarget.Y - _gaze.Y) * k);
        Write(_slots.EyeBallX, Math.Max(-1, Math.Min(1, Values[_slots.EyeBallX] + 0.35 * _gaze.X)));
        Write(_slots.EyeBallY, Math.Max(-1, Math.Min(1, Values[_slots.EyeBallY] + 0.35 * _gaze.Y)));

        var openness = Blink(dt);
        foreach (var slot in _slots.EyeOpen)
        {
            if (_present[slot])
            {
                Values[slot] = Math.Min(Values[slot], openness);
            }
        }
    }

    private void ApplySprings(double dt)
    {
        for (var i = 0; i < Space.Count; i++)
        {
            if (!_present[i] || Space.Params[i] is null || !_hasSpring[i])
            {
                continue;
            }

            if (!_springLive[i])
            {
                _springs[i].Snap(Values[i]);
                _springLive[i] = true;
            }

            Values[i] = _springs[i].Update(dt, Values[i]);
        }
    }

    private double Blink(double dt)
    {
        _blinkAt -= dt;
        if (_blinkT is null && _blinkAt <= 0)
        {
            _blinkT = 0;
        }

        if (_blinkT is not { } bt)
        {
            return 1;
        }

        bt += dt;
        _blinkT = bt;
        if (bt < 0.07)
        {
            return 1 - bt / 0.07;
        }

        if (bt < 0.11)
        {
            return 0;
        }

        if (bt < 0.2)
        {
            return (bt - 0.11) / 0.09;
        }

        _blinkT = null;
        _blinkAt = _rng.Random() > 0.15 ? _rng.Uniform(2.0, 5.0) : 0.25;
        return 1;
    }

    /// <summary>The value of a parameter after the last update, or null when the director does not know it.</summary>
    public double? Value(string name) => Space.Slot(name) is { } slot ? Values[slot] : null;
}
