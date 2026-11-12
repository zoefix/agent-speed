namespace AgentSpeed.Presentation.Character;

/// <summary>What the agents of her orb are doing (spec/CHARACTERS.md, Behavior, Inputs).</summary>
public enum AgentActivity
{
    Generating,
    Tools,
    Idle,
}

/// <summary>One frame of what the app knows (motions/behavior.py, Inputs). Times are seconds.</summary>
public record struct BehaviorInputs
{
    public BehaviorInputs()
    {
    }

    /// <summary><c>Generating</c> when an agent is generating, else <c>Tools</c> when one waits for a tool, else <c>Idle</c>.</summary>
    public AgentActivity Phase { get; set; } = AgentActivity.Idle;

    /// <summary>Seconds the request has waited for its first output; null when not generating or once output flows.</summary>
    public double? Wait { get; set; }

    /// <summary>Seconds since the last response ended, while idle.</summary>
    public double Idle { get; set; }

    /// <summary>The local hour, 0–23.</summary>
    public int Hour { get; set; } = 12;

    public bool NewPeak { get; set; }

    public bool Trouble { get; set; }

    /// <summary>The pointer relative to her face center in her heights (x right, y down); null when unknown.</summary>
    public (double X, double Y)? Cursor { get; set; }

    /// <summary>The pointer is over her figure.</summary>
    public bool Over { get; set; }

    /// <summary>A single click on her figure this frame.</summary>
    public bool Click { get; set; }
}

/// <summary>Her mood: event moods (the first four, highest first) take over for a fixed time, state moods follow the agents' phase.</summary>
public enum CharacterMood
{
    Poke,
    Cheer,
    Greet,
    Trouble,
    Think,
    Work,
    Focus,
    Rest,
    Wait,
}

public static class CharacterMoods
{
    public static IReadOnlyList<CharacterMood> All { get; } =
    [
        CharacterMood.Poke, CharacterMood.Cheer, CharacterMood.Greet, CharacterMood.Trouble, CharacterMood.Think, CharacterMood.Work,
        CharacterMood.Focus, CharacterMood.Rest, CharacterMood.Wait,
    ];

    /// <summary>The mood's name in the reference (<c>poke</c>, <c>cheer</c>, ...).</summary>
    public static string Key(this CharacterMood mood) => mood switch
    {
        CharacterMood.Poke => "poke",
        CharacterMood.Cheer => "cheer",
        CharacterMood.Greet => "greet",
        CharacterMood.Trouble => "trouble",
        CharacterMood.Think => "think",
        CharacterMood.Work => "work",
        CharacterMood.Focus => "focus",
        CharacterMood.Rest => "rest",
        _ => "wait",
    };

    public static CharacterMood? Parse(string? key) => key switch
    {
        "poke" => CharacterMood.Poke,
        "cheer" => CharacterMood.Cheer,
        "greet" => CharacterMood.Greet,
        "trouble" => CharacterMood.Trouble,
        "think" => CharacterMood.Think,
        "work" => CharacterMood.Work,
        "focus" => CharacterMood.Focus,
        "rest" => CharacterMood.Rest,
        "wait" => CharacterMood.Wait,
        _ => null,
    };

    /// <summary>An event mood: started by a trigger, it lasts a fixed time.</summary>
    public static bool IsEvent(this CharacterMood mood) => mood <= CharacterMood.Trouble;

    internal static bool IsSocial(this CharacterMood mood) => mood is CharacterMood.Wait or CharacterMood.Work;
}

/// <summary>The pose a mood asks for: one of the pack's mood poses (<see cref="State"/>), or whatever pose shows already (<see cref="IsKeep"/>).</summary>
public readonly record struct MoodPose(string? State)
{
    public static MoodPose Keep => default;

    public bool IsKeep => State is null;
}

/// <summary>Where she looks in a mood (spec/CHARACTERS.md, Behavior, Looking).</summary>
public enum CharacterLook
{
    User,
    Up,
    None,
}

