using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>How a row springs to a new place per style (DESIGN 2c Rows: glass critically damped, settle ≈ 0.45 s; timing
/// damping 0.72, slight overshoot; neon 0.9).</summary>
public static class BoardRowSprings
{
    public static DampedSpring For(BoardStyle style, double initial) => style switch
    {
        BoardStyle.Timing => new DampedSpring(0.45, 0.72, initial),
        BoardStyle.Neon => new DampedSpring(0.45, 0.9, initial),
        _ => new DampedSpring(0.43, 1, initial),
    };
}

/// <summary>
/// One row's motion in a board (DESIGN 2c Rows): its place (a spring to the plan's y), entering (fade in 0.3 s from 10 pt
/// below), leaving (fade out 0.3 s), and the value's flash (a rise of more than 5 % flashes warm for 0.8 s, a fall dims it).
/// With Reduce Motion a row that moves crossfades at its new place instead of travelling. Its 性能 cell (DESIGN 12,
/// <i>Motion</i>) replaces its characters in place, up or down, crossfades between <c>–</c> and a number (0.15 s; Reduce
/// Motion: every change), crossfades its color between live and resting (0.2 s) and fades its provisional mark in and out
/// (0.25 s; Reduce Motion: at once).
/// </summary>
public sealed class BoardRowMotion
{
    public const double FadeSeconds = 0.3;
    public const double EnterOffset = 10;
    public const double FlashSeconds = 0.8;
    public const double DimSeconds = 0.5;
    public const double FlashThreshold = 0.05;

    /// <summary>The share underline's width transition (CSS <c>width .55s cubic-bezier(.2,.8,.2,1)</c>).</summary>
    public const double ShareSeconds = 0.55;

    private DampedSpring _y;
    private double _target;
    private double _enterStart = double.NegativeInfinity;
    private double _leaveStart = double.NaN;
    private double _flashStart = double.NegativeInfinity;
    private double _last = double.NaN;
    private double _shareFrom;
    private double _shareTo;
    private double _shareStart = double.NegativeInfinity;
    private bool? _intelLive;
    private double _intelChange = double.NegativeInfinity;
    private bool _intelKnown;
    private int? _intelShown;
    private string _intelOld = string.Empty;
    private bool _intelOldProof;
    private double _intelFadeStart = double.NegativeInfinity;
    private double _intelFadeSeconds;
    private bool _intelProof;
    private double _proofChange = double.NegativeInfinity;

    public BoardRowMotion(string key, BoardStyle style, BoardEntry entry, double now, bool appear)
    {
        Key = key;
        ValueKey = key + "#v";
        Entry = entry;
        _target = entry.Y;
        _y = BoardRowSprings.For(style, entry.Y);
        if (appear)
        {
            _enterStart = now;
        }
    }

    public string Key { get; }

    /// <summary>The layer key of the static parts by the row's value (timing's fastest dot, neon's lead tint).</summary>
    public string ValueKey { get; }

    public BoardEntry Entry { get; private set; }

    public double Live { get; private set; }

    /// <summary>Timing's interval to the group above (<see cref="BoardValueGlyphs.Interval"/>), empty for the leader and other rows.</summary>
    public string IntervalText { get; set; } = string.Empty;

    /// <summary>The value as the row shows it (<see cref="BoardValueGlyphs.Text"/>), made once per tick.</summary>
    public string ValueText { get; private set; } = BoardValueGlyphs.Dash.ToString();

    /// <summary>The row's share of the board's value (the underline), eased toward the latest tick's.</summary>
    public double Share(double now)
    {
        var t = (now - _shareStart) / ShareSeconds;
        return t is >= 0 and < 1 ? _shareFrom + (_shareTo - _shareFrom) * Easing.OutCubic(t) : _shareTo;
    }

    /// <summary>Sets the share of the latest tick; the underline eases to it unless motion is reduced.</summary>
    public void SetShare(double share, double now, bool animate)
    {
        share = double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0;
        if (Math.Abs(share - _shareTo) < 1e-6)
        {
            return;
        }

        _shareFrom = animate ? Share(now) : share;
        _shareTo = share;
        _shareStart = animate ? now : double.NegativeInfinity;
    }

    /// <summary>The underline is still easing.</summary>
    public bool Sharing(double now) => now - _shareStart < ShareSeconds;

    /// <summary>The 性能 cell's color crossfades in 0.2 s between live and resting (an opacity change, kept with Reduce Motion).</summary>
    public const double IntelFadeSeconds = 0.2;

    /// <summary>How live the 性能 cell's color is: 1 live, 0 resting or none, between them while it crossfades.</summary>
    public double IntelLive(double now)
    {
        var live = _intelLive == true ? 1.0 : 0;
        var t = (now - _intelChange) / IntelFadeSeconds;
        return t is >= 0 and < 1 ? (1 - live) + (2 * live - 1) * Easing.OutQuad(t) : live;
    }

    /// <summary>The 性能 cell's text on display (the score, or <c>–</c>).</summary>
    public string IntelText { get; private set; } = PerfNumber.Dash;

    /// <summary>The 性能 cell's old text while it crossfades out, else null.</summary>
    public string? IntelOutgoing(double now) => now - _intelFadeStart < _intelFadeSeconds ? _intelOld : null;

    /// <summary>The outgoing text carried the provisional mark (its underline fades out with it).</summary>
    public bool IntelOutgoingProvisional => _intelOldProof;

    /// <summary>The 性能 cell shows a score (not <c>–</c>).</summary>
    public bool IntelHasScore => _intelShown is not null;

    /// <summary>The 性能 cell's crossfade: the outgoing and the incoming text's opacities (0 and 1 outside one).</summary>
    public (double Outgoing, double Incoming) IntelFade(double now)
    {
        var t = (now - _intelFadeStart) / _intelFadeSeconds;
        if (t is >= 0 and < 1)
        {
            var e = Easing.OutQuad(t);
            return (1 - e, e);
        }

        return (0, 1);
    }

    /// <summary>How present the 性能 cell's provisional mark is: 1 provisional, 0 settled or none, between them while it fades.</summary>
    public double IntelProof(double now)
    {
        var target = _intelProof ? 1.0 : 0;
        var t = (now - _proofChange) / PerfProvisionalMark.FadeSeconds;
        return t is >= 0 and < 1 ? (1 - target) + (2 * target - 1) * Easing.OutQuad(t) : target;
    }

    /// <summary>The flash: +1 warm, −1 dim, 0 none.</summary>
    public int FlashDirection { get; private set; }

    public bool Leaving => !double.IsNaN(_leaveStart);

    public double Y => _y.Value;

    public double Target => _target;

    public void Restyle(BoardStyle style) => _y = BoardRowSprings.For(style, _y.Value);

    /// <summary>A new tick's entry for this row (a new place, and a flash when its value changed by more than 5 %).</summary>
    public void Update(BoardEntry entry, double live, double now, bool reduceMotion)
    {
        Entry = entry;
        _leaveStart = double.NaN;
        if (Math.Abs(entry.Y - _target) > 0.01)
        {
            _target = entry.Y;
            if (reduceMotion)
            {
                _y.Snap(_target);
                _enterStart = now;
            }
        }

        if (double.IsFinite(_last) && _last > 0)
        {
            var change = (live - _last) / _last;
            if (Math.Abs(change) > FlashThreshold)
            {
                FlashDirection = change > 0 ? 1 : -1;
                _flashStart = now;
            }
        }

        if (entry.Readout is { } readout)
        {
            if (_intelLive is { } was && was != readout.Live)
            {
                _intelChange = now;
            }

            _intelLive = readout.Live;
            var shown = readout.Score.Shown;
            if (_intelKnown && shown != _intelShown)
            {
                var change = PerfMotion.Plan(_intelShown, shown, PerfSurface.BoardRow, reduceMotion);
                if (change.Kind == PerfChangeKind.Crossfade)
                {
                    _intelOldProof = _intelShown is not null && IntelProof(now) >= 0.5;
                    _intelOld = IntelText;
                    _intelFadeStart = now;
                    _intelFadeSeconds = change.Duration;
                }
                else
                {
                    _intelFadeStart = double.NegativeInfinity;
                }
            }

            if (_intelKnown && readout.IsProvisional != _intelProof)
            {
                _proofChange = reduceMotion ? double.NegativeInfinity : now;
            }

            _intelKnown = true;
            _intelShown = shown;
            _intelProof = readout.IsProvisional;
            IntelText = readout.Intelligence;
        }

        _last = live;
        if (live != Live || string.IsNullOrEmpty(ValueText))
        {
            ValueText = BoardValueGlyphs.Text(live);
        }

        Live = live;
    }

    public void Leave(double now)
    {
        if (!Leaving)
        {
            _leaveStart = now;
        }
    }

    public void Step(double dt) => _y.Step(_target, dt);

    /// <summary>Opacity from entering and leaving.</summary>
    public double Alpha(double now)
    {
        var enter = Math.Clamp((now - _enterStart) / FadeSeconds, 0, 1);
        var leave = Leaving ? 1 - Math.Clamp((now - _leaveStart) / FadeSeconds, 0, 1) : 1;
        return Easing.OutCubic(enter) * leave;
    }

    /// <summary>The extra offset of an entering row (it rises 10 pt while fading in).</summary>
    public double EnterLift(double now, bool reduceMotion) =>
        reduceMotion ? 0 : EnterOffset * (1 - Easing.OutCubic(Math.Clamp((now - _enterStart) / FadeSeconds, 0, 1)));

    /// <summary>How far the value's flash is, 0 at its start and 1 when it ended.</summary>
    public double FlashProgress(double now) =>
        Math.Clamp((now - _flashStart) / (FlashDirection < 0 ? DimSeconds : FlashSeconds), 0, 1);

    public bool Gone(double now) => Leaving && now - _leaveStart >= FadeSeconds;

    /// <summary>Whether the row still needs frames (moving, fading or flashing).</summary>
    public bool Animating(double now) => Moving(now) || Flashing(now);

    /// <summary>Moving to its place, entering or leaving (DESIGN 2c Frame policy: a row move needs 60 fps).</summary>
    public bool Moving(double now) => !_y.IsSettled(_target, 0.05) || now - _enterStart < FadeSeconds || Leaving;

    /// <summary>Its value flashes warm or dims, its underline eases or its 性能 cell fades (no travel: 30 fps is enough).</summary>
    public bool Flashing(double now) => FlashProgress(now) < 1 || Sharing(now) || now - _intelChange < IntelFadeSeconds ||
        now - _intelFadeStart < _intelFadeSeconds || now - _proofChange < PerfProvisionalMark.FadeSeconds;

    public void Settle()
    {
        _y.Snap(_target);
        _enterStart = double.NegativeInfinity;
        _flashStart = double.NegativeInfinity;
        _shareStart = double.NegativeInfinity;
        _intelChange = double.NegativeInfinity;
        _intelFadeStart = double.NegativeInfinity;
        _proofChange = double.NegativeInfinity;
    }
}
