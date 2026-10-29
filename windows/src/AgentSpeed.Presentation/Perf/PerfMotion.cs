using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Perf;

/// <summary>Where a score is drawn: each surface has its own way to change (DESIGN 12, <i>Motion</i>).</summary>
public enum PerfSurface
{
    /// <summary>The orb readout: counts at 30 fps.</summary>
    Orb,

    /// <summary>The board hero: counts in the roll class (60 fps); the arc's end and the meter follow the shown value.</summary>
    BoardHero,

    /// <summary>The open score card's number: counts; its scale follows.</summary>
    Card,

    /// <summary>The dashboard agent row: counts.</summary>
    DashboardRow,

    /// <summary>Board rows: cached content, characters replaced in place.</summary>
    BoardRow,

    /// <summary>The taskbar block: a status item, characters replaced in place.</summary>
    Taskbar,
}

public enum PerfChangeKind
{
    None,

    /// <summary>The characters change in place, in one frame.</summary>
    Replace,

    /// <summary>The integer counts through the intermediate values with easeOutCubic timing, up or down.</summary>
    Count,

    /// <summary>An opacity change: none ↔ a value, and every change under Reduce Motion.</summary>
    Crossfade,
}

/// <summary>How one change of the shown integer animates.</summary>
public readonly record struct PerfChange(PerfChangeKind Kind, int? From, int? To, double Duration)
{
    public static PerfChange Still(int? value) => new(PerfChangeKind.None, value, value, 0);

    /// <summary>Linear progress 0..1 at <paramref name="elapsed"/> seconds.</summary>
    public double Progress(double elapsed) => Duration > 0 ? Easing.Clamp01(elapsed / Duration) : 1;

    /// <summary>The integer to draw at <paramref name="elapsed"/>: a count's intermediate value, otherwise the target.</summary>
    public int? ValueAt(double elapsed)
    {
        if (Kind != PerfChangeKind.Count || From is not { } from || To is not { } to)
        {
            return To;
        }

        return (int)Math.Round(from + (to - (double)from) * Easing.OutCubic(Progress(elapsed)), MidpointRounding.AwayFromZero);
    }

    /// <summary>A crossfade's opacities of the outgoing and the incoming value.</summary>
    public (double Outgoing, double Incoming) Fade(double elapsed)
    {
        if (Kind != PerfChangeKind.Crossfade)
        {
            return (0, 1);
        }

        var t = Easing.OutQuad(Progress(elapsed));
        return (1 - t, t);
    }

    public bool IsRunning(double elapsed) => Kind is PerfChangeKind.Count or PerfChangeKind.Crossfade && elapsed < Duration;
}

/// <summary>
/// The score's motion (DESIGN 12, <i>Motion</i>): calm in both directions — a change of at least 1 % and at least 3 counts
/// over <c>clamp(0.3 + 0.35 × |log2(new / old)|, 0.3, 0.7)</c> s; smaller changes replace the characters; a drop is never
/// red, shaken or flashed; nothing animates continuously.
/// </summary>
public static class PerfMotion
{
    public const int CountMinimum = 3;
    public const double CountShare = 0.01;
    public const double CountBase = 0.3;
    public const double CountPerDoubling = 0.35;
    public const double CountMax = 0.7;

    /// <summary>none ↔ a value, and every change under Reduce Motion.</summary>
    public const double CrossfadeSeconds = 0.15;

    /// <summary>Live ↔ resting crossfades the color (an opacity change, kept with Reduce Motion).</summary>
    public const double FormSeconds = 0.2;

    /// <summary>A count's frame rates: orbs at 30 fps, boards in the roll class at 60 fps.</summary>
    public const double OrbFps = 30;

    public const double BoardFps = 60;

    /// <summary>Whether a change from <paramref name="from"/> to <paramref name="to"/> is large enough to count.</summary>
    public static bool Counts(int from, int to)
    {
        var delta = Math.Abs((long)to - from);
        return delta >= CountMinimum && delta >= CountShare * Math.Max(Math.Abs(from), 1);
    }

    /// <summary>+10 % lands in 0.35 s, a doubling in 0.65 s.</summary>
    public static double CountDuration(int from, int to)
    {
        if (from <= 0 || to <= 0)
        {
            return CountMax;
        }

        return Math.Clamp(CountBase + CountPerDoubling * Math.Abs(Math.Log2((double)to / from)), CountBase, CountMax);
    }

    public static PerfChange Plan(int? from, int? to, PerfSurface surface, bool reduceMotion)
    {
        if (from == to)
        {
            return PerfChange.Still(to);
        }

        if (reduceMotion || from is null || to is null)
        {
            return new PerfChange(PerfChangeKind.Crossfade, from, to, CrossfadeSeconds);
        }

        if (surface is PerfSurface.BoardRow or PerfSurface.Taskbar || !Counts(from.Value, to.Value))
        {
            return new PerfChange(PerfChangeKind.Replace, from, to, 0);
        }

        return new PerfChange(PerfChangeKind.Count, from, to, CountDuration(from.Value, to.Value));
    }
}

/// <summary>
/// One surface's shown score over time: plans each change and retargets a running count from the integer on display, with
/// a fresh duration, so a value that arrives mid-count never jumps back. Frames are needed only while <see cref="IsAnimating"/>.
/// </summary>
public sealed class PerfTicker(PerfSurface surface)
{
    private PerfChange _change = PerfChange.Still(null);
    private double _start = double.NegativeInfinity;
    private bool _initialised;

    public PerfSurface Surface { get; } = surface;

    public PerfChange Change => _change;

    public int? Target => _change.To;

    /// <summary>
    /// Takes a new shown value at <paramref name="now"/> seconds. The first value shows at once; so does any value with
    /// <paramref name="animate"/> false, which also settles a change in progress.
    /// </summary>
    public void Set(int? value, double now, bool reduceMotion, bool animate = true)
    {
        if (!_initialised || !animate)
        {
            _initialised = true;
            _change = PerfChange.Still(value);
            _start = now;
            return;
        }

        if (value == _change.To)
        {
            return;
        }

        _change = PerfMotion.Plan(Shown(now), value, Surface, reduceMotion);
        _start = now;
    }

    /// <summary>The integer to draw at <paramref name="now"/>.</summary>
    public int? Shown(double now) => _change.ValueAt(now - _start);

    /// <summary>The crossfade's opacities at <paramref name="now"/> (outgoing 0 and incoming 1 outside a crossfade).</summary>
    public (double Outgoing, double Incoming) Fade(double now) => _change.Fade(now - _start);

    /// <summary>The value fading out during a crossfade, else null.</summary>
    public int? Outgoing(double now) =>
        _change.Kind == PerfChangeKind.Crossfade && _change.IsRunning(now - _start) ? _change.From : null;

    public bool IsAnimating(double now) => _change.IsRunning(now - _start);

    /// <summary>Linear progress of the running change (1 when settled).</summary>
    public double Progress(double now) => _change.Progress(now - _start);
}
