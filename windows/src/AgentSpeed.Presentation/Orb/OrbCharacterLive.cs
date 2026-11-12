using AgentSpeed.Core;
using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Orb;

/// <summary>Where the pointer is for a living character: her orb window reads it when her renderer asks (CHARACTERS 5, <i>Behavior</i>, <i>Inputs</i>).</summary>
public interface IOrbCharacterPointer
{
    /// <summary>
    /// The pointer as fractions of the orb window's side (x right, y down, 0…1 inside it), or null while it may not be
    /// read: the orb is click-through, hidden, closing or being dragged, the session is locked, or the pointer is on
    /// another display.
    /// </summary>
    (double X, double Y)? PointerFraction { get; }

    bool IsDragging { get; }
}

/// <summary>
/// What a living character reacts to, shared by the orb's feed (the agents), its window (the pointer and clicks) and
/// her renderer, which reads it every frame (CHARACTERS 5, <i>Behavior</i>, <i>Inputs</i>). Plain storage: writing it
/// never redraws. Times are seconds on the orbs' shared clock.
/// </summary>
public sealed class OrbCharacterLive
{
    public AgentActivity Phase { get; set; } = AgentActivity.Idle;

    /// <summary>The request still waiting for its first output started; null when none waits.</summary>
    public double? WaitingSince { get; set; }

    /// <summary>The newest response of the orb's set ended (the orb's first reading while there is none).</summary>
    public double IdleSince { get; set; }

    /// <summary>The local hour, 0–23.</summary>
    public int Hour { get; set; } = 12;

    public int Peaks { get; private set; }

    public int Troubles { get; private set; }

    /// <summary>The orb's new peaks cheer her only while peaks are celebrated (Settings).</summary>
    public bool CelebratesPeaks { get; set; } = true;

    /// <summary>The orb's window; null for previews and harness orbs.</summary>
    public IOrbCharacterPointer? Pointer { get; set; }

    public int Clicks { get; private set; }

    /// <summary>Where the last single click was pressed, as fractions of the window's side.</summary>
    public (double X, double Y) ClickFraction { get; private set; }

    /// <summary>The frame harness scripts the cursor (<see cref="ScriptedCursor"/>) instead of reading the pointer.</summary>
    public bool Scripted { get; set; }

    /// <summary>The scripted cursor relative to her face in her heights (x right, y down); null when absent.</summary>
    public (double X, double Y)? ScriptedCursor { get; set; }

    public void NotePeak() => Peaks = unchecked(Peaks + 1);

    public void NoteTrouble() => Troubles = unchecked(Troubles + 1);

    /// <summary>A single click on the orb (CHARACTERS 5, <c>click</c>); her renderer tells whether it landed on her figure.</summary>
    public void Click((double X, double Y) fraction)
    {
        ClickFraction = fraction;
        Clicks = unchecked(Clicks + 1);
    }

    /// <summary>A scripted click on her figure (the frame harness).</summary>
    public void ScriptedClick() => Clicks = unchecked(Clicks + 1);

    /// <summary>Takes one engine tick of the orb's agents, <paramref name="now"/> on the shared clock being <paramref name="wallNow"/>.</summary>
    public void Update(CharacterActivity activity, double now, DateTimeOffset wallNow)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Phase = activity.Phase switch
        {
            AgentPhase.Generating => AgentActivity.Generating,
            AgentPhase.Tools => AgentActivity.Tools,
            _ => AgentActivity.Idle,
        };
        WaitingSince = activity.WaitingSince is { } since ? ClockOf(since, now, wallNow) : null;
        IdleSince = ClockOf(activity.IdleSince, now, wallNow);
        Hour = wallNow.ToLocalTime().Hour;
    }

    /// <summary>A wall-clock instant on the shared clock, <paramref name="now"/> being <paramref name="wallNow"/>.</summary>
    public static double ClockOf(DateTimeOffset instant, double now, DateTimeOffset wallNow) => now - (wallNow - instant).TotalSeconds;

    /// <summary>A point given as fractions of the window's side on her canvas (DIP, y down), with the content scale around its center undone.</summary>
    public static (double X, double Y) CanvasPoint((double X, double Y) fraction, double canvasSide, double contentScale)
    {
        var c = canvasSide / 2;
        var s = Math.Max(contentScale, 0.01);
        return (c + (fraction.X * canvasSide - c) / s, c + (fraction.Y * canvasSide - c) / s);
    }
}
