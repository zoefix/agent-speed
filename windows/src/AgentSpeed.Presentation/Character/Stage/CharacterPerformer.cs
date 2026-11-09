namespace AgentSpeed.Presentation.Character;

/// <summary>What happened in a performer's frame, for effects the renderer adds (petals, ...).</summary>
public readonly record struct PerformerEvent(PerformerEventKind Kind, SpeedBand Band = SpeedBand.Idle)
{
    public static PerformerEvent Petals { get; } = new(PerformerEventKind.Petals);

    public static PerformerEvent PoseSwapped(SpeedBand band) => new(PerformerEventKind.PoseSwapped, band);
}

public enum PerformerEventKind
{
    Petals,

    /// <summary>The band's pose was swapped at the edge-on instant.</summary>
    PoseSwapped,
}

/// <summary>
/// How a pose sits on screen relative to the idle pose (spec/CHARACTERS.md, Pose swap): its feet on the idle pose's
/// feet, scaled so the face-to-feet distance matches.
/// </summary>
/// <param name="Scale">Canvas pixels of this pose per canvas pixel of the idle pose.</param>
/// <param name="FeetX">The pose's feet in its own canvas.</param>
public readonly record struct PosePlacement(double Scale, double FeetX, double FeetY)
{
    /// <summary>Screen position of a pose canvas point, given the idle pose's feet on screen and screen points per idle canvas pixel.</summary>
    public (double X, double Y) Screen(double x, double y, (double X, double Y) feetOnScreen, double pointsPerPixel)
    {
        var k = pointsPerPixel * Scale;
        return (feetOnScreen.X + (x - FeetX) * k, feetOnScreen.Y + (y - FeetY) * k);
    }
}

/// <summary>
/// One character on stage: its poses, one director over the union of their parameters, physics per pose, the pose swap
/// and the placement of each pose (characters/reference/stage.py). Step it once per frame with the speed level; then
/// read <see cref="ActivePose"/>, <see cref="World"/> and <see cref="Opacities"/> to draw. No heap allocation per frame
/// except when an action starts.
/// </summary>
public sealed partial class CharacterPerformer
{
    /// <summary>The reference's root travel per unit of PosX / PosY, in face widths (rig/params.py).</summary>
    public const double PosXTravel = 3.0;
    public const double PosYTravel = 2.0;
    public const double CrossfadeDuration = 0.3;
    private const int ExitTarget = -1;

    private readonly CharacterPhysics[] _physics;
    private readonly int[][] _slotMaps;
    private readonly PoseSwitch _poseSwitch = new();
    private readonly Slots _slots;
    private readonly double _faceWidth;
    private readonly List<PerformerEvent> _events = new(8);
    private Affine2[] _world = [];
    private double[] _opacities = [];
    private Affine2[] _fadingWorld = [];
    private double[] _fadingOpacities = [];
    private bool _pendingExit;
    private bool _calm;

