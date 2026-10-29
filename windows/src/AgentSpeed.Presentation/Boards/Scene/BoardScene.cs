using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Boards.Gpu;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;

namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>
/// One board's picture, platform-neutral (DESIGN 2c, Two layers): each engine tick (<see cref="Apply"/>) sets the rows'
/// places and fires the effects of its events; each frame (<see cref="Render"/>) composites the cached layers — rows, the
/// hero and the footer, rasterized by the platform only when their content changes — at their animated places between the
/// effects behind and above them. The draw list is in canvas points (<see cref="BoardCanvas"/>); nothing allocates per frame.
/// </summary>
/// <remarks>
/// Layer keys: a row's body is its plan key and its value <c>key#v</c>; the hero (everything but its number) is
/// <see cref="HeroKey"/> with its <see cref="UnitKey"/> and <see cref="HistoryKey"/>, the number's glyphs <see cref="BoardHeroNumber.GlyphKeys"/>, the footer <see cref="FooterKey"/>, the
/// static pattern behind the content <see cref="BackdropKey"/>, and each live callout its <see cref="BoardCallout.Key"/>.
/// </remarks>
public sealed class BoardScene
{
    public const string HeroKey = "#hero";

    /// <summary>The unit after the number (placed by the number's width, so the hero does not depend on the value).</summary>
    public const string UnitKey = "#unit";

    /// <summary>The sparkline, the bars or the LED bar.</summary>
    public const string HistoryKey = "#history";
    public const string FooterKey = "#footer";
    public const string BackdropKey = "#backdrop";

    /// <summary>The captions line at the top of the ranking (性能 over the 性能 cells, tok/s over the values, timing's 間隔).</summary>
    public const string CaptionsKey = "#caps";

    /// <summary>A switch change crossfades the hero (DESIGN 2c, Live metrics: Switches).</summary>
    public const double SwitchSeconds = 0.2;

    /// <summary>A two-line row's first baseline under its top.</summary>
    public const double FirstBaseline = 16;

    /// <summary>A two-line row's second baseline under its top (glass 32, timing and neon 31).</summary>
    public static double SecondBaseline(BoardStyle style) => style == BoardStyle.Glass ? 32 : 31;

    /// <summary>A rise of more than 8 % + 20 tok/s in one tick punches the number (DESIGN 2c Hero number).</summary>
    public const double PunchRatio = 0.08;
    public const double PunchFloor = 20;
    public const double PunchSeconds = 0.4;

    private readonly Dictionary<string, BoardRowMotion> _rows = new(StringComparer.Ordinal);
    private readonly List<BoardRowMotion> _order = [];
    private readonly BoardHeroNumber _number = new();
    private readonly BoardCallouts _callouts = new();
    private readonly BoardGauge _gauge = new();
    private BoardTick? _tick;
    private BoardPalette? _palette;
    private BoardGaugePalette? _gaugePalette;
    private double _switchStart = double.NegativeInfinity;
    private double _lastFrame = double.NaN;
    private double _ambience;
    private double _punchStart = double.NegativeInfinity;
    private bool _dark = true;
    private BoardRowFigures _figures = BoardRowFigures.Nominal;

    public BoardScene(BoardStyle style, BoardSize size, uint seed = 1)
    {
        Effects = new BoardEffects(style, size, seed);
        Configure(style, size, Effects.Policy);
    }

    public BoardEffects Effects { get; }

    public BoardStyle Style { get; private set; }

    public BoardSize Size { get; private set; }

    public BoardMetrics Metrics { get; private set; } = BoardMetrics.For(BoardStyle.Glass, BoardSize.M);

    public BoardCanvas Canvas => Effects.Canvas;

    public BoardTick? Tick => _tick;

    /// <summary>Where the style's hero puts its parts.</summary>
    public BoardHeroLayout Hero { get; private set; } = BoardHeroLayout.For(BoardStyle.Glass, BoardSize.M);

    /// <summary>The appearance (DESIGN 2c: light and dark follow Appearance); the platform redraws the layers after a change.</summary>
    public bool Dark
    {
        get => _dark;
        set
        {
            _dark = value;
            _palette = null;
            _gaugePalette = null;
            Effects.Dark = value;
        }
    }

    /// <summary>The style's colors in the current appearance and contrast.</summary>
    public BoardPalette Palette => _palette ??= BoardPalette.For(Style, _dark, Effects.Policy.HighContrast);

    /// <summary>The sub-gauge's and the rows' live metrics colors.</summary>
    public BoardGaugePalette GaugePalette => _gaugePalette ??= BoardGaugePalette.For(Style, Palette);

    /// <summary>The live metrics switches the board is laid out for (from the latest tick).</summary>
    public BoardReadouts Readouts { get; private set; } = BoardReadouts.None;

