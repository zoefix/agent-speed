namespace AgentSpeed.Presentation.Character;

/// <summary>The face patches of one kind (<c>expr-*</c>, <c>mouth-*</c> or <c>gaze-*</c>) a pose has: names, slots (-1 for none) and shown weights.</summary>
internal sealed class PatchGroup(string[] names, int[] slots)
{
    public static PatchGroup Empty { get; } = new([], []);

    public string[] Names { get; } = names;

    public int[] Slots { get; } = slots;

    public double[] Weights { get; } = new double[names.Length];

    public bool IsEmpty => Names.Length == 0;

    public bool Contains(string? name)
    {
        if (name is null)
        {
            return false;
        }

        foreach (var n in Names)
        {
            if (n == name)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A pose's variants as the director uses them: arm gestures by id, the ids of face edits, its face patches and the open mouth shapes it can talk with.</summary>
internal sealed class PoseFaces(Dictionary<string, RigDocument.Variant> meta, HashSet<string> faceIds, PatchGroup expressions, PatchGroup mouths,
    PatchGroup gazes, int talkShapes)
{
    public static PoseFaces Empty { get; } = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal), PatchGroup.Empty, PatchGroup.Empty,
        PatchGroup.Empty, 0);

    public Dictionary<string, RigDocument.Variant> Meta { get; } = meta;

    public HashSet<string> FaceIds { get; } = faceIds;

    public PatchGroup Expressions { get; } = expressions;

    public PatchGroup Mouths { get; } = mouths;

    public PatchGroup Gazes { get; } = gazes;

    /// <summary>The open mouth shapes the pose has, a mask over <see cref="Talk.Shapes"/>.</summary>
    public int TalkShapes { get; } = talkShapes;

    /// <summary>Hides every patch again, as a fresh pose shows them.</summary>
    public void Reset()
    {
        Array.Clear(Expressions.Weights);
        Array.Clear(Mouths.Weights);
        Array.Clear(Gazes.Weights);
    }
}

/// <summary>
/// The living pack (spec/CHARACTERS.md, Behavior; motions/director.py): moods from the inputs, face patches swapped while
/// she blinks, lip flaps through the mouth patches, and the head and eyes turned toward where she looks.
/// </summary>
public sealed partial class CharacterDirector
{
    /// <summary>Face patch rates (seconds for a full swap); a blink over a patch shows <c>gaze-closed</c> while the eyes are half shut.</summary>
    private const double PatchFade = 0.05;
    private const double BlinkFade = 0.03;

    /// <summary>A dozing blink holds the eyes shut this long.</summary>
    private const double DozeLow = 0.5;
    private const double DozeHigh = 1.2;

    private readonly List<(IReadOnlyList<RigDocument.Variant> Key, PoseFaces Faces)> _faceCache = [];
    private PoseFaces _faces = PoseFaces.Empty;
    private bool _active;
    private bool _snapFaces;
    private bool _facePending;
    private double _blinkHold = 0.04;
    private bool _blinkSkipped;
    private string? _eventFace;
    private double _eventUntil;

    /// <summary>The moods of a living pack; null for any other pack.</summary>
    public CharacterBehavior? Behavior { get; }

    /// <summary>Whether the pack is living (face patches or mood poses): only then do inputs change anything.</summary>
    public bool IsLiving => Behavior is not null;

    /// <summary>The mood of the last update that had inputs, in a living pack.</summary>
    public CharacterMood? Mood { get; private set; }

    /// <summary>The expression patch shown, by name (<c>smile</c> for <c>expr-smile</c>).</summary>
    public string? Expression { get; private set; }

    /// <summary>The gaze patch shown, by name (<c>left</c> for <c>gaze-left</c>).</summary>
    public string? GazePatch { get; private set; }

    /// <summary>The open mouth shape this frame (<c>a</c>, <c>o</c>, <c>u</c>), null while closed or silent.</summary>
    public string? MouthShape => _active ? Behavior?.Talk.Shape : null;

    /// <summary>Whether she speaks (a group of phrases until its silence) this frame.</summary>
    public bool IsTalking => _active && Behavior?.Talk.On == true;

    /// <summary>
    /// Whether the living face or look moves this frame (DESIGN 2a.6, Frame policy): a face or gaze swap, a phrase being
    /// spoken, the look turning on or off, or (<paramref name="following"/>) the head following a cursor that just moved.
    /// </summary>
    public bool IsLivingBusy(bool following)
    {
        if (!_active || Behavior is not { } b)
        {
            return false;
        }

        if (_facePending)
        {
            return true;
        }

        if (b.Talk.On && b.Talk.Stage is Talk.TalkStage.Open or Talk.TalkStage.Gap or Talk.TalkStage.Pause)
        {
            return true;
        }

        if (b.LookWeight > 0 && b.LookWeight < 1)
        {
            return true;
        }

        return following && b.LookMode == CharacterLook.User && b.LookWeight > 0;
    }