    public CharacterPerformer(CharacterPack pack, ulong seed = 7, double orb = 1)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Pack = pack;
        var parameters = new List<CharacterParam>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pose in pack.Poses)
        {
            foreach (var p in pose.Rig.Params)
            {
                if (seen.Add(p.Id))
                {
                    parameters.Add(p);
                }
            }
        }

        var idle = pack.Poses[0].Rig;
        var book = CharacterSides.Parse(idle.Landmarks.ObjectSide) ?? CharacterSide.Left;
        Director = new CharacterDirector(parameters, seed, book.Other(), book, orb, celebrate: false, profile: pack.Manifest.Motion ?? new MotionProfile());
        var space = Director.Space;
        _slots = new Slots(space);
        Values = new double[space.Count];
        var defaults = space.Params.Select(p => p?.Default ?? 0).ToArray();
        _physics = pack.Poses.Select(pose =>
        {
            var physics = new CharacterPhysics(pose.Rig.Physics, name => space.IsParam(name) ? space.Slot(name) : null, windSign: 1);
            physics.Reset(defaults);
            return physics;
        }).ToArray();
        _slotMaps = pack.Poses.Select(pose => pose.Rig.Params.Select(p => space.Slot(p.Id)!.Value).ToArray()).ToArray();
        var baseMeasures = Measures(idle);
        Placements = pack.Poses.Select(pose =>
        {
            var feet = pose.Rig.Landmarks.Feet ?? [pose.Rig.CanvasWidth / 2, pose.Rig.CanvasHeight];
            var scale = PoseScale(baseMeasures, Measures(pose.Rig));
            return new PosePlacement(scale, feet[0], feet.Length > 1 ? feet[1] : pose.Rig.CanvasHeight);
        }).ToArray();
        var top = double.PositiveInfinity;
        foreach (var part in idle.Parts)
        {
            for (var i = 1; i < part.Vertices.Length; i += 2)
            {
                top = Math.Min(top, part.Vertices[i]);
            }
        }

        var feetY = idle.Landmarks.Feet is { Length: > 0 } f ? f[^1] : idle.CanvasHeight;
        IdleHeight = double.IsFinite(top) ? feetY - top : idle.CanvasHeight;
        _faceWidth = idle.Landmarks.FaceWidth ?? idle.CanvasWidth / 10;
        RigValues = new double[pack.Poses.Select(p => p.Rig.Params.Length).DefaultIfEmpty(0).Max()];
    }

    public CharacterPack Pack { get; }

    public CharacterDirector Director { get; }

    public int ActivePose { get; private set; }

    public IReadOnlyList<PosePlacement> Placements { get; }

    /// <summary>The idle pose's height from the top of its art to its feet, in its canvas pixels.</summary>
    public double IdleHeight { get; }

    /// <summary>Parameter values by director slot after the last step (swap and physics applied).</summary>
    public double[] Values { get; }

    /// <summary>The active pose's rig parameters after the last step (the first <c>Params.Length</c> entries).</summary>
    public double[] RigValues { get; }

    /// <summary>The active pose's bone transforms after the last step.</summary>
    public Affine2[] World => _world;

    /// <summary>The active pose's part opacities after the last step.</summary>
    public double[] Opacities => _opacities;

    public IReadOnlyList<PerformerEvent> Events => _events;

    /// <summary>Set on the frame an exit turn reaches edge-on (see <see cref="CharacterStage"/>).</summary>
    public bool ExitReached { get; private set; }

    /// <summary>How much of the director's motion shows, 0…1 (DESIGN 2a.6 effects: Off 0 keeps only breathing and blinks, Soft 0.5).</summary>
    public double Amplitude { get; set; } = 1;

    /// <summary>Reduce Motion: no airborne actions, and pose swaps crossfade over <see cref="CrossfadeDuration"/> instead of turning.</summary>
    public bool Calm
    {
        get => _calm;
        set
        {
            _calm = value;
            Director.Grounded = value;
        }
    }

    /// <summary>During a calm swap: the outgoing pose, its last transforms and opacities, and the crossfade progress 0 → 1.</summary>
    public int? FadingPose { get; private set; }

    public Affine2[] FadingWorld => _fadingWorld;

    public double[] FadingOpacities => _fadingOpacities;

    public double FadeProgress { get; private set; } = 1;

    /// <summary>Face center to feet, face box diagonal, shoulders to hips (null in packs built without it).</summary>
    internal static double?[] Measures(CharacterRig rig)
    {
        var lm = rig.Landmarks;
        if (lm.Face is not { Length: 4 } f)
        {
            return [null, null, lm.TorsoLength];
        }

        double? body = lm.Feet is { Length: 2 } feet ? feet[1] - (f[1] + f[3]) / 2 : null;
        return [body, CharacterPhysics.Hypot(f[2] - f[0], f[3] - f[1]), lm.TorsoLength];
    }

    /// <summary>
    /// How much a pose is scaled to match the idle pose: the middle of the three ratios of <see cref="Measures"/>, so one
    /// that the pose throws off (tucked legs in a jump, a turned face) is outvoted; face to feet alone without a torso.
    /// </summary>
    internal static double PoseScale(double?[] baseMeasures, double?[] pose)
    {
        var ratios = new List<double>(3);
        for (var i = 0; i < Math.Min(baseMeasures.Length, pose.Length); i++)
        {
            if (baseMeasures[i] is { } b && pose[i] is { } p && b > 0 && p > 0)
            {
                ratios.Add(b / p);
            }
        }

        if (ratios.Count == 3)
        {
            ratios.Sort();
            return ratios[1];
        }

        if (baseMeasures.Length > 0 && pose.Length > 0 && baseMeasures[0] is { } b0 && pose[0] is { } p0 && b0 > 0 && p0 > 0)
        {
            return b0 / p0;
        }

        return 1;
    }

    /// <summary>The pose index a band shows: its own, else the nearest lower band's.</summary>
    public int PoseIndex(SpeedBand band)
    {
        var best = 0;
        for (var i = 0; i < Pack.Poses.Count; i++)
        {
            if (Pack.Poses[i].Band <= band)
            {
                best = i;
            }
        }

        return best;
    }

    public CharacterRig ActiveRig => Pack.Poses[ActivePose].Rig;

    public PosePlacement ActivePlacement => Placements[ActivePose];

    public SpeedBand? Band => Director.Band;

    public double Intensity => Director.Intensity;

    /// <summary>
    /// True while she acts out something (a gesture, a move, a hand fidget aside) or turns to swap poses: the frames that
    /// need the full 60 fps.
    /// </summary>
    public bool IsActing => Director.ActiveGesture is not null || _poseSwitch.IsRunning || FadingPose is not null;

    /// <summary>The character's sideways and upward travel this frame (side steps, hops), in idle canvas pixels (y down).</summary>
    public (double Dx, double Dy) BodyOffset =>
        (Values[_slots.PosX] * PosXTravel * _faceWidth, -Values[_slots.PosY] * PosYTravel * _faceWidth);

    /// <summary>Asks the character to turn edge-on so another one can take over (<see cref="CharacterStage"/>).</summary>
    public void RequestExit() => _pendingExit = true;

    private CharacterSide BookOfActive() => CharacterSides.Parse(ActiveRig.Landmarks.ObjectSide) ?? Director.Book;

    /// <summary>Starts this character on the edge-on instant in the pose of <paramref name="level"/>, turning back toward the viewer.</summary>
    public void EnterEdgeOn(int level)
    {
        ActivePose = PoseIndex(SpeedBands.OfLevel(level));
        var book = BookOfActive();
        Director.SetLevel(level);
        Director.SetSides(book.Other(), book, Pack.Poses[ActivePose].Band);
        _poseSwitch.EnterEdgeOn();
    }

    /// <summary>Starts this character in the pose of <paramref name="level"/>, facing the viewer (a calm character change).</summary>
    public void EnterAtRest(int level)
    {
        ActivePose = PoseIndex(SpeedBands.OfLevel(level));
        var book = BookOfActive();
        Director.SetLevel(level);
        Director.SetSides(book.Other(), book, Pack.Poses[ActivePose].Band);
    }

    /// <summary>
    /// The floating readout's geometry for this frame (characters/reference/stage.py): the idle pose's feet at
    /// <paramref name="feetOnScreen"/>, <paramref name="pointsPerPixel"/> screen points per idle canvas pixel,
    /// <paramref name="unit"/> points per reference unit.
    /// </summary>
    public HudLayout HudLayout((double X, double Y) feetOnScreen, double pointsPerPixel, double unit)
    {
        var h = IdleHeight * pointsPerPixel;
        var (dx, dy) = BodyOffset;
        var fx = feetOnScreen.X + dx * pointsPerPixel;
        var fy = feetOnScreen.Y;
        var lift = dy * pointsPerPixel;
        return new HudLayout((fx, fy + 6 * unit, 0.27 * h, 0.27 * h * 0.17), (fx + 0.01 * h, fy - 0.6 * h + lift, 0.3 * h, 0.05 * h), h, unit);
    }

    /// <summary>Pulls every motion parameter toward its rest value by <see cref="Amplitude"/>, keeping breathing and blinks.</summary>
    private void Damp()
    {
        var space = Director.Space;
        for (var i = 0; i < Values.Length; i++)
        {
            if (i == _slots.Breath || i == _slots.EyeOpen[0] || i == _slots.EyeOpen[1])
            {
                continue;
            }

            var rest = space.Params[i]?.Default ?? 0;
            Values[i] = rest + (Values[i] - rest) * Amplitude;
        }
    }

    /// <summary>Advances one frame at speed level 0...13.</summary>
    public void Step(double dt, int level)
    {
        _events.Clear();
        ExitReached = false;
        Director.SetLevel(level);
        Director.Update(dt);
        foreach (var e in Director.Events)
        {
            if (e == DirectorEvent.Petals)
            {
                _events.Add(PerformerEvent.Petals);
            }
        }

        Director.Events.Clear();
        Array.Copy(Director.Values, Values, Values.Length);
        if (Amplitude < 1)
        {
            Damp();
        }

        if (FadingPose is not null)
        {
            FadeProgress = Math.Min(1, FadeProgress + dt / CrossfadeDuration);
            if (FadeProgress >= 1)
            {
                FadingPose = null;
            }
        }

        if ((Calm || Director.Profile.Seated) && !_pendingExit && Director.Band is { } calmBand && PoseIndex(calmBand) != ActivePose
            && !_poseSwitch.IsRunning)
        {
            // Reduce Motion, and a character in a chair, crossfade poses instead of turning.
            FadingPose = ActivePose;
            CopyInto(ref _fadingWorld, _world);
            CopyInto(ref _fadingOpacities, _opacities);
            FadeProgress = 0;
            ActivePose = PoseIndex(calmBand);
            var book = BookOfActive();
            Director.SetSides(book.Other(), book, Pack.Poses[ActivePose].Band);
            _events.Add(PerformerEvent.PoseSwapped(Pack.Poses[ActivePose].Band));
        }
        else if (_pendingExit && !_poseSwitch.IsRunning)
        {
            _poseSwitch.Request(ExitTarget);
            _pendingExit = false;
        }
        else if (!_pendingExit && Director.Band is { } band)
        {
            var wanted = PoseIndex(band);
            if (wanted != ActivePose)
            {
                _poseSwitch.Request(wanted);
            }
        }

        var (swap, output, swapped) = _poseSwitch.Update(dt);
        if (output is { } o)
        {
            Values[_slots.Spin] = o.Spin;
            Values[_slots.Squash] += o.Squash;
            Values[_slots.PosY] += o.PosY;
            Values[_slots.SkirtFlare] = Math.Max(Values[_slots.SkirtFlare], o.SkirtFlare);
        }

        if (swapped && swap is { } target)
        {
            if (target == ExitTarget)
            {
                ExitReached = true;
            }
            else
            {
                ActivePose = target;
                var book = BookOfActive();
                Director.SetSides(book.Other(), book, Pack.Poses[target].Band);
                _events.Add(PerformerEvent.PoseSwapped(Pack.Poses[target].Band));
            }
        }

        for (var i = 0; i < _physics.Length; i++)
        {
            _physics[i].Step(dt, Values);
            if (i == ActivePose)
            {
                _physics[i].Write(Values);
            }
        }

        var map = _slotMaps[ActivePose];
        for (var i = 0; i < map.Length; i++)
        {
            RigValues[i] = Values[map[i]];
        }

        var rig = ActiveRig;
        var values = RigValues.AsSpan(0, map.Length);
        rig.WorldTransforms(values, ref _world);
        rig.Opacities(values, ref _opacities);
    }

    private static void CopyInto<T>(ref T[] target, T[] source)
    {
        if (target.Length != source.Length)
        {
            target = new T[source.Length];
        }

        Array.Copy(source, target, source.Length);
    }
}