    /// <summary>The hero's sub-gauge (layout B).</summary>
    public BoardGauge Gauge => _gauge;

    /// <summary>The rows' right-hand columns.</summary>
    public BoardRowColumns Columns { get; private set; } = BoardRowColumns.For(BoardStyle.Glass, BoardSize.M, BoardReadouts.None);

    /// <summary>
    /// The two-line group rows' figures as the platform measured them in the style's fonts (DESIGN 2c, The second line): their
    /// widest widths, which the columns widen to fit, and the cap heights that centre them on the row. Set before the layers are
    /// brought up to date; <see cref="BoardRowFigures.Nominal"/> until then.
    /// </summary>
    public BoardRowFigures Figures
    {
        get => _figures;
        set
        {
            if (value != _figures)
            {
                _figures = value;
                Columns = BoardRowColumns.For(Style, Size, Readouts, value);
            }
        }
    }

    /// <summary>The number on display (its glyphs are composed every frame).</summary>
    public string NumberText => _number.Text;

    /// <summary>The callouts alive (chips, the timing block and banner), whose layers the platform rasterizes.</summary>
    public IReadOnlyList<BoardCallout> Callouts => _callouts.Items;

    /// <summary>The row whose tint hover lifts (DESIGN 2c: hover lifts the row tint only).</summary>
    public string? Hovered { get; set; }

    /// <summary>The hero number's box in board points (embers leave it); a heuristic until the style's hero is laid out.</summary>
    public (double Left, double Top, double Width, double Height) HeroNumber { get; set; }

    /// <summary>The rows in drawing order (the plan's order, then leaving rows).</summary>
    public IReadOnlyList<BoardRowMotion> Rows => _order;

    public void Configure(BoardStyle style, BoardSize size, BoardEffectsPolicy policy)
    {
        var restyle = style != Style;
        Style = style;
        Size = size;
        Layout();
        Effects.Configure(style, size);
        Effects.Policy = policy;
        _palette = null;
        _gaugePalette = null;
        HeroNumber = (Metrics.Pad, Metrics.HeroTop + Hero.NumberTop, Metrics.ValueSize * 2.8, Hero.NumberHeight);
        if (restyle)
        {
            foreach (var row in _order)
            {
                row.Restyle(style);
            }
        }
    }

    private void Layout()
    {
        Metrics = BoardMetrics.For(Style, Size, Readouts);
        Hero = BoardHeroLayout.For(Style, Size, Readouts);
        Columns = BoardRowColumns.For(Style, Size, Readouts, _figures);
    }

    /// <summary>Applies an engine tick: rows move to their plan places, enter or leave, values flash, events fire effects.</summary>
    public void Apply(BoardTick tick, double now)
    {
        ArgumentNullException.ThrowIfNull(tick);
        var previous = _tick;
        _tick = tick;
        var reduce = Effects.Policy.ReduceMotion;
        if (tick.Readouts != Readouts)
        {
            // A switch change: the hero crossfades in 0.2 s, rows spring to their new places (Reduce Motion: the crossfade only).
            Readouts = tick.Readouts;
            Layout();
            HeroNumber = (Metrics.Pad, Metrics.HeroTop + Hero.NumberTop, Metrics.ValueSize * 2.8, Hero.NumberHeight);
            _switchStart = previous is null ? double.NegativeInfinity : now;
        }

        _gauge.Apply(tick, now, previous is not null, reduce);
        // Without frames (a hidden board) rows that left are dropped here, so nothing accumulates.
        Prune(now);
        foreach (var row in _order)
        {
            row.Leave(now);
        }

        var first = previous is null;
        var placed = new List<BoardRowMotion>(tick.Entries.Count);
        var above = double.NaN;
        foreach (var entry in tick.Entries)
        {
            if (!_rows.TryGetValue(entry.Key, out var row))
            {
                row = new BoardRowMotion(entry.Key, Style, entry, now, appear: !first);
                _rows[entry.Key] = row;
            }

            var live = tick.LiveOf(entry);
            row.Update(entry, live, now, reduce);
            if (entry.Kind == BoardEntryKind.Group)
            {
                // Groups come in rank order: the interval is to the group placed just above.
                row.IntervalText = Style == BoardStyle.Timing && entry.Rank > 1 && double.IsFinite(above)
                    ? BoardValueGlyphs.Interval(above, entry.Group.Live) : string.Empty;
                above = entry.Group.Live;
            }

            row.SetShare(entry.Kind is BoardEntryKind.Group or BoardEntryKind.Sub && tick.Value > 0 ? live / tick.Value : 0, now, !first && !reduce);
            placed.Add(row);
        }

        foreach (var row in _order)
        {
            if (row.Leaving)
            {
                placed.Add(row);
            }
        }

        _order.Clear();
        _order.AddRange(placed);
        if (first)
        {
            foreach (var row in _order)
            {
                row.Settle();
            }
        }

        _number.Set(tick.ValueText, previous is null || tick.Value >= previous.Value, now, !first && !reduce);
        _callouts.Prune(now);

        Effects.SetHeat(tick.Heat, tick.Overdrive, tick.Tier, tick.Generating);
        if (previous is not null)
        {
            Fire(previous, tick, now);
        }
    }