/// <summary>
/// Moods, poses, faces, looks and actions from the inputs (motions/behavior.py, Behavior). The director asks for the pose,
/// the face, where she looks, whether she talks and which actions she plays. Nothing here allocates once built.
/// </summary>
public sealed class CharacterBehavior
{
    /// <summary>Full-body poses a pack may carry for the moods (manifest <c>states</c>).</summary>
    public static IReadOnlyList<string> StateNames { get; } = ["think", "wait", "cheer", "sleepy", "focus", "greet"];

    /// <summary>She looks at a cursor within this many of her heights from her face.</summary>
    public const double LookRange = 2.0;

    internal const double Hold = 1.2;
    private const double ThinkFace = 3.0;
    private const double ThinkPose = 10.0;
    private const double FocusFace = 3.0;
    private const double FocusPose = 12.0;
    private const double WaitPose = 20.0;
    private const double WaitSmile = 6.0;
    private const double WaitYawn = 180.0;
    private const double RestIdle = 600.0;
    private const double RestNightIdle = 120.0;
    private const int NightEnd = 6;
    private const double Wake = 60.0;
    private const double PokeSurprise = 0.4;
    private const double EntryDelay = 0.55;
    private const double LookIn = 0.4;
    private const double LookOut = 0.8;
    private const double HeadX = 0.7;
    private const double HeadY = 0.5;
    private const double HeadReach = 0.8;
    private const double EyeReach = 0.3;
    private const double GazeOn = 0.35;
    private const double GazeOff = 0.25;
    private const double GazeDwell = 0.5;
    private const double GazeAxis = 0.75;
    private const double SmileAfter = 0.8;
    private const double GestureAfter = 4.0;
    private const double GoodbyeAfter = 1.0;
    private const double Attention = 8.0;
    private const double WinkFor = 1.2;
    private static readonly (double, double, double, double) UpTarget = (0.15, 0.3, 0.3, -0.8);
    private static readonly double[] EventLasts = [1.6, 3.2, 2.4, 2.5];

    internal static readonly string[] HoverGestures = ["wave", "peace", "thumbsup"];

    private static readonly string[] None = [];
    private static readonly string[] Doubletake = ["doubletake"];
    private static readonly string[] ChinShake = ["chin", "shake"];
    private static readonly string[] Hop = ["hop"];
    private static readonly string[] JumpjoyHop = ["jumpjoy", "hop"];
    private static readonly string[] WaveBow = ["wave", "bow"];
    private static readonly string[] Nod = ["nod"];
    private static readonly string[] Yawn = ["yawn"];
    private static readonly string[] WaveNod = ["wave", "nod"];
    private static readonly string[] ThinkPool = ["chin", "tilt", "ponder"];
    private static readonly string[] FocusPool = ["chin", "nod"];
    private static readonly string[] RestPool = ["yawn"];

    private readonly string[] _hoverOptions = new string[HoverGestures.Length];
    private readonly string[] _hoverQueue = new string[1];
    private readonly double[] _until = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    private readonly double[] _started = new double[4];
    private readonly string[] _states;
    private Rng _rng;
    private int _hoverCount;
    private BehaviorInputs _inputs = new();
    private double _stateT = Hold;
    private double _woken = double.NegativeInfinity;
    private bool _hoverDone;
    private double _attentionUntil = double.NegativeInfinity;
    private string? _faceOverride;
    private double _faceUntil = double.NegativeInfinity;
    private string[] _queue = None;
    private double _queueAt;
    private double _gazeSince;

    public CharacterBehavior(ulong seed, IEnumerable<string>? states = null)
    {
        _rng = new Rng(unchecked(seed + 1));
        var given = new HashSet<string>(states ?? [], StringComparer.Ordinal);
        _states = StateNames.Where(given.Contains).ToArray();
    }