/// <summary>
/// The character an orb shows and the turn between two characters (spec/CHARACTERS.md, Which character): the current one
/// turns edge-on, the next one takes over at that instant and turns back.
/// </summary>
public sealed class CharacterStage(CharacterPerformer performer)
{
    public CharacterPerformer Current { get; private set; } = performer;

    public CharacterPerformer? Incoming { get; private set; }

    /// <summary>Set on the frame the incoming character took over.</summary>
    public bool Swapped { get; private set; }

    /// <summary>
    /// Reduce Motion: a character change crossfades over <see cref="CharacterPerformer.CrossfadeDuration"/> instead of
    /// turning; passed on to the performers with <see cref="Amplitude"/>.
    /// </summary>
    public bool Calm { get; set; }

    public double Amplitude { get; set; } = 1;

    /// <summary>During a calm character change: the outgoing character and the crossfade progress 0 → 1.</summary>
    public CharacterPerformer? Outgoing { get; private set; }

    public double OutgoingFade { get; private set; } = 1;

    /// <summary>Starts the turn to another character; a newer request replaces one still waiting.</summary>
    public void SwitchTo(CharacterPerformer next)
    {
        Incoming = next;
        Current.RequestExit();
    }

    public void Step(double dt, int level)
    {
        Swapped = false;
        if (Current.Calm != Calm)
        {
            Current.Calm = Calm;
        }

        Current.Amplitude = Amplitude;
        if (Outgoing is { } o)
        {
            o.Amplitude = Amplitude;
            o.Step(dt, level);
        }

        if (Outgoing is not null)
        {
            OutgoingFade = Math.Min(1, OutgoingFade + dt / CharacterPerformer.CrossfadeDuration);
            if (OutgoingFade >= 1)
            {
                Outgoing = null;
            }
        }

        if (Calm && Incoming is { } calmNext)
        {
            calmNext.Calm = true;
            calmNext.EnterAtRest(level);
            Outgoing = Current;
            OutgoingFade = 0;
            Current = calmNext;
            Incoming = null;
            Swapped = true;
        }

        Current.Step(dt, level);
        if (Current.ExitReached && Incoming is { } next)
        {
            next.EnterEdgeOn(level);
            next.Step(dt, level);
            Current = next;
            Incoming = null;
            Swapped = true;
        }
    }
}