    /// <summary>
    /// The frame at <paramref name="now"/>: the square and its pattern, the ambience and effects behind, the rows with their
    /// underlines and callouts, the hero and its number, the footer, then the rail or tube and the effects above.
    /// </summary>
    public void Render(double now, IBoardLayers layers, BoardDrawList list)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(list);
        var dt = double.IsNaN(_lastFrame) ? 0 : Math.Clamp(now - _lastFrame, 0, BoardEffects.MaxStep);
        _lastFrame = now;
        Effects.Advance(now);

        // The ambience drifts only while the board animates: an idle board is static (2c Frame policy), so a tick that changes
        // nothing draws the same frame.
        if (Effects.Demand != FrameClass.None)
        {
            _ambience += dt;
        }
        var canvas = Canvas;
        var m = BoardCanvas.Margin;
        var metrics = Metrics;
        var policy = Effects.Policy;
        var reduce = policy.ReduceMotion;
        var palette = Palette;
        var heat = _tick?.Heat ?? 0;
        list.Canvas = canvas.Extent;
        list.BoxHalf = canvas.Half;
        list.BoxRadius = canvas.Radius;
        list.OutsideReach = BoardCanvas.BloomReach;

        BoardBackdrop.Emit(list, canvas, Style, palette, metrics);
        if (layers.TryGet(BackdropKey, out var backdrop))
        {
            list.Clip = BoardClip.Inside;
            list.AddLayer(BoardLayerInstance.Create(backdrop, m, m, 1));
            list.Clip = BoardClip.None;
        }

        BoardAmbience.EmitBack(list, canvas, Style, palette, heat, _ambience, policy);
        Effects.EmitBack(list);

        var clip = RectBounds.From(m + metrics.ListLeft, m + metrics.ListTop, metrics.ListWidth, metrics.ListHeight);
        list.Clip = BoardClip.Inside;
        Prune(now);
        _callouts.Prune(now);
        var animating = Effects.Demand != FrameClass.None;
        var comets = policy.Comets && !palette.HighContrast && animating;
        var pulses = policy.Comets && animating;

        // Two passes, so a frame draws every row's shapes (hover tint, share underline) in one batch and every row's layers in
        // the next instead of alternating per row; the underlines lie under every row's content, as the CSS bar's z-index -1.
        foreach (var row in _order)
        {
            row.Step(dt);
            var alpha = row.Alpha(now);
            if (row.Entry.Kind == BoardEntryKind.Group && !row.Leaving)
            {
                Effects.FollowRow(row.Key, metrics.ListTop + row.Y + RowHeight(row.Entry) / 2);
            }

            if (alpha <= 0.002)
            {
                continue;
            }

            var top = m + metrics.ListTop + row.Y + row.EnterLift(now, reduce);
            var left = m + metrics.ListLeft;
            var height = RowHeight(row.Entry);
            if (string.Equals(row.Key, Hovered, StringComparison.Ordinal) && row.Entry.Kind == BoardEntryKind.Group)
            {
                list.Add(OrbGpuInstance.RoundedRect(left + metrics.ListWidth / 2, top + height / 2, metrics.ListWidth / 2, height / 2, 0,
                    Style == BoardStyle.Timing ? 4 : 9.5, palette.Fill.MultiplyAlpha(alpha)));
            }

            if (Style != BoardStyle.Timing)
            {
                Underline(list, row, left, top, height, alpha, now, comets);
            }
        }