    /// <summary>The mood poses the pack has, in <see cref="StateNames"/> order.</summary>
    public IReadOnlyList<string> States => _states;

    public BehaviorInputs Inputs => _inputs;

    public double T { get; private set; }

    public AgentActivity Phase { get; private set; } = AgentActivity.Idle;

    /// <summary>How long <see cref="Phase"/> has lasted.</summary>
    public double PhaseT { get; private set; }

    public CharacterMood StateMood { get; private set; } = CharacterMood.Wait;

    public CharacterMood Mood { get; private set; } = CharacterMood.Wait;

    /// <summary>How long <see cref="Mood"/> has lasted.</summary>
    public double MoodT { get; private set; }

    /// <summary>How long the cursor has hovered over her.</summary>
    public double OverT { get; private set; }

    public double LookWeight { get; private set; }

    /// <summary>Head and eye targets: AngleX, AngleY, EyeBallX, EyeBallY.</summary>
    public (double AngleX, double AngleY, double EyeBallX, double EyeBallY) LookTarget { get; private set; }

    /// <summary>Where her eyes look as a gaze patch: <c>left</c>, <c>right</c>, <c>up</c>, <c>down</c>, or null.</summary>
    public string? Gaze { get; private set; }

    public Talk Talk { get; } = new();

    /// <summary>Whether the shown pose has a <c>wink</c> expression (set by the director per pose).</summary>
    internal bool HasWink { get; set; }

    /// <summary>Where she looks this mood: the user, up, or nowhere in particular.</summary>
    public CharacterLook LookMode => Mood == CharacterMood.Think ? CharacterLook.Up
        : (Mood is CharacterMood.Focus or CharacterMood.Rest) ? CharacterLook.None
        : CharacterLook.User;

    /// <summary>The cursor is over her in a mood that answers it: she starts nothing from her pool meanwhile.</summary>
    internal bool Attending => _inputs.Over && Mood.IsSocial();

    internal bool Talking => Mood == CharacterMood.Work && _inputs.Phase == AgentActivity.Generating && _inputs.Wait is null;

    /// <summary>Replaces the hover gestures the shown pose can play (in <see cref="HoverGestures"/> order).</summary>
    internal void ClearHoverOptions() => _hoverCount = 0;

    internal void AddHoverOption(string gid) => _hoverOptions[_hoverCount++] = gid;

    private bool HasState(string name) => Array.IndexOf(_states, name) >= 0;

    // Moods

    private bool Resting(in BehaviorInputs i)
    {
        var night = 0 <= i.Hour && i.Hour < NightEnd;
        var longIdle = i.Idle >= RestIdle || (night && i.Idle >= RestNightIdle);
        return i.Phase == AgentActivity.Idle && longIdle && T - _woken >= Wake;
    }

    private CharacterMood StateWanted(in BehaviorInputs i) => i.Phase switch
    {
        AgentActivity.Generating => i.Wait is { } wait && wait >= ThinkFace ? CharacterMood.Think : CharacterMood.Work,
        AgentActivity.Tools => PhaseT >= FocusFace ? CharacterMood.Focus : CharacterMood.Work,
        _ => Resting(i) ? CharacterMood.Rest : CharacterMood.Wait,
    };

    private void Start(CharacterMood mood, ref int fresh)
    {
        var e = (int)mood;
        _until[e] = T + EventLasts[e];
        _started[e] = T;
        fresh |= 1 << e;
    }

    /// <summary>The actions a mood starts with, and the earliest start.</summary>
    private (string[] Actions, double At) Entry(CharacterMood mood, CharacterMood cameFrom) => mood switch
    {
        CharacterMood.Poke => (Doubletake, T),
        CharacterMood.Trouble => (ChinShake, T),
        CharacterMood.Cheer => (HasState("cheer") ? Hop : JumpjoyHop, T + EntryDelay),
        CharacterMood.Greet => (HasState("greet") ? None : WaveBow, T + EntryDelay),
        CharacterMood.Wait when cameFrom is CharacterMood.Work or CharacterMood.Think or CharacterMood.Focus => (Nod, T),
        CharacterMood.Rest => (Yawn, T + EntryDelay),
        _ => (None, T),
    };

