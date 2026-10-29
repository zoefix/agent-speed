using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>One end of glass's value arc on display: where it is (a share of the sweep) and how visible; alpha 0 draws none.</summary>
public readonly record struct BoardScoreTip(double At, double Alpha);

/// <summary>
/// Glass's value arc at one moment: solid from the start to <see cref="Settled"/>, then on to <see cref="Changing"/> at
/// <see cref="ChangingAlpha"/> (a crossfade's growing or receding stretch), with up to two tips (the old and the new end).
/// </summary>
public readonly record struct BoardScoreSweep(double Settled, double Changing, double ChangingAlpha, BoardScoreTip First, BoardScoreTip Second);

/// <summary>
/// The hero's 性能 over time (DESIGN 12, <i>Motion</i> and <i>Provisional mark</i>; 2c, Updates, motion and quiet): a change of at
/// least 1 % and 3 counts through every integer, up or down, over <c>clamp(0.3 + 0.35 × |log2(new / old)|, 0.3, 0.7)</c> s with
/// easeOutCubic timing, the arc's end and the meter following the shown value on the log scale; smaller changes replace the
/// characters; a change to or from <c>–</c> crossfades (0.15 s); a change between live and resting crossfades the color (0.2 s,
/// kept with Reduce Motion); the provisional mark fades in and out with the state (0.25 s; Reduce Motion: at once); Reduce
/// Motion turns every value change into a 0.15 s crossfade. Nothing runs between changes.
/// </summary>
public sealed class BoardHeroScore
{
    private readonly PerfTicker _ticker = new(PerfSurface.BoardHero);
    private PerfSetReading _reading = PerfSetReading.None;
    private bool _live;
    private bool _fromLive;
    private bool _fromProvisional;
    private double _formStart = double.NegativeInfinity;
    private bool _provisional;
    private double _proofStart = double.NegativeInfinity;

    /// <summary>The reading on display (its target, while a change runs).</summary>
    public PerfSetReading Reading => _reading;

    /// <summary>The change in progress or the last one.</summary>
    public PerfChange Change => _ticker.Change;

    /// <summary>The number has a value (none draws <c>–</c>, no value arc and no tip).</summary>
    public bool HasScore => _reading.HasScore;

    /// <summary>Takes a tick's reading at <paramref name="now"/>; <paramref name="animate"/> false shows it at once.</summary>
    public void Apply(PerfSetReading reading, double now, bool animate, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var live = reading.HasScore && reading.Form == PerfForm.Live;
        if (reading.Shown != _ticker.Target)
        {
            _fromLive = Liveness(now) >= 0.5;
            _fromProvisional = _reading.HasScore && Proof(now) >= 0.5;
        }

        _ticker.Set(reading.Shown, now, reduceMotion, animate);
        _formStart = animate && _reading.HasScore && reading.HasScore && live != _live ? now : _formStart;
        if (!animate)
        {
            _formStart = double.NegativeInfinity;
        }

        var provisional = reading.IsProvisional;
        if (provisional != _provisional)
        {
            _proofStart = animate && !reduceMotion ? now : double.NegativeInfinity;
        }

        _live = live;
        _provisional = provisional;
        _reading = reading;
    }

    /// <summary>Ends every change at once.</summary>
    public void Settle()
    {
        _ticker.Set(_ticker.Target, 0, true, animate: false);
        _formStart = double.NegativeInfinity;
        _proofStart = double.NegativeInfinity;
    }

    /// <summary>A count, a crossfade, the color's or the provisional mark's fade runs (60 fps, in the roll class).</summary>
    public bool Animating(double now) =>
        _ticker.IsAnimating(now) || now - _formStart < PerfMotion.FormSeconds || now - _proofStart < PerfProvisionalMark.FadeSeconds;

    /// <summary>The integer on display (a count's intermediate value); null while none.</summary>
    public int? Shown(double now) => _ticker.Shown(now);

    /// <summary>
    /// The number's text at <paramref name="now"/>: <paramref name="compact"/> for glass's dial, the full form for neon and
    /// timing (DESIGN 12, <i>Number formats</i>).
    /// </summary>
    public string Text(double now, bool compact) => Number(Shown(now), compact);

    /// <summary><see cref="Text"/> written into <paramref name="destination"/> without allocating, for frames; returns its length.</summary>
    public int Format(Span<char> destination, double now, bool compact) => Format(destination, Shown(now), compact);

    /// <summary>The text fading out during a crossfade, else null.</summary>
    public string? Outgoing(double now, bool compact) => HasOutgoing(now) ? Number(_ticker.Change.From, compact) : null;

    /// <summary><see cref="Outgoing"/> written into <paramref name="destination"/>; 0 when nothing fades out.</summary>
    public int FormatOutgoing(Span<char> destination, double now, bool compact) =>
        HasOutgoing(now) ? Format(destination, _ticker.Change.From, compact) : 0;