        foreach (var row in _order)
        {
            var alpha = row.Alpha(now);
            if (alpha <= 0.002)
            {
                continue;
            }

            var top = m + metrics.ListTop + row.Y + row.EnterLift(now, reduce);
            var left = m + metrics.ListLeft;
            var height = RowHeight(row.Entry);
            var twoLine = row.Entry.TwoLine;
            if (layers.TryGet(row.Key, out var body))
            {
                list.AddLayer(BoardLayerInstance.Create(body, left, top, alpha, clip: clip));
                if (pulses)
                {
                    // A two-line row's capsule sits in the first 16 pt band, 4 under its top.
                    BoardPulses.EmitShimmer(list, body, Style, left, top, twoLine ? 24 : height, alpha, now, BoardPulses.PhaseOf(row.Key), clip);
                }
            }

            if (BoardPulses.BadgeKey(row.Entry) is { } badgeKey && layers.TryGet(badgeKey, out var badge) &&
                BoardPulses.Badge(row.Entry) is { } shown)
            {
                // Two-line rows: the badge on the first line (centred in its band, 1 pt up); timing's stripe spans both lines.
                var badgeHeight = twoLine && Style != BoardStyle.Timing ? 22 : height;
                BoardPulses.EmitBadge(list, badge, Style, row.Entry.Kind == BoardEntryKind.Sub, shown.Phase, left, top, badgeHeight, alpha, now,
                    BoardPulses.PhaseOf(row.Key), pulses, clip);
            }

            if (layers.TryGet(row.ValueKey, out var value))
            {
                list.AddLayer(BoardLayerInstance.Create(value, left + metrics.ListWidth - value.Width, top, alpha, clip: clip));
            }

            if (row.Entry.Kind is BoardEntryKind.Group or BoardEntryKind.Sub)
            {
                var baseline = Value(list, layers, row, left + metrics.ListWidth, top, height, alpha, now, clip);
                Readout(list, layers, row, left + metrics.ListWidth, baseline, alpha, now, clip);
            }

            if (row.IntervalText.Length > 0 && Size != BoardSize.S)
            {
                var intervalRight = left + metrics.ListWidth - (metrics.IntelligenceColumn ? Columns.IntervalRight : BoardValueGlyphs.IntervalRight);
                if (twoLine)
                {
                    // A figure too, centred on the row at its own size on its own baseline.
                    BoardValueGlyphs.EmitOnBaseline(list, layers, row.IntervalText, BoardValueSet.Interval, intervalRight, 1,
                        top + _figures.IntervalBaseline(height), 0, palette.Ink3, 1, alpha, clip);
                }
                else
                {
                    BoardValueGlyphs.Emit(list, layers, row.IntervalText, BoardValueSet.Interval, intervalRight, top + (height - 1) / 2, 0, palette.Ink3,
                        alpha, clip);
                }
            }
        }

        if (metrics.IntelligenceColumn && layers.TryGet(CaptionsKey, out var captions))
        {
            list.AddLayer(BoardLayerInstance.Create(captions, m + metrics.ListLeft, m + metrics.ListTop, 1, clip: clip));
        }

        EmitRowCallouts(now, layers, list, clip);
        list.Clip = BoardClip.None;
        var switchAlpha = SwitchAlpha(now);
        list.AlphaScale = (float)switchAlpha;
        EmitHero(now, layers, list);
        list.AlphaScale = 1;
        if (layers.TryGet(FooterKey, out var footer))
        {
            list.AddLayer(BoardLayerInstance.Create(footer, m + metrics.Pad, m + metrics.FooterTop, 1));
        }