    private void Moods(double dt, in BehaviorInputs i)
    {
        var fresh = 0;
        if (i.Phase != Phase)
        {
            if (Phase == AgentActivity.Idle && StateMood == CharacterMood.Rest)
            {
                Start(CharacterMood.Greet, ref fresh);
            }

            Phase = i.Phase;
            PhaseT = 0;
        }
        else
        {
            PhaseT += dt;
        }

        if (i.NewPeak)
        {
            Start(CharacterMood.Cheer, ref fresh);
        }

        if (i.Trouble)
        {
            Start(CharacterMood.Trouble, ref fresh);
        }

        if (i.Click)
        {
            if (StateMood == CharacterMood.Rest)
            {
                _woken = T;
            }

            Start(CharacterMood.Poke, ref fresh);
        }

        var previousState = StateMood;
        _stateT += dt;
        var wanted = StateWanted(i);
        if (wanted != StateMood && _stateT >= Hold)
        {
            StateMood = wanted;
            _stateT = 0;
        }

        var before = Mood;
        Mood = StateMood;
        for (var e = 0; e < _until.Length; e++)
        {
            if (_until[e] > T)
            {
                Mood = (CharacterMood)e;
                break;
            }
        }

        if (Mood != before)
        {
            MoodT = 0;
            _queue = None;
        }
        else
        {
            MoodT += dt;
        }

        if ((fresh & (1 << (int)Mood)) != 0 || (Mood == StateMood && StateMood != previousState))
        {
            (_queue, _queueAt) = Entry(Mood, previousState);
        }
    }

    // The cursor

    private void Hover(double dt, in BehaviorInputs i, SpeedBand band)
    {
        var social = Mood.IsSocial();
        if (i.Over)
        {
            OverT += dt;
            if (OverT >= SmileAfter)
            {
                _attentionUntil = T + Attention;
            }

            if (OverT >= GestureAfter && !_hoverDone && social)
            {
                _hoverDone = true;
                if (_hoverCount > 0)
                {
                    _hoverQueue[0] = _hoverOptions[_rng.Index(_hoverCount)];
                    _queue = _hoverQueue;
                    _queueAt = T;
                }
                else if (HasWink)
                {
                    _faceOverride = "wink";
                    _faceUntil = T + WinkFor;
                }
            }

            return;
        }

        if (OverT >= GoodbyeAfter && social && band is SpeedBand.Idle or SpeedBand.Cruise)
        {
            _queue = WaveNod;
            _queueAt = T;
        }

        OverT = 0;
        _hoverDone = false;
    }

    private string Direction(double dx, double dy)
    {
        var horizontal = Math.Abs(dx) >= Math.Abs(dy);
        if (Gaze is "left" or "right")
        {
            horizontal = Math.Abs(dx) >= GazeAxis * Math.Abs(dy);
        }
        else if (Gaze is "up" or "down")
        {
            horizontal = GazeAxis * Math.Abs(dx) > Math.Abs(dy);
        }

        if (horizontal)
        {
            return dx > 0 ? "right" : "left";
        }

        return dy > 0 ? "down" : "up";
    }

    private static double Clamp1(double v) => Math.Max(-1, Math.Min(1, v));