    /// <summary>The mood pose to show in a living pack fed inputs, else null: the band pose.</summary>
    public MoodPose? PoseWanted() => _active ? Behavior?.Pose() : null;

    private RigDocument.Variant? Arm(string gid) => _faces.Meta.TryGetValue(gid, out var m) && m.Kind == "arm" ? m : null;

    /// <summary>Takes a pose's variants; each list is compiled once and reused when its pose shows again.</summary>
    private void SetVariants(IReadOnlyList<RigDocument.Variant> variants)
    {
        if (variants.Count == 0)
        {
            _faces = PoseFaces.Empty;
            return;
        }

        foreach (var (key, cached) in _faceCache)
        {
            if (ReferenceEquals(key, variants))
            {
                cached.Reset();
                _faces = cached;
                return;
            }
        }

        _faces = Compile(variants);
        _faceCache.Add((variants, _faces));
    }

    private PoseFaces Compile(IReadOnlyList<RigDocument.Variant> variants)
    {
        var meta = new Dictionary<string, RigDocument.Variant>(StringComparer.Ordinal);
        var faceIds = new HashSet<string>(StringComparer.Ordinal);
        var expressions = new List<(string Name, int Slot)>();
        var mouths = new List<(string Name, int Slot)>();
        var gazes = new List<(string Name, int Slot)>();
        foreach (var v in variants)
        {
            meta[v.Id] = v;
            if (v.Kind == "face")
            {
                faceIds.Add(v.Id);
            }

            var dash = v.Id.IndexOf('-', StringComparison.Ordinal);
            if (v.Kind != "patch" || dash < 0)
            {
                continue;
            }

            var group = v.Id[..dash] switch
            {
                "expr" => expressions,
                "mouth" => mouths,
                "gaze" => gazes,
                _ => null,
            };
            if (group is null)
            {
                continue;
            }

            var name = v.Id[(dash + 1)..];
            var slot = Space.Slot(v.Param) ?? -1;
            var i = group.FindIndex(p => p.Name == name);
            if (i >= 0)
            {
                group[i] = (name, slot);
            }
            else
            {
                group.Add((name, slot));
            }
        }

        static PatchGroup Build(List<(string Name, int Slot)> items) =>
            new(items.Select(p => p.Name).ToArray(), items.Select(p => p.Slot).ToArray());

        var mouthGroup = Build(mouths);
        var talk = 0;
        for (var i = 0; i < Talk.Shapes.Count; i++)
        {
            if (mouthGroup.Contains(Talk.Shapes[i].Shape))
            {
                talk |= 1 << i;
            }
        }

        return new PoseFaces(meta, faceIds, Build(expressions), mouthGroup, Build(gazes), talk);
    }

    /// <summary>What the behavior asks of the shown pose before it queues a hover reaction.</summary>
    private void RefreshBehaviorOptions()
    {
        if (Behavior is not { } b)
        {
            return;
        }

        b.ClearHoverOptions();
        foreach (var gid in CharacterBehavior.HoverGestures)
        {
            if (ArmGestures.Arms.ContainsKey(gid) && Profile.Allows(gid) && Arm(gid) is not null)
            {
                b.AddHoverOption(gid);
            }
        }

        b.HasWink = _faces.Expressions.Contains("wink");
    }

    private void Feel(double dt, BehaviorInputs? inputs)
    {
        _active = Behavior is not null && inputs is not null;
        if (!_active)
        {
            return;
        }

        var b = Behavior!;
        b.Update(dt, inputs!.Value, Band ?? SpeedBand.Idle);
        if (b.Mood != Mood)
        {
            Mood = b.Mood;
            var gap = CurrentGaps();
            _nextGesture = Time + _rng.Uniform(gap.Low * Profile.Gap, gap.High * Profile.Gap);
        }
    }

    /// <summary>Blends a head or eye parameter toward where she looks, except the share a playing action moves.</summary>
    private void Look(int slot, double target)
    {
        var w = Behavior!.LookWeight;
        if (!(w > 0))
        {
            return;
        }

        var k = w * IdleShare(slot);
        Write(slot, Values[slot] * (1 - k) + target * k);
    }

    private double Slewed(double w, double target, double dt, double seconds) =>
        _snapFaces ? target : w + Math.Max(-dt / seconds, Math.Min(dt / seconds, target - w));

    private void WriteSlot(int slot, double v)
    {
        if (slot >= 0)
        {
            Write(slot, v);
        }
    }