        BoardAmbience.EmitFront(list, canvas, Style, palette, metrics, Hero, heat, _tick?.Generating ?? false, _ambience, policy);
        Effects.EmitFront(list);
    }

    /// <summary>
    /// The frames the board needs (DESIGN 2c, Frame policy): the effects' class, raised to 60 fps while a row moves or fades,
    /// digits roll, the number punches or a callout pops, and to 30 fps while a value flashes, a digit glows or an underline
    /// eases.
    /// </summary>
    public FrameClass Demand(double now)
    {
        var demand = Effects.Demand;
        if (demand == FrameClass.Fps60)
        {
            return demand;
        }

        var look = BoardNumberLook.For(Style);
        if (now - _punchStart < PunchSeconds || _number.Rolling(now, look) || _callouts.Alive(now) || now - _switchStart < SwitchSeconds ||
            (Readouts.Hero && _gauge.Animating(now)))
        {
            return FrameClass.Fps60;
        }

        var flashing = _number.Animating(now, look);
        foreach (var row in _order)
        {
            if (row.Moving(now))
            {
                return FrameClass.Fps60;
            }

            flashing |= row.Flashing(now);
        }

        return flashing ? FramePolicy.Max(demand, FrameClass.Fps30) : demand;
    }

    /// <summary>
    /// A row's own height without the gap below it (glass and neon space group rows 2 pt apart; two-line rows leave the gap out).
    /// </summary>
    public double RowHeight(BoardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind == BoardEntryKind.Group && Style != BoardStyle.Timing && !entry.TwoLine ? entry.Height - 2 : entry.Height;
    }

    /// <summary>A two-line group row's figures' shared baseline under its top (the 性能 cell joins the value while the column shows).</summary>
    public double FigureBaseline(BoardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _figures.Baseline(RowHeight(entry), Metrics.IntelligenceColumn);
    }

    /// <summary>The hero's opacity while a switch change crossfades it.</summary>
    public double SwitchAlpha(double now)
    {
        var t = (now - _switchStart) / SwitchSeconds;
        return t is >= 0 and < 1 ? Easing.OutQuad(t) : 1;
    }

    /// <summary>
    /// Whether a canvas point lies on the hero's 性能 (glass: the dial; neon: the digits and meter; timing: the 性能 row), where
    /// hovering opens the score card (DESIGN 2c, Tooltip and score card).
    /// </summary>
    public bool HitScore(double x, double y) =>
        Readouts.Hero && Readouts.Intelligence && _gauge.ScoreArea is { } area && x >= area.Left && x < area.Right && y >= area.Top &&
        y < area.Bottom;

    /// <summary>
    /// The row under a canvas point, for hover, tooltips and clicks; <paramref name="atRest"/> tests the places rows are heading
    /// for (a hover refreshed on a tick follows the row the ranking moves under a resting cursor before it arrives).
    /// </summary>
    public BoardRowMotion? HitRow(double x, double y, bool atRest = false)
    {
        var metrics = Metrics;
        var m = BoardCanvas.Margin;
        if (x < m + metrics.ListLeft || x > m + metrics.ListLeft + metrics.ListWidth || y < m + metrics.ListTop ||
            y > m + metrics.ListTop + metrics.ListHeight)
        {
            return null;
        }

        foreach (var row in _order)
        {
            var top = m + metrics.ListTop + (atRest ? row.Target : row.Y);
            if (!row.Leaving && y >= top && y < top + row.Entry.Height)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>Ends every motion at once (the window appears, Reduce Motion turns on).</summary>
    public void Settle()
    {
        foreach (var row in _order)
        {
            row.Settle();
        }

        _number.Settle();
        _gauge.Settle();
        _switchStart = double.NegativeInfinity;
        _callouts.Clear();
        _punchStart = double.NegativeInfinity;
        _lastFrame = double.NaN;
        Effects.Clear();
        Effects.Resume();
    }

    private void Prune(double now)
    {
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            if (_order[i].Gone(now))
            {
                _rows.Remove(_order[i].Key);
                _order.RemoveAt(i);
            }
        }
    }

    private void EmitHero(double now, IBoardLayers layers, BoardDrawList list)
    {
        var m = BoardCanvas.Margin;
        var x = m + Metrics.Pad;
        var y = m + Metrics.HeroTop;
        if (layers.TryGet(HeroKey, out var hero))
        {
            list.AddLayer(BoardLayerInstance.Create(hero, x, y, 1));
        }

        if (layers.TryGet(HistoryKey, out var history))
        {
            var pad = BoardHeroLayout.HistoryPad;
            list.AddLayer(BoardLayerInstance.Create(history, x + Hero.History.Left - pad, y + Hero.History.Top - pad, 1));
        }

        var top = y + Hero.NumberTop;
        // High contrast keeps the number solid: no chromatic fringes.
        var look = BoardNumberLook.For(Style);
        if (Palette.HighContrast)
        {
            look = look with { Fringes = false };
        }

        var width = _number.Emit(list, layers, x, top, Hero.NumberHeight, Hero.Tracking, now, PunchScale(now), look, _tick?.Heat ?? 0,
            Effects.Policy.ReduceMotion);
        if (width > 0)
        {
            HeroNumber = (Metrics.Pad, Metrics.HeroTop + Hero.NumberTop, width, Hero.NumberHeight);
            if (layers.TryGet(UnitKey, out var unit))
            {
                list.AddLayer(BoardLayerInstance.Create(unit, x + width + Hero.UnitGap - BoardHeroLayout.UnitPad, y, 1));
            }
        }

        if (Readouts.Hero)
        {
            _gauge.Emit(list, layers, Style, Size, Readouts, Hero, GaugePalette, x, y, 1, now);
        }

        for (var i = 0; i < _callouts.Count; i++)
        {
            var callout = _callouts.Items[i];
            if (!layers.TryGet(callout.Key, out var slot))
            {
                continue;
            }

            var t = callout.Progress(now);
            if (callout.Kind == BoardCalloutKind.NewPeak)
            {
                var (alpha, dx, dy, scale) = BoardCallouts.Chip(Style, callout.Kind, t);
                var pad = BoardCallouts.ChipPad;
                BoardCallouts.Emit(list, slot, x + dx - pad, top - (Style == BoardStyle.Neon ? 13 : 14) + dy - pad, alpha, scale);
            }
            else if (callout.Kind == BoardCalloutKind.NewLeader)
            {
                var (from, to) = BoardCallouts.Wipe(t);
                var bannerTop = m + Metrics.Pad + Metrics.HeroHeight - 14;
                list.AddLayer(BoardLayerInstance.Create(slot, m, bannerTop, 1, clip: RectBounds.From(m + slot.Width * from, bannerTop,
                    slot.Width * (to - from), slot.Height)));
            }
        }
    }

    private void EmitRowCallouts(double now, IBoardLayers layers, BoardDrawList list, RectBounds clip)
    {
        var m = BoardCanvas.Margin;
        var metrics = Metrics;
        for (var i = 0; i < _callouts.Count; i++)
        {
            var callout = _callouts.Items[i];
            if (callout.Kind is not (BoardCalloutKind.Climb or BoardCalloutKind.Block) || !layers.TryGet(callout.Key, out var slot) ||
                RowOf(callout.Row) is not { } row)
            {
                continue;
            }

            var mid = m + metrics.ListTop + row.Y + RowHeight(row.Entry) / 2;
            var t = callout.Progress(now);
            if (callout.Kind == BoardCalloutKind.Block)
            {
                list.AddLayer(BoardLayerInstance.Create(slot, m + metrics.ListLeft + 4, mid - slot.Height / 2, 1 - Easing.OutCubic(t), clip: clip));
                continue;
            }

            var (alpha, dx, _, scale) = BoardCallouts.Chip(Style, callout.Kind, t);
            var pad = BoardCallouts.ChipPad;
            var right = m + metrics.ListLeft + metrics.ListWidth - (Style == BoardStyle.Timing ? 92 : 52);
            BoardCallouts.Emit(list, slot, right - slot.Width + pad + dx, mid - slot.Height / 2, alpha * row.Alpha(now), scale, clip);
        }
    }

    private BoardRowMotion? RowOf(string? key)
    {
        foreach (var row in _order)
        {
            if (string.Equals(row.Key, key, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    // The share underline (glass and neon): from the name to the value, as long as the row's share of the board's value, in
    // the row's heat; a white comet runs along it while the row generates.
    private void Underline(BoardDrawList list, BoardRowMotion row, double left, double top, double height, double alpha, double now, bool comets)
    {
        var entry = row.Entry;
        if (entry.Kind is not (BoardEntryKind.Group or BoardEntryKind.Sub))
        {
            return;
        }

        var share = row.Share(now);
        var group = entry.Kind == BoardEntryKind.Group;
        var x0 = left + (group ? 40 : 46);
        var track = left + Metrics.ListWidth - 6 - x0;
        if (share * track < 0.5 || track <= 0)
        {
            return;
        }

        var thickness = group ? 2 : 1.5;
        var y = top + height - 1 - thickness / 2;
        var heat = HeatMath.Heat(row.Live, _tick?.Redline ?? HeatMath.DefaultRedline);
        var color = HeatPalette.Sample(Math.Max(0.12, heat));
        var opacity = alpha * (group ? 1 : 0.7);
        var glows = !Palette.HighContrast;
        var neon = Style == BoardStyle.Neon;
        var x1 = x0 + track * share;
        // The CSS shadows (neon 8 px, glass 6 px at 70 %) of a 2 pt bar peak at about a fifth of its color: erf(1 / (σ√2)).
        list.Add(OrbGpuInstance.Capsule(x0 + thickness / 2, y, Math.Max(x0 + thickness / 2, x1 - thickness / 2), y, thickness,
            color.WithAlpha(opacity), color.WithAlpha((neon ? 0 : 0.35) * opacity), 1, glows ? OrbGpuInstance.Glow(neon ? 4 : 3) : 0,
            neon ? 0.3 : 0.18));
        var generating = group ? entry.Group.Generating > 0 : entry.Agent?.Phase == AgentPhase.Generating;
        if (!comets || !generating)
        {
            return;
        }

        // CSS comet: a white radial dash (glass 18 × 4 pt, neon 22 × 6) from its own width before the bar to the bar's end,
        // over 3.2 s − 2.4 s × heat, fading in and out.
        var seconds = 3.2 - 2.4 * heat;
        var phase = ((now + BoardPulses.Seed(row.Key) * seconds) % seconds) / seconds;
        var half = neon ? 11 : 9;
        var head = x0 - 2 * half + (track * share - (neon ? 8 : 6) + 2 * half) * Easing.InOutSine(phase);
        var fade = phase < 0.15 ? phase / 0.15 : phase > 0.85 ? (1 - phase) / 0.15 : 1;
        list.Add(OrbGpuInstance.Ellipse(head + half, y, half * 0.55, neon ? 1.2 : 0.7, 0, Rgba.White, 0, OrbGpuInstance.Glow(1),
            (neon ? 1 : 0.9) * fade * alpha));
    }

    // A row's value from the value glyphs, in its color, its warm flash or its dim; returns its baseline (a two-line row's
    // figures share one baseline centred on the row, 2 pt larger).
    private double Value(BoardDrawList list, IBoardLayers layers, BoardRowMotion row, double right, double top, double height, double alpha, double now,
        RectBounds clip)
    {
        var sub = row.Entry.Kind == BoardEntryKind.Sub;
        var set = sub ? BoardValueSet.Sub : BoardValueSet.Group;
        var color = BoardValueGlyphs.Color(Style, Palette, sub, row.Live, _tick?.Redline ?? HeatMath.DefaultRedline);
        var (tint, amount, dim) = Flash(row, now);
        if (amount > 0)
        {
            color = Rgba.Lerp(color, tint.WithAlpha(1), amount);
        }

        if (row.Entry.TwoLine)
        {
            var baseline = top + FigureBaseline(row.Entry);
            BoardValueGlyphs.EmitOnBaseline(list, layers, row.ValueText, set, right - BoardValueGlyphs.Inset(Style), 1, baseline,
                BoardValueGlyphs.Tracking(Style, sub, twoLine: true), color, 1, alpha * dim, clip);
            return baseline;
        }

        var center = top + (Style == BoardStyle.Timing && !sub ? (height - 1) / 2 : height / 2);
        BoardValueGlyphs.Emit(list, layers, row.ValueText, set, right - BoardValueGlyphs.Inset(Style), center,
            BoardValueGlyphs.Tracking(Style, sub), color, alpha * dim, clip);
        return BoardValueGlyphs.TryGet('0', set, layers, out var zero) ? center - zero.Height / 2 + BoardValueGlyphs.Baseline(zero) : center + 4;
    }

    // A row's 性能 cell on the value's baseline: its digits replaced in place (a fall crossfades), its color crossfading between
    // live and resting, and the dotted underline of an provisional score with the digits, as part of the row's content.
    private void Readout(BoardDrawList list, IBoardLayers layers, BoardRowMotion row, double right, double baseline, double alpha, double now, RectBounds clip)
    {
        if (row.Entry.Readout is null || !Metrics.IntelligenceColumn)
        {
            return;
        }

        var sub = row.Entry.Kind == BoardEntryKind.Sub;
        var x = right - Columns.IntelligenceRight;
        var live = row.IntelLive(now);
        var size = BoardRowColumns.IntelligenceSize(Style, sub, row.Entry.TwoLine);
        var tracking = Style == BoardStyle.Glass ? 0 : -0.02 * size;
        var (outgoing, incoming) = row.IntelFade(now);
        var gauge = GaugePalette;
        var ink = Palette.HighContrast ? Palette.Ink : Rgba.Lerp(gauge.RowQuiet, gauge.RowLive, live);
        var width = Cell(list, layers, row.IntelText, sub, x, baseline, tracking, live, alpha * incoming, clip);
        var proof = row.IntelProof(now);
        if (proof > 0.002 && row.IntelHasScore && width > 0)
        {
            BoardScoreDraw.Underline(list, x - width, width, baseline, size, ink, alpha * incoming * proof, Palette.HighContrast, clip);
        }

        if (row.IntelOutgoing(now) is { } old)
        {
            var oldWidth = Cell(list, layers, old, sub, x, baseline, tracking, live, alpha * outgoing, clip);
            if (row.IntelOutgoingProvisional && oldWidth > 0)
            {
                BoardScoreDraw.Underline(list, x - oldWidth, oldWidth, baseline, size, ink, alpha * outgoing, Palette.HighContrast, clip);
            }
        }
    }

    // One text of the 性能 cell, right-aligned at x, in the live set by `live` and the resting set by the rest; returns its width.
    private double Cell(BoardDrawList list, IBoardLayers layers, string text, bool sub, double x, double baseline, double tracking, double live,
        double alpha, RectBounds clip)
    {
        var gauge = GaugePalette;
        var baked = Style == BoardStyle.Neon && gauge.Glows;
        var width = 0.0;
        if (live > 0.002)
        {
            width = BoardValueGlyphs.EmitOnBaseline(list, layers, text, sub ? BoardValueSet.IntelSub : BoardValueSet.IntelGroup, x, 1, baseline,
                tracking, gauge.RowLive, baked ? 0 : 1, alpha * live * (baked ? gauge.RowLive.A : 1), clip);
        }

        if (live < 0.998)
        {
            width = BoardValueGlyphs.EmitOnBaseline(list, layers, text, sub ? BoardValueSet.IntelSubRest : BoardValueSet.IntelGroupRest, x, 1,
                baseline, tracking, gauge.RowQuiet, 1, alpha * (1 - live), clip);
        }

        return width;
    }

    // The CSS punch: 1 → 1.06 at 35 % → 1 over 0.4 s.
    private double PunchScale(double now)
    {
        var t = (now - _punchStart) / PunchSeconds;
        if (t is < 0 or >= 1)
        {
            return 1;
        }

        var up = t < 0.35 ? Easing.InOutSine(t / 0.35) : 1 - Easing.InOutSine((t - 0.35) / 0.65);
        return 1 + 0.06 * up;
    }

    private (Rgba Tint, double Amount, double Dim) Flash(BoardRowMotion row, double now)
    {
        var t = row.FlashProgress(now);
        if (t >= 1 || row.FlashDirection == 0 || Effects.Policy.ReduceMotion)
        {
            return (default, 0, 1);
        }

        if (row.FlashDirection < 0)
        {
            return (default, 0, 0.45 + 0.55 * Easing.OutCubic(t));
        }

        // High contrast keeps values in their solid ink.
        if (Palette.HighContrast)
        {
            return (default, 0, 1);
        }

        // White-hot, then the row's heat color, then the layer's own color. On light the prototypes' white-hot reads only through
        // its heat glow, which glyph layers do not have, so the flash starts at the heat color.
        var heat = HeatMath.Heat(row.Live, _tick?.Redline ?? HeatMath.DefaultRedline);
        var color = Dark ? Rgba.Lerp(HeatPalette.WhiteHot, HeatPalette.Sample(heat), Math.Clamp(t / 0.4, 0, 1)) : EffortCapsulePalette.Heat(heat, false);
        return (color, 1 - Easing.OutCubic(t), 1);
    }

    private void Fire(BoardTick previous, BoardTick tick, double now)
    {
        var reduce = Effects.Policy.ReduceMotion;
        if (!reduce && previous.Value > 0 && tick.Value > previous.Value * (1 + PunchRatio) + PunchFloor)
        {
            _punchStart = now;
            var (left, top, width, height) = HeroNumber;
            Effects.Punch(left, top, width, height, (tick.Value - previous.Value) / Math.Max(1, previous.Value));
        }

        var celebrate = Effects.Policy.Celebrate && !reduce;
        if (tick.NewPeak)
        {
            var (left, top, width, height) = HeroNumber;
            Effects.NewPeak(left + width / 2, top + height / 2);
            if (celebrate)
            {
                _number.Sheen(now);
                _callouts.Add(new BoardCallout("#peak", BoardCalloutKind.NewPeak, string.Empty, null, now));
            }
        }

        foreach (var e in tick.Events)
        {
            if (RowFx(tick, e) is not { } fx)
            {
                continue;
            }

            var leader = e.Kind == BoardRankEventKind.NewLeader;
            if (leader)
            {
                Effects.NewLeader(fx);
            }
            else
            {
                Effects.Overtake(fx);
            }

            if (reduce)
            {
                continue;
            }

            _callouts.Add(new BoardCallout(e.Id + "#climb", BoardCalloutKind.Climb, "▲" + (e.From - e.To).ToString(CultureInfo.InvariantCulture),
                e.Id, now));
            if (Style == BoardStyle.Timing)
            {
                _callouts.Add(new BoardCallout(e.Id + "#block", BoardCalloutKind.Block, e.To.ToString(CultureInfo.InvariantCulture), e.Id, now));
                if (leader && celebrate && GroupOf(tick, e.Id) is { } group)
                {
                    _callouts.Add(new BoardCallout("#leader", BoardCalloutKind.NewLeader, group.Root.Project, e.Id, now));
                }
            }
        }
    }

    private static BoardGroup? GroupOf(BoardTick tick, string id)
    {
        foreach (var entry in tick.Entries)
        {
            if (entry.Kind == BoardEntryKind.Group && string.Equals(entry.Key, id, StringComparison.Ordinal))
            {
                return entry.Group;
            }
        }

        return null;
    }

    private BoardRowFx? RowFx(BoardTick tick, BoardRankEvent e)
    {
        var metrics = Metrics;
        BoardEntry? climber = null;
        BoardEntry? passed = null;
        foreach (var entry in tick.Entries)
        {
            if (entry.Kind != BoardEntryKind.Group)
            {
                continue;
            }

            if (climber is not null && passed is null)
            {
                passed = entry;
            }

            if (string.Equals(entry.Key, e.Id, StringComparison.Ordinal))
            {
                climber = entry;
            }
        }

        if (climber is null)
        {
            return null;
        }

        var centerY = metrics.ListTop + climber.Y + RowHeight(climber) / 2;
        var passedY = passed is null ? double.NaN : metrics.ListTop + passed.Y + RowHeight(passed) / 2;
        var rankX = metrics.ListLeft + 14;
        return new BoardRowFx(climber.Key, metrics.ListLeft, metrics.ListLeft + metrics.ListWidth, centerY, RowHeight(climber), rankX, centerY,
            passedY, HeatPalette.SourceColor(climber.Group.Root.Source));
    }
}