    private void Look(double dt, in BehaviorInputs i)
    {
        var mode = LookMode;
        var near = i.Cursor is { } c && CharacterPhysics.Hypot(c.X, c.Y) <= LookRange;
        var on = true;
        string? wanted = null;
        if (mode == CharacterLook.Up)
        {
            LookTarget = UpTarget;
            wanted = "up";
        }
        else if (mode == CharacterLook.User && near)
        {
            var (dx, dy) = i.Cursor!.Value;
            LookTarget = (HeadX * Clamp1(dx / HeadReach), -HeadY * Clamp1(dy / HeadReach), Clamp1(dx / EyeReach), Clamp1(dy / EyeReach));
            var far = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (far >= (Gaze is not null ? GazeOff : GazeOn))
            {
                wanted = Direction(dx, dy);
            }
        }
        else if (mode == CharacterLook.User && Mood.IsEvent())
        {
            LookTarget = (0, 0, 0, 0);
        }
        else
        {
            on = false;
        }

        LookWeight = on ? Math.Min(1, LookWeight + dt / LookIn) : Math.Max(0, LookWeight - dt / LookOut);
        if (!string.Equals(wanted, Gaze, StringComparison.Ordinal) && T - _gazeSince >= GazeDwell)
        {
            Gaze = wanted;
            _gazeSince = T;
        }
    }

    internal void Update(double dt, in BehaviorInputs inputs, SpeedBand band)
    {
        T += dt;
        _inputs = inputs;
        Moods(dt, inputs);
        Hover(dt, inputs, band);
        Look(dt, inputs);
    }

    // What the director asks for

    /// <summary>The mood pose to show: a state the pack has, <see cref="MoodPose.Keep"/>, or null for the band pose.</summary>
    public MoodPose? Pose()
    {
        string? name = null;
        switch (Mood)
        {
            case CharacterMood.Poke or CharacterMood.Trouble:
                return MoodPose.Keep;
            case CharacterMood.Cheer:
                name = "cheer";
                break;
            case CharacterMood.Greet:
                name = "greet";
                break;
            case CharacterMood.Rest:
                name = "sleepy";
                break;
            case CharacterMood.Think when _inputs.Wait is { } wait && wait >= ThinkPose:
                name = "think";
                break;
            case CharacterMood.Focus when PhaseT >= FocusPose:
                name = "focus";
                break;
            case CharacterMood.Wait when _inputs.Idle >= WaitPose && T >= _attentionUntil:
                name = "wait";
                break;
        }

        return name is not null && HasState(name) ? new MoodPose(name) : null;
    }

    /// <summary>
    /// The actions she picks from between others and the band whose signature actions join them (null: the mood's own
    /// list, filtered by what the profile allows); an event mood picks none.
    /// </summary>
    internal (IReadOnlyList<string> Actions, SpeedBand? Signature) Pool(SpeedBand band, IReadOnlyList<string> bandPool, IReadOnlyList<string> idlePool,
        IReadOnlyList<string> idleEarlyPool)
    {
        if (Mood.IsEvent())
        {
            return (None, null);
        }

        switch (Mood)
        {
            case CharacterMood.Think:
                return (ThinkPool, null);
            case CharacterMood.Focus:
                return (FocusPool, null);
            case CharacterMood.Rest:
                return (RestPool, null);
            case CharacterMood.Wait:
                return (_inputs.Idle >= WaitYawn ? idlePool : idleEarlyPool, SpeedBand.Idle);
            default:
                return (bandPool, band);
        }
    }

    /// <summary>The gap range between actions: the mood's own, or the band's (the idle band's while waiting).</summary>
    internal (double Low, double High) Gaps(SpeedBand band, IReadOnlyDictionary<SpeedBand, (double Low, double High)> bandGaps) => Mood switch
    {
        _ when Mood.IsEvent() => bandGaps[band],
        CharacterMood.Think => (2.0, 4.0),
        CharacterMood.Focus => (3.0, 6.0),
        CharacterMood.Rest => (20.0, 40.0),
        CharacterMood.Wait => bandGaps[SpeedBand.Idle],
        _ => bandGaps[band],
    };

    /// <summary>The queued actions to try in turn once no action plays, when their time has come.</summary>
    internal string[] TakeQueue()
    {
        if (_queue.Length == 0 || T < _queueAt)
        {
            return None;
        }

        var output = _queue;
        _queue = None;
        return output;
    }