    /// <summary>The crossfade's opacities of the outgoing and the incoming number (0 and 1 outside a crossfade).</summary>
    public (double Outgoing, double Incoming) Fade(double now) => _ticker.Fade(now);

    /// <summary>The outgoing number was live, and carried the provisional mark (its colors while it fades out).</summary>
    public bool OutgoingLive => _fromLive;

    public bool OutgoingProvisional => _fromProvisional;

    /// <summary>How live the colors are: 1 live, 0 resting or none, between them while the 0.2 s color crossfade runs.</summary>
    public double Liveness(double now)
    {
        var target = _live ? 1.0 : 0;
        var t = (now - _formStart) / PerfMotion.FormSeconds;
        return t is >= 0 and < 1 ? (1 - target) + (2 * target - 1) * Easing.OutQuad(t) : target;
    }

    /// <summary>How present the provisional mark is: 1 provisional, 0 settled or none, between them while it fades.</summary>
    public double Proof(double now)
    {
        var target = _provisional ? 1.0 : 0;
        var t = (now - _proofStart) / PerfProvisionalMark.FadeSeconds;
        return t is >= 0 and < 1 ? (1 - target) + (2 * target - 1) * Easing.OutQuad(t) : target;
    }

    /// <summary>How live the arc, the tip and the meter look and how present their provisional mark is: the outgoing value's while it fades to none.</summary>
    public (double Live, double Proof) Look(double now) =>
        HasScore ? (Liveness(now), Proof(now)) : _ticker.IsAnimating(now) ? (_fromLive ? 1 : 0, _fromProvisional ? 1 : 0) : (0, 0);

    /// <summary>Glass's value arc on the log scale: the count's eased value, a crossfade's two ends, or the shown value.</summary>
    public BoardScoreSweep Sweep(double now)
    {
        var change = _ticker.Change;
        if (_ticker.IsAnimating(now))
        {
            if (change.Kind == PerfChangeKind.Crossfade)
            {
                var t = _ticker.Fade(now).Incoming;
                var from = Share(change.From);
                var to = Share(change.To);
                if (from is { } a && to is { } b)
                {
                    return new BoardScoreSweep(Math.Min(a, b), Math.Max(a, b), b > a ? t : 1 - t, new BoardScoreTip(a, 1 - t), new BoardScoreTip(b, t));
                }

                if (to is { } incoming)
                {
                    return new BoardScoreSweep(0, incoming, t, default, new BoardScoreTip(incoming, t));
                }

                return from is { } outgoing ? new BoardScoreSweep(0, outgoing, 1 - t, new BoardScoreTip(outgoing, 1 - t), default) : default;
            }

            if (change.Kind == PerfChangeKind.Count && change.From is { } start && change.To is { } end)
            {
                var value = PerfScale.Fraction(start + (end - (double)start) * Easing.OutCubic(_ticker.Progress(now)));
                return new BoardScoreSweep(value, value, 0, new BoardScoreTip(value, 1), default);
            }
        }

        return Share(change.To) is { } f ? new BoardScoreSweep(f, f, 0, new BoardScoreTip(f, 1), default) : default;
    }

    /// <summary>
    /// Neon's meter: the lit counts before and after a crossfade and its progress (a count lights the segments of the shown
    /// value; otherwise both counts are the target's).
    /// </summary>
    public (int From, int To, double Progress) Meter(double now)
    {
        var change = _ticker.Change;
        if (_ticker.IsAnimating(now) && change.Kind == PerfChangeKind.Crossfade)
        {
            return (Lit(change.From), Lit(change.To), _ticker.Fade(now).Incoming);
        }

        var lit = Lit(Shown(now));
        return (lit, lit, 1);
    }

    /// <summary><c>lit = round(f × 12)</c>, <c>f</c> the log scale: 6 lit at 1000, all 12 from 4000.</summary>
    public static int Lit(int? shown) => shown is { } value ? Math.Clamp(PerfScale.LitSegments(value, BoardGaugeSpec.Segments), 0, BoardGaugeSpec.Segments) : 0;

    /// <summary>The number as drawn: compact (glass's dial) or full (neon, timing), or <c>–</c>.</summary>
    public static string Number(int? shown, bool compact) => compact ? PerfNumber.Compact(shown) : PerfNumber.Full(shown);

    /// <summary>Room for any number <see cref="Format"/> writes.</summary>
    public const int MaxLength = PerfNumber.MaxCompactLength;

    private static int Format(Span<char> destination, int? shown, bool compact) =>
        compact ? PerfNumber.FormatCompact(shown, destination) : PerfNumber.FormatFull(shown, destination);

    private bool HasOutgoing(double now) => _ticker.Change.Kind == PerfChangeKind.Crossfade && _ticker.IsAnimating(now);

    private static double? Share(int? shown) => shown is { } value ? PerfScale.Fraction(value) : null;
}