    /// <summary>
    /// Face patches: one expression, or the gaze where she looks, swapped while she blinks; a blink over a patch shows
    /// <c>gaze-closed</c>; the lips move through the mouth patches while she talks. Returns how open the eyes are.
    /// </summary>
    private double LivingFace(double dt, CharacterMotion? gesture)
    {
        var b = Behavior!;
        var expressions = _faces.Expressions;
        var gazes = _faces.Gazes;
        var mouths = _faces.Mouths;
        var yawning = gesture is { Name: "yawn" };
        var talk = (b.Talking && Level >= 1) || gesture is { Talk: true };
        talk = talk && Profile.Talk && _faces.TalkShapes != 0 && !yawning;

        // While she speaks (a group of phrases until its silence) the face is drawn: no expression, no gaze.
        var speaking = talk && b.Talk.Speaking;
        var eventFace = Time < _eventUntil ? _eventFace : null;
        var wanted = speaking ? null : b.Face(gesture, eventFace, Band ?? SpeedBand.Idle);
        if (!expressions.Contains(wanted))
        {
            wanted = null;
        }

        var gaze = !speaking && wanted is null && gazes.Contains(b.Gaze) ? b.Gaze : null;
        var change = wanted != Expression || gaze != GazePatch;
        var openness = DozeBlink(dt, change, b.Mood == CharacterMood.Rest);
        if (_snapFaces || (change && (openness <= 0.02 || _blinkSkipped)))
        {
            Expression = wanted;
            GazePatch = gaze;
        }

        var covered = Expression is not null || GazePatch is not null;
        var blinking = covered && Expression is not ("laugh" or "yawn") && openness < 0.5;
        for (var i = 0; i < expressions.Names.Length; i++)
        {
            var w = Slewed(expressions.Weights[i], expressions.Names[i] == Expression ? 1 : 0, dt, PatchFade);
            expressions.Weights[i] = w;
            WriteSlot(expressions.Slots[i], w);
        }

        for (var i = 0; i < gazes.Names.Length; i++)
        {
            var closed = gazes.Names[i] == "closed";
            var target = closed ? (blinking ? 1 : 0) : (gazes.Names[i] == GazePatch ? 1 : 0);
            var w = Slewed(gazes.Weights[i], target, dt, closed ? BlinkFade : PatchFade);
            gazes.Weights[i] = w;
            WriteSlot(gazes.Slots[i], w);
        }

        var clear = Expression is null;
        foreach (var w in expressions.Weights)
        {
            clear &= w <= 0;
        }

        var shape = b.Speak(dt, talk, Level, _faces.TalkShapes, clear);
        if (shape is null && talk && b.Talk.Stage == Talk.TalkStage.Gap)
        {
            shape = "closed";
        }

        for (var i = 0; i < mouths.Names.Length; i++)
        {
            var w = mouths.Names[i] == shape ? 1.0 : 0.0;
            mouths.Weights[i] = w;
            WriteSlot(mouths.Slots[i], w);
        }

        _snapFaces = false;
        var pending = Expression != wanted || GazePatch != gaze;
        foreach (var w in expressions.Weights)
        {
            pending |= w > 0 && w < 1;
        }

        for (var i = 0; i < gazes.Names.Length; i++)
        {
            pending |= gazes.Names[i] != "closed" && gazes.Weights[i] > 0 && gazes.Weights[i] < 1;
        }

        _facePending = pending;
        return openness;
    }

    /// <summary>
    /// The blink of a living pack: as <see cref="Blink"/>, with the eyes held shut 0.5–1.2 s while she rests.
    /// <see cref="_blinkSkipped"/> tells that the whole shut phase fell between the last frame and this one (a low frame rate).
    /// </summary>
    private double DozeBlink(double dt, bool force, bool doze)
    {
        _blinkSkipped = false;
        _blinkAt -= dt;
        if (_blinkT is null && (_blinkAt <= 0 || force))
        {
            _blinkT = 0;
            _blinkHold = doze ? Behavior!.DozeHold(DozeLow, DozeHigh) : 0.04;
        }

        if (_blinkT is not { } bt)
        {
            return 1;
        }

        bt += dt;
        _blinkT = bt;
        var hold = _blinkHold;
        _blinkSkipped = bt - dt < 0.07 && bt >= 0.07 + hold;
        if (bt < 0.07)
        {
            return 1 - bt / 0.07;
        }

        if (bt < 0.07 + hold)
        {
            return 0;
        }

        if (bt < 0.16 + hold)
        {
            return (bt - 0.07 - hold) / 0.09;
        }

        _blinkT = null;
        _blinkAt = _rng.Random() > 0.15 ? _rng.Uniform(2.0, 5.0) : 0.25;
        return 1;
    }
}