    /// <summary>The expression she means to show (a patch name, unchecked against the pose), or null.</summary>
    internal string? Face(CharacterMotion? action, string? eventFace, SpeedBand band)
    {
        if (Mood == CharacterMood.Poke)
        {
            return T - _started[(int)CharacterMood.Poke] < PokeSurprise ? "surprised" : "laugh";
        }

        if (action is { Name: "yawn" })
        {
            return "yawn";
        }

        if (!Mood.IsSocial())
        {
            return MoodFace(Mood);
        }

        if (T < _faceUntil)
        {
            return _faceOverride;
        }

        if (action is { Expression: { Length: > 0 } expression })
        {
            if (OwnFace(action.Name) is { } own)
            {
                return own;
            }

            return ActionFace(expression, out var mapped) ? mapped : expression;
        }

        if (Mood == CharacterMood.Work && eventFace is { Length: > 0 })
        {
            return ActionFace(eventFace, out var mapped) ? mapped : eventFace;
        }

        var face = Mood == CharacterMood.Work ? BandFace(band) : _inputs.Idle < WaitSmile ? "smile" : null;
        if (face is null && OverT >= SmileAfter)
        {
            face = "smile";
        }

        return face;
    }

    private static string? MoodFace(CharacterMood mood) => mood switch
    {
        CharacterMood.Cheer => "excited",
        CharacterMood.Greet => "smile",
        CharacterMood.Trouble => "worried",
        CharacterMood.Think => "thinking",
        CharacterMood.Focus => "focused",
        CharacterMood.Rest => "sleepy",
        _ => null,
    };

    private static string? BandFace(SpeedBand band) => band switch
    {
        SpeedBand.Cruise or SpeedBand.Boost => "smile",
        SpeedBand.Overdrive => "focused",
        SpeedBand.Redline => "excited",
        _ => null,
    };

    /// <summary>The expressions actions name, as face patches; <c>talk</c> leaves the face as it is.</summary>
    private static bool ActionFace(string expression, out string? face)
    {
        face = expression switch
        {
            "smile" => "smile",
            "happy" => "excited",
            "surprised" => "surprised",
            "determined" => "focused",
            "sleepy" => "sleepy",
            _ => null,
        };
        return face is not null || expression == "talk";
    }

    private static string? OwnFace(string action) => action switch
    {
        "yawn" => "yawn",
        "giggle" => "laugh",
        _ => null,
    };

    /// <summary>How long a dozing blink holds the eyes shut, from the behavior's generator.</summary>
    internal double DozeHold(double low, double high) => _rng.Uniform(low, high);

    /// <summary>Steps the lip flaps with the behavior's own generator.</summary>
    internal string? Speak(double dt, bool on, int level, int shapes, bool clear) => Talk.Update(dt, on, level, shapes, clear, ref _rng);
}

/// <summary>
/// Anime lip flaps in phrases, not lip sync (motions/behavior.py, Talk): a group of 2–4 phrases of 3–8 syllables, a pause
/// between phrases, a silence after the group. A group waits until the face is clear of expressions before its first
/// syllable; <see cref="Speaking"/> is true from then until its silence.
/// </summary>
public sealed class Talk
{
    public enum TalkStage
    {
        Ready,
        Open,
        Gap,
        Pause,
        Silence,
    }

    /// <summary>The open shapes and how often each comes (<c>mouth-i</c>, the teeth, never talks); a shape set is a bit mask over them.</summary>
    public static IReadOnlyList<(string Shape, double Weight)> Shapes { get; } = [("a", 0.6), ("o", 0.25), ("u", 0.15)];

    private const int PhraseSyllablesMin = 3;
    private const int PhraseSyllablesSpread = 6;
    private const int GroupPhrasesMin = 2;
    private const int GroupPhrasesSpread = 3;
    private const double RateLow = 5.0;
    private const double RateHigh = 6.0;
    private const double RateGain = 0.1;
    private const double RateMax = 7.0;
    private const double OpenLow = 0.55;
    private const double OpenHigh = 0.70;
    private const double PauseLow = 0.4;
    private const double PauseHigh = 1.2;
    private const double SilenceLow = 2.0;
    private const double SilenceHigh = 5.0;

    private double _left;
    private double _gap;
    private int _syllables;
    private int _phrases;
    private double _rate = RateLow;

    public bool On { get; private set; }

    /// <summary>The open shape now, null while closed.</summary>
    public string? Shape { get; private set; }

    public TalkStage? Stage { get; private set; }

    public bool Speaking => Stage != TalkStage.Silence;

    private static string Pick(int shapes, ref Rng rng)
    {
        var total = 0.0;
        string? first = null;
        for (var i = 0; i < Shapes.Count; i++)
        {
            if ((shapes & (1 << i)) != 0)
            {
                total += Shapes[i].Weight;
                first ??= Shapes[i].Shape;
            }
        }

        var x = rng.Random() * total;
        var last = first!;
        for (var i = 0; i < Shapes.Count; i++)
        {
            if ((shapes & (1 << i)) == 0)
            {
                continue;
            }

            x -= Shapes[i].Weight;
            if (x < 0)
            {
                return Shapes[i].Shape;
            }

            last = Shapes[i].Shape;
        }

        return last;
    }

    private void Phrase(int level, ref Rng rng)
    {
        _syllables = PhraseSyllablesMin + rng.Index(PhraseSyllablesSpread);
        _rate = Math.Min(RateMax, rng.Uniform(RateLow, RateHigh) + RateGain * level);
    }

    private void Syllable(int shapes, ref Rng rng)
    {
        Shape = Pick(shapes, ref rng);
        var span = 1.0 / _rate;
        var opened = span * rng.Uniform(OpenLow, OpenHigh);
        Stage = TalkStage.Open;
        _gap = span - opened;
        _left += opened;
    }

    /// <summary>The open shape now, or null for closed. <paramref name="shapes"/> masks the open shapes the pose has, over <see cref="Shapes"/>.</summary>
    internal string? Update(double dt, bool talking, int level, int shapes, bool clear, ref Rng rng)
    {
        if (!talking || shapes == 0)
        {
            On = false;
            Shape = null;
            Stage = null;
            _left = 0;
            return null;
        }

        if (!On)
        {
            On = true;
            Stage = TalkStage.Ready;
            _left = 0;
            _phrases = GroupPhrasesMin + rng.Index(GroupPhrasesSpread);
        }

        if (Stage == TalkStage.Ready)
        {
            if (!clear)
            {
                return null;
            }

            _left = 0;
            Phrase(level, ref rng);
            Syllable(shapes, ref rng);
        }

        _left -= dt;
        while (_left <= 0)
        {
            switch (Stage)
            {
                case TalkStage.Open:
                    Stage = TalkStage.Gap;
                    Shape = null;
                    _left += _gap;
                    _syllables--;
                    break;
                case TalkStage.Gap when _syllables > 0:
                    Syllable(shapes, ref rng);
                    break;
                case TalkStage.Gap:
                    _phrases--;
                    if (_phrases > 0)
                    {
                        Stage = TalkStage.Pause;
                        _left += rng.Uniform(PauseLow, PauseHigh);
                    }
                    else
                    {
                        Stage = TalkStage.Silence;
                        _left += rng.Uniform(SilenceLow, SilenceHigh);
                    }

                    break;
                case TalkStage.Pause:
                    Phrase(level, ref rng);
                    Syllable(shapes, ref rng);
                    break;
                default:
                    Stage = TalkStage.Ready;
                    _left = 0;
                    _phrases = GroupPhrasesMin + rng.Index(GroupPhrasesSpread);
                    return Shape;
            }
        }

        return Shape;
    }
}
