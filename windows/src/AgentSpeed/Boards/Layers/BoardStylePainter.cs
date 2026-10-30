using System.Globalization;
using System.Numerics;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Modes;
using Vortice;
using Vortice.Direct2D1;

namespace AgentSpeed.Boards.Layers;

/// <summary>The fonts of the live metrics (DESIGN 2c, Live metrics), resolved per style and size by its painter.</summary>
internal enum BoardReadoutFont
{
    /// <summary>性能's label over its number (glass, neon).</summary>
    MainLabel,

    /// <summary>The table's labels (glass, neon).</summary>
    TableLabel,

    /// <summary>Timing's tab labels.</summary>
    TabLabel,

    /// <summary>The table's units.</summary>
    Unit,

    /// <summary><c>≥</c> at 0.85 × the table number's size.</summary>
    AtLeast,

    /// <summary>The subscription tag at 0.8 × the table number's size.</summary>
    Tag,

    GaugeMain,

    GaugeTable,

    IntelGroup,

    IntelSub,

    IntelGroupRest,

    IntelSubRest,

    /// <summary>The captions line.</summary>
    Captions,
}

/// <summary>What a row layer shows, resolved once per row from the tick.</summary>
/// <param name="Column">The shared capsule column's width (the prototypes' <c>--capw</c>: the widest visible capsule).</param>
/// <param name="Fastest">Timing: this row's agent is the fastest of all (the purple dot).</param>
/// <param name="Interval">Timing: <c>boards.leader</c> on the first group (the scene composes the other intervals).</param>
internal readonly record struct BoardRowContent(
    BoardScene Scene,
    BoardTick Tick,
    BoardPalette Palette,
    BoardEntry Entry,
    double Live,
    string Name,
    string Detail,
    AgentPhase Phase,
    SourceKind Source,
    EffortCapsule Capsule,
    float Column,
    bool Fastest,
    string Interval)
{
    public bool Lead => Entry.Kind == BoardEntryKind.Group && Entry.Rank == 1;

    public bool Compact => Scene.Size == BoardSize.S;
}

/// <summary>
/// The shared part of the three styles' painters (DESIGN 2c Content): layer sizes and signatures, what each row says, the
/// capsule column, and the parts every style draws alike (glyph layers, the elbow of sub rows, the more lines' dots).
/// A layer's signature holds everything it shows, so an unchanged layer is never rasterized again.
/// </summary>
internal abstract class BoardStylePainter : IBoardLayerPainter, IDisposable
{
    private readonly Dictionary<(BoardTextStyle Style, char Glyph), float> _advances = [];
    private string? _locale;
    private double _glyphHeat = double.NaN;
    private BoardTick? _columnTick;
    private float _column;
    private string? _fastest;
    private ((BoardStyle, BoardSize, bool, bool, string, int) Key, string Text)? _theme;
    private readonly Dictionary<(BoardTextStyle Style, float Height), float> _baselines = [];
    private (string Key, double Width)? _gaugeWidth;
    private ((BoardSize, bool) Key, BoardRowFigures Figures)? _figures;

    protected BoardStylePainter(BoardStyle style, Localizer localizer, BoardText text)
    {
        Style = style;
        Loc = localizer;
        Text = text;
        Capsule = new BoardModeCapsule(text);
    }

    public BoardStyle Style { get; }

    protected Localizer Loc { get; }

    protected BoardText Text { get; }

    protected BoardModeCapsule Capsule { get; }

    /// <summary>Width of the value column's layer at the row's right end.</summary>
    protected abstract float ValueWidth(BoardSize size);

    /// <summary>How far the value layer reaches left over the body (timing's fastest dot sits just before the value).</summary>
    protected virtual float ValueBleed => 0;

    protected abstract BoardCapsuleLook CapsuleLook(BoardScene scene, bool sub);

    /// <summary><c>mode.configured.mark</c> for a style's look.</summary>
    protected string ConfiguredMark => Loc["mode.configured.mark"];

    /// <summary>The hero number's text style, without tracking (the scene spaces the glyphs by <c>BoardHeroLayout.Tracking</c>).</summary>
    protected abstract BoardTextStyle NumberStyle(BoardScene scene);

    /// <summary>
    /// The style of a set of row numbers (<c>.speed</c>, a sub row's <c>.sub .speed</c>, timing's <c>.gap</c>), without tracking;
    /// <paramref name="dim"/> is the weight of the en dash shown below 0.05.
    /// </summary>
    protected abstract BoardTextStyle ValueStyle(BoardValueSet set, bool dim);

    /// <summary>A live metrics font of the style at the scene's size, without tracking unless it is a label's.</summary>
    protected abstract BoardTextStyle ReadoutStyle(BoardScene scene, BoardReadoutFont font);

    /// <summary>
    /// A glyph set drawn in its final color with its glow baked in (neon's lit digits on dark), or null for a white set the scene
    /// tints.
    /// </summary>
    protected virtual (Rgba Color, (Rgba Color, float Radius)[] Glows)? Baked(BoardScene scene, BoardValueSet set) => null;

    /// <summary>The text style of a glyph set; a two-line group row's figures (M and L) are 2 pt larger.</summary>
    private BoardTextStyle GlyphStyle(BoardScene scene, BoardValueSet set, bool dim)
    {
        var style = BaseGlyphStyle(scene, set, dim);
        return set is BoardValueSet.Group or BoardValueSet.IntelGroup or BoardValueSet.IntelGroupRest && scene.Metrics.TwoLineRows
            ? style.With(style.Size + (float)BoardRowFigures.Growth)
            : style;
    }

    private BoardTextStyle BaseGlyphStyle(BoardScene scene, BoardValueSet set, bool dim) => set switch
    {
        BoardValueSet.IntelGroup => ReadoutStyle(scene, BoardReadoutFont.IntelGroup),
        BoardValueSet.IntelSub => ReadoutStyle(scene, BoardReadoutFont.IntelSub),
        BoardValueSet.IntelGroupRest => ReadoutStyle(scene, BoardReadoutFont.IntelGroupRest),
        BoardValueSet.IntelSubRest => ReadoutStyle(scene, BoardReadoutFont.IntelSubRest),
        BoardValueSet.GaugeMain or BoardValueSet.GaugeMainPlain => ReadoutStyle(scene, BoardReadoutFont.GaugeMain),
        BoardValueSet.GaugeTable or BoardValueSet.GaugeTablePlain => ReadoutStyle(scene, BoardReadoutFont.GaugeTable),
        _ => ValueStyle(set, dim),
    };

    public BoardRowFigures Figures(BoardScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var key = (scene.Size, scene.Metrics.TwoLineRows);
        if (_figures is { } cached && cached.Key == key)
        {
            return cached.Figures;
        }

        var intelligence = GlyphStyle(scene, BoardValueSet.IntelGroup, false);
        var value = GlyphStyle(scene, BoardValueSet.Group, false);
        var interval = ValueStyle(BoardValueSet.Interval, false);
        // As the scene composes them: glass's 性能 without tracking, every other figure at −0.02 em.
        var figures = new BoardRowFigures(
            BoardRowFigures.Width(BoardRowFigures.IntelligenceSample, c => Advance(c, intelligence), Style == BoardStyle.Glass ? 0 : -0.02 * intelligence.Size),
            BoardRowFigures.WidestValue(c => Advance(c, value), -0.02 * value.Size),
            Text.CapHeight(intelligence), Text.CapHeight(value), Text.CapHeight(interval));
        _figures = (key, figures);
        return figures;
    }

    public BoardLayerSpec? Describe(in BoardLayerRequest request)
    {
        var scene = request.Scene;
        if (scene.Tick is not { } tick)
        {
            return null;
        }

        Text.Locale = Loc.Culture.Name;
        if (!string.Equals(_locale, Loc.Culture.Name, StringComparison.Ordinal))
        {
            _locale = Loc.Culture.Name;
            _advances.Clear();
        }
        var metrics = scene.Metrics;
        var theme = Theme(scene);
        switch (request.Kind)
        {
            case BoardLayerKind.RowBody when request.Row is { } row:
            {
                var content = Content(scene, tick, row.Entry, row.Live);
                var columns = BodyColumns(scene, row.Entry);
                // A two-line group row's detail runs on under the value column, so its body spans the list.
                var width = row.Entry.TwoLine ? metrics.ListWidth : columns;
                var more = row.Entry.Kind is BoardEntryKind.MoreSubs or BoardEntryKind.MoreGroups ? MoreText(row.Entry, row.Live) : string.Empty;
                return new BoardLayerSpec(width, scene.RowHeight(row.Entry), string.Join('|', theme, "b", row.Entry.Kind, row.Entry.Rank, row.Entry.TwoLine,
                    content.Name, content.Detail, content.Source, content.Capsule.Effort, content.Capsule.Speed,
                    content.Capsule.EffortConfigured, content.Capsule.SpeedConfigured,
                    content.Column.ToString("0.#", CultureInfo.InvariantCulture), content.Interval, more, BodyKey(content)), MeterX(content, (float)columns));
            }

            case BoardLayerKind.Badge when request.Row is { } row && BoardPulses.Badge(row.Entry) is { } badge:
            {
                var sub = row.Entry.Kind == BoardEntryKind.Sub;
                var (width, height) = BoardPulses.BadgeSize(Style, sub, badge.Phase, row.Entry.TwoLine);
                return new BoardLayerSpec(width, height, string.Join('|', theme, "bg", badge.Source, badge.Phase, sub, row.Entry.TwoLine));
            }

            case BoardLayerKind.RowValue when request.Row is { Entry.Kind: BoardEntryKind.Group or BoardEntryKind.Sub } row:
            {
                var content = Content(scene, tick, row.Entry, row.Live);
                return HasValueLayer(content) ? new BoardLayerSpec(ValueWidth(scene.Size) + ValueBleed, scene.RowHeight(row.Entry),
                    string.Join('|', theme, "v", row.Entry.Kind, row.Entry.TwoLine, ValueKey(content))) : null;
            }

            case BoardLayerKind.ValueGlyph:
            {
                var style = GlyphStyle(scene, request.Set, request.Glyph == BoardValueGlyphs.Dash);
                var pad = BoardValueGlyphs.PadOf(request.Set);
                var height = Math.Ceiling(style.Size * 1.3) + 2 * pad;
                return new BoardLayerSpec(Advance(request.Glyph, style) + 2 * pad, height,
                    string.Join('|', theme, "vg", request.Set, request.Glyph, Baked(scene, request.Set) is null ? "w" : "b"), Baseline: GlyphBaseline(style, height));
            }

            case BoardLayerKind.GaugeText:
                return DescribeGaugeText(scene, tick, request.Part, theme);

            case BoardLayerKind.Captions:
                return scene.Metrics.IntelligenceColumn
                    ? new BoardLayerSpec(metrics.ListWidth, scene.Metrics.CaptionsHeight, string.Join('|', theme, "cap", Capsules(scene, tick)))
                    : null;

            case BoardLayerKind.Hero:
            {
                var hero = scene.Hero;
                return new BoardLayerSpec(hero.Width, hero.Height, string.Join('|', theme, "h", tick.Title, tick.GeneratingCount, tick.ToolsCount,
                    tick.Generating, HeroHeatKey(tick), HeroLimit(scene, tick).ToString("0.#", CultureInfo.InvariantCulture)));
            }

            case BoardLayerKind.Unit:
            {
                var pad = BoardHeroLayout.UnitPad;
                return new BoardLayerSpec(Text.Measure(Loc["unit.tokS"], UnitStyle) + 2 * pad, scene.Hero.Height,
                    string.Join('|', theme, "u", Loc["unit.tokS"], HeroHeatKey(tick)));
            }

            case BoardLayerKind.History:
            {
                var box = scene.Hero.History;
                var pad = BoardHeroLayout.HistoryPad;
                return new BoardLayerSpec(box.Width + 2 * pad, box.Height + 2 * pad, string.Join('|', theme, "hi", HistoryKey(tick.History),
                    HistoryLookKey(tick)));
            }

            case BoardLayerKind.Footer:
                return new BoardLayerSpec(metrics.Side - 2 * metrics.Pad, metrics.FooterHeight,
                    string.Join('|', theme, "f", tick.TodayText, tick.CostText, tick.PeakText));

            case BoardLayerKind.Backdrop:
                return DescribeBackdrop(scene, theme);

            case BoardLayerKind.Glyph:
            {
                var style = NumberStyle(scene);
                var advance = Advance(request.Glyph, style);
                var pad = BoardHeroNumber.GlyphPad;
                return new BoardLayerSpec(advance + 2 * pad, scene.Hero.NumberHeight + 2 * pad,
                    string.Join('|', theme, "g", request.Glyph, GlyphKey(scene)));
            }

            case BoardLayerKind.Callout when request.Callout is { } callout:
                return DescribeCallout(scene, callout, theme);

            default:
                return null;
        }
    }

    public void Paint(ID2D1DeviceContext context, in BoardLayerRequest request, in BoardLayerSpec spec)
    {
        var scene = request.Scene;
        if (scene.Tick is not { } tick)
        {
            return;
        }

        context.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        var width = (float)spec.Width;
        var height = (float)spec.Height;
        switch (request.Kind)
        {
            case BoardLayerKind.RowBody when request.Row is { } row:
            {
                var content = Content(scene, tick, row.Entry, row.Live);
                switch (row.Entry.Kind)
                {
                    case BoardEntryKind.Group:
                        PaintGroup(context, content, (float)BodyColumns(scene, row.Entry), height);
                        break;
                    case BoardEntryKind.Sub:
                        PaintSub(context, content, width, height);
                        break;
                    default:
                        PaintMore(context, content, MoreText(row.Entry, row.Live), width, height);
                        break;
                }

                break;
            }

            case BoardLayerKind.RowValue when request.Row is { } row:
                PaintValue(context, Content(scene, tick, row.Entry, row.Live), width, height);
                break;
            case BoardLayerKind.Hero:
                PaintHero(context, scene, tick, width, height);
                break;
            case BoardLayerKind.Unit:
                PaintUnit(context, scene, tick, (float)BoardHeroLayout.UnitPad);
                break;
            case BoardLayerKind.History:
            {
                var pad = BoardHeroLayout.HistoryPad;
                var box = scene.Hero.History;
                PaintHistory(context, scene, tick, new BoardBox(pad, pad, box.Width, box.Height));
                break;
            }

            case BoardLayerKind.Footer:
                PaintFooter(context, scene, tick, width, height);
                break;
            case BoardLayerKind.Backdrop:
                PaintBackdrop(context, scene, width, height);
                break;
            case BoardLayerKind.Glyph:
                PaintGlyph(context, scene, request.Glyph, width, height);
                break;
            case BoardLayerKind.Callout when request.Callout is { } callout:
                PaintCallout(context, scene, callout, width, height);
                break;
            case BoardLayerKind.Badge when request.Row is { } row && BoardPulses.Badge(row.Entry) is { } badge:
                PaintBadge(context, scene.Palette, badge.Phase, SourceColor(badge.Source), row.Entry.Kind == BoardEntryKind.Sub, width, height);
                break;
            case BoardLayerKind.ValueGlyph:
            {
                // White: the scene tints each row's glyphs to the row's color; neon's lit sets bake their color and glow.
                var style = GlyphStyle(scene, request.Set, request.Glyph == BoardValueGlyphs.Dash);
                var pad = (float)BoardValueGlyphs.PadOf(request.Set);
                var text = request.Glyph.ToString();
                if (Baked(scene, request.Set) is { } baked)
                {
                    foreach (var (color, radius) in baked.Glows)
                    {
                        BoardPaint.Glow(context, color, radius, c =>
                        {
                            using var white = BoardPaint.Brush(c, Rgba.White);
                            Text.Draw(c, text, style, pad, height / 2, white);
                        });
                    }

                    using var lit = BoardPaint.Brush(context, baked.Color);
                    Text.Draw(context, text, style, pad, height / 2, lit);
                    break;
                }

                using (var white = BoardPaint.Brush(context, Rgba.White))
                {
                    Text.Draw(context, text, style, pad, height / 2, white);
                }

                break;
            }

            case BoardLayerKind.GaugeText:
                PaintText(context, GaugeText(scene, tick, request.Part), GaugeStyle(scene, request.Part), height);
                break;
            case BoardLayerKind.Captions:
                PaintCaptions(context, scene, tick, width, height);
                break;
        }
    }

    private const string CostDisplayAtLeast = "≥";

    // ---- Live metrics (DESIGN 2c, Live metrics) ----

    /// <summary>The y of a glyph layer's baseline (its line centred in the layer), measured once per style and height.</summary>
    private float GlyphBaseline(in BoardTextStyle style, double height)
    {
        var key = (style, (float)height);
        if (!_baselines.TryGetValue(key, out var baseline))
        {
            using var layout = Text.Layout("0", style);
            baseline = BoardText.Baseline(layout, (float)height / 2);
            _baselines[key] = baseline;
        }

        return baseline;
    }

    /// <summary>The centre on which <see cref="BoardText.Draw"/> puts a line of <paramref name="style"/> so its baseline lands on <paramref name="baseline"/>.</summary>
    protected float CenterOnBaseline(in BoardTextStyle style, float baseline) => baseline - GlyphBaseline(style, 0);

    // A white text layer, TextPad around its text, its baseline recorded.
    private BoardLayerSpec? TextLayer(string text, in BoardTextStyle style, string theme, string kind)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var pad = BoardGauge.TextPad;
        var height = Math.Ceiling(style.Size * 1.6) + 2 * pad;
        return new BoardLayerSpec(Text.Measure(text, style) + 2 * pad, height, string.Join('|', theme, kind, text), Baseline: GlyphBaseline(style, height));
    }

    private void PaintText(ID2D1DeviceContext context, string text, in BoardTextStyle style, float height)
    {
        using var white = BoardPaint.Brush(context, Rgba.White);
        Text.Draw(context, text, style, (float)BoardGauge.TextPad, height / 2, white);
    }

    private BoardLayerSpec? DescribeGaugeText(BoardScene scene, BoardTick tick, BoardGaugePart part, string theme)
    {
        var readouts = scene.Readouts;
        var on = part switch
        {
            BoardGaugePart.LabelIntelligence => readouts.Intelligence,
            BoardGaugePart.LabelThinking or BoardGaugePart.UnitThinking => readouts.Thinking,
            _ => readouts.Cost,
        };

        return on && readouts.Hero ? TextLayer(GaugeText(scene, tick, part), GaugeStyle(scene, part), theme, "gt" + (int)part) : null;
    }

    private string GaugeText(BoardScene scene, BoardTick tick, BoardGaugePart part) => part switch
    {
        BoardGaugePart.LabelIntelligence => Loc["intel.glyph"],
        BoardGaugePart.LabelThinking => Loc["intel.thinking.glyph"],
        BoardGaugePart.LabelCost => Loc["cost.glyph"],
        BoardGaugePart.UnitThinking => Loc["unit.tokS"],
        BoardGaugePart.UnitCost => tick.Gauge.Cost.Unit,
        BoardGaugePart.AtLeast => CostDisplayAtLeast,
        _ => Loc["cost.subscription.tag"],
    };

    private BoardTextStyle GaugeStyle(BoardScene scene, BoardGaugePart part) => part switch
    {
        BoardGaugePart.LabelIntelligence => ReadoutStyle(scene, Style == BoardStyle.Timing ? BoardReadoutFont.TabLabel : BoardReadoutFont.MainLabel),
        BoardGaugePart.LabelThinking or BoardGaugePart.LabelCost =>
            ReadoutStyle(scene, Style == BoardStyle.Timing ? BoardReadoutFont.TabLabel : BoardReadoutFont.TableLabel),
        BoardGaugePart.UnitThinking or BoardGaugePart.UnitCost => ReadoutStyle(scene, BoardReadoutFont.Unit),
        BoardGaugePart.AtLeast => ReadoutStyle(scene, BoardReadoutFont.AtLeast),
        _ => ReadoutStyle(scene, BoardReadoutFont.Tag),
    };

    /// <summary>
    /// The hero's left column ends 8 before the sub-gauge (layout B), else before the history; the sub-gauge is as wide as its
    /// nominal width or its table, whichever is more, plus what a wider value holds.
    /// </summary>
    protected float HeroLimit(BoardScene scene, BoardTick tick)
    {
        if (!scene.Metrics.Gauge)
        {
            return (float)scene.Hero.History.Left - 8;
        }

        return (float)(scene.Hero.Width - GaugeWidth(scene, tick) - scene.Gauge.Extra - 8);
    }

    // The sub-gauge's width from the measured cells (DESIGN 2c, Table cells), cached per theme and cost unit.
    private double GaugeWidth(BoardScene scene, BoardTick tick)
    {
        var readouts = scene.Readouts;
        var key = Theme(scene) + "|" + tick.Gauge.Cost.Unit;
        if (_gaugeWidth is { } cached && cached.Key == key)
        {
            return cached.Width;
        }

        var spec = BoardGaugeSpec.For(Style, scene.Size);
        var label = 0f;
        foreach (var part in new[] { BoardGaugePart.LabelIntelligence, BoardGaugePart.LabelThinking, BoardGaugePart.LabelCost })
        {
            var on = part switch
            {
                BoardGaugePart.LabelIntelligence => readouts.Intelligence && Style == BoardStyle.Timing,
                BoardGaugePart.LabelThinking => readouts.Thinking,
                _ => readouts.Cost,
            };

            if (on)
            {
                label = Math.Max(label, Text.Measure(GaugeText(scene, tick, part), GaugeStyle(scene, part)));
            }
        }

        var unitStyle = ReadoutStyle(scene, BoardReadoutFont.Unit);
        var unit = Math.Max(readouts.Thinking ? Text.Measure(Loc["unit.tokS"], unitStyle) : 0, readouts.Cost ? Text.Measure(tick.Gauge.Cost.Unit, unitStyle) : 0);
        var number = ReadoutStyle(scene, BoardReadoutFont.GaugeTable);
        var tracking = BoardGauge.TableTracking(Style, spec);
        var sample = 0.0;
        foreach (var c in BoardGauge.NumberSample)
        {
            sample += Advance(c, number);
        }

        sample += (BoardGauge.NumberSample.Length - 1) * tracking;
        var cells = new BoardGaugeCells(label, Text.Measure(CostDisplayAtLeast, ReadoutStyle(scene, BoardReadoutFont.AtLeast)), sample, unit);
        var width = BoardGaugeLayout.Width(Style, spec, cells, readouts);
        _gaugeWidth = (key, width);
        return width;
    }

    // The captions line: a hairline from the rank column to the end of the capsule column, 性能 over the 性能 cells, tok/s over the
    // values and timing's 間隔 over the interval column (not at S), all in ink-3.
    private void PaintCaptions(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float width, float height)
    {
        var palette = scene.Palette;
        var columns = scene.Columns;
        var style = ReadoutStyle(scene, BoardReadoutFont.Captions);
        var mid = height / 2;
        var start = Style == BoardStyle.Timing ? 4f : 6f;
        var end = width - (float)columns.CapsuleRight - 2;
        if (end > start)
        {
            BoardPaint.FillRound(context, start, mid - 0.5f, end - start, 1, 0, palette.Line);
        }

        using var ink3 = BoardPaint.Brush(context, palette.Ink3);
        Text.Draw(context, Loc["intel.glyph"], style, width - (float)columns.IntelligenceRight, mid, ink3, right: true);
        Text.Draw(context, Loc["unit.tokS"], style, width - (float)columns.ValueRight, mid, ink3, right: true);
        if (Style == BoardStyle.Timing && scene.Size != BoardSize.S)
        {
            Text.Draw(context, Loc["boards.gap"], style, width - (float)columns.IntervalRight, mid, ink3, right: true);
        }
    }

    // The capsule column's identity for the captions layer (its hairline ends with the column).
    private string Capsules(BoardScene scene, BoardTick tick)
    {
        if (!ReferenceEquals(tick, _columnTick))
        {
            _columnTick = tick;
            _column = Column(scene, tick);
            _fastest = Fastest(tick);
        }

        return _column.ToString("0.#", CultureInfo.InvariantCulture);
    }

    // The width a row body lays its columns out in: the list less the value column for group and sub rows (widened to the
    // measured widest value at M and L).
    private double BodyColumns(BoardScene scene, BoardEntry entry) =>
        scene.Metrics.ListWidth - (entry.Kind is BoardEntryKind.Group or BoardEntryKind.Sub
            ? Math.Max(ValueWidth(scene.Size), scene.Columns.ValueRight + scene.Columns.Value)
            : 0);

    /// <summary>The capsule column's right end in a row body <paramref name="width"/> wide, moved left by the 性能 column.</summary>
    protected float RowCapsuleRight(in BoardRowContent row, float width) =>
        row.Scene.Metrics.IntelligenceColumn ? (float)(row.Scene.Metrics.ListWidth - row.Scene.Columns.CapsuleRight) : CapsuleRight(row, width);

    /// <summary>
    /// A two-line group row's name on the first baseline (16), trimmed at its tail to the name column, and its detail alone on the
    /// second, trimmed at its tail where the first figure column starts (DESIGN 2c, Ranking rows: The second line).
    /// </summary>
    protected void DrawTwoLines(ID2D1DeviceContext context, in BoardRowContent row, in BoardTextStyle nameStyle, Rgba nameColor, in BoardTextStyle detailStyle,
        Rgba detailColor, float x, float right)
    {
        var room = right - x;
        if (room > 4)
        {
            using var nameBrush = BoardPaint.Brush(context, nameColor);
            Text.DrawOnBaseline(context, row.Name, nameStyle, x, (float)BoardScene.FirstBaseline, nameBrush, room);
        }

        var detailRoom = (float)row.Scene.Columns.DetailRight(row.Scene.Metrics.ListWidth) - x;
        if (!string.IsNullOrEmpty(row.Detail) && detailRoom > 4)
        {
            using var detailBrush = BoardPaint.Brush(context, detailColor);
            Text.DrawOnBaseline(context, row.Detail, detailStyle, x, (float)BoardScene.SecondBaseline(Style), detailBrush, detailRoom);
        }
    }

    public virtual void Dispose()
    {
    }

    /// <summary>
    /// A row's phase badge centered in its layer (the scene places it and pulses a generating one, <see cref="BoardPulses"/>);
    /// row bodies leave it out. By default the tools and idle badges of <see cref="Badge"/>.
    /// </summary>
    protected virtual void PaintBadge(ID2D1DeviceContext context, BoardPalette palette, AgentPhase phase, Rgba source, bool sub, float width,
        float height) =>
        Badge(context, phase, source, palette.Ink3, width / 2 - 5, height / 2, Style == BoardStyle.Neon, !palette.HighContrast);

    /// <summary>Whether the style shimmers a top-rung meter (the prototypes' <c>.cap.top .meter::after</c>).</summary>
    protected virtual bool Shimmers => false;

    /// <summary>The right end of the capsule column in a row body <paramref name="width"/> wide.</summary>
    protected virtual float CapsuleRight(in BoardRowContent row, float width) => width - 8;

    protected abstract void PaintGroup(ID2D1DeviceContext context, in BoardRowContent row, float width, float height);

    protected abstract void PaintSub(ID2D1DeviceContext context, in BoardRowContent row, float width, float height);

    protected abstract void PaintMore(ID2D1DeviceContext context, in BoardRowContent row, string text, float width, float height);

    protected virtual void PaintValue(ID2D1DeviceContext context, in BoardRowContent row, float width, float height)
    {
    }

    /// <summary>The hero's title and counts (and timing's live label) in hero coordinates; the number, unit and history are apart.</summary>
    protected abstract void PaintHero(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float width, float height);

    /// <summary>The unit's text style (its layer is as wide as the unit).</summary>
    protected abstract BoardTextStyle UnitStyle { get; }

    /// <summary>The unit starting at <paramref name="x"/>, on the number's baseline (the layer's top is the hero's).</summary>
    protected abstract void PaintUnit(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float x);

    /// <summary>The history in <paramref name="box"/> (layer coordinates).</summary>
    protected abstract void PaintHistory(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, BoardBox box);

    /// <summary>Identity of how the history is drawn beyond its values (its heat color; neon: lit segments and the peak).</summary>
    protected virtual int HistoryLookKey(BoardTick tick) => HeatKey(GlyphHeat(tick.Heat));

    /// <summary>The board's heat color in hero accents, stepped like the glyphs' glow (<see cref="GlyphHeat"/>), at least <paramref name="floor"/>.</summary>
    protected Rgba HeroHeat(BoardTick tick, double floor = 0.06) => HeatPalette.Sample(Math.Max(floor, GlyphHeat(tick.Heat)));

    protected abstract void PaintFooter(ID2D1DeviceContext context, BoardScene scene, BoardTick tick, float width, float height);

    protected abstract void PaintGlyph(ID2D1DeviceContext context, BoardScene scene, char glyph, float width, float height);

    protected abstract BoardLayerSpec? DescribeCallout(BoardScene scene, BoardCallout callout, string theme);

    protected abstract void PaintCallout(ID2D1DeviceContext context, BoardScene scene, BoardCallout callout, float width, float height);

    protected virtual BoardLayerSpec? DescribeBackdrop(BoardScene scene, string theme) => null;

    protected virtual void PaintBackdrop(ID2D1DeviceContext context, BoardScene scene, float width, float height)
    {
    }

    /// <summary>Extra identity of a row body (what a style draws beyond the shared content).</summary>
    protected virtual string BodyKey(in BoardRowContent row) => string.Empty;

    /// <summary>Whether the row has static parts by its value (its value itself is composed from glyphs).</summary>
    protected virtual bool HasValueLayer(in BoardRowContent row) => false;

    /// <summary>Identity of the static parts by a row's value.</summary>
    protected virtual string ValueKey(in BoardRowContent row) => string.Empty;

    /// <summary>Identity of the glyphs' look beyond the theme (a heat-colored glow).</summary>
    protected virtual string GlyphKey(BoardScene scene) => string.Empty;

    protected static int HeatKey(double heat) => (int)Math.Round(Math.Clamp(heat, 0, 1) * 32);

    /// <summary>Identity of the heat the hero's accents and the unit are drawn at (stepped, <see cref="GlyphHeat"/>).</summary>
    protected int HeroHeatKey(BoardTick tick) => HeatKey(GlyphHeat(tick.Heat));

    /// <summary>
    /// The heat the glyphs' glow (and neon's hero tints) are drawn at: it follows the board's heat in steps of 0.08, and at once
    /// across 0.02 (no glow below), so 14 glyph layers are not rasterized again for every small change.
    /// </summary>
    protected double GlyphHeat(double heat)
    {
        if (double.IsNaN(_glyphHeat) || Math.Abs(heat - _glyphHeat) >= 0.08 || heat <= 0.02 != _glyphHeat <= 0.02)
        {
            _glyphHeat = heat;
        }

        return _glyphHeat;
    }

    protected static Rgba SourceColor(SourceKind source) => HeatPalette.SourceColor(source);

    protected string MoreText(BoardEntry entry, double live) =>
        Loc.Format(entry.Kind == BoardEntryKind.MoreSubs ? "boards.moreSubs" : "boards.moreGroups", entry.Rest, SpeedFormat.Speed(live));

    // Where a row's top-rung meter starts in its body, for the scene's shimmer; NaN when it has none.
    private double MeterX(in BoardRowContent row, float width)
    {
        if (!Shimmers || row.Palette.HighContrast || row.Column <= 0 || !row.Capsule.IsTopRung || row.Capsule.Level is null ||
            row.Entry.Kind is not (BoardEntryKind.Group or BoardEntryKind.Sub))
        {
            return double.NaN;
        }

        return RowCapsuleRight(row, width) - row.Column + CapsuleLook(row.Scene, row.Entry.Kind == BoardEntryKind.Sub).Padding;
    }

    /// <summary>Draws the capsule right-aligned in the shared column ending at <paramref name="right"/>; returns the column's left.</summary>
    protected float DrawCapsule(ID2D1DeviceContext context, in BoardRowContent row, float right, float mid, float height)
    {
        if (row.Column <= 0)
        {
            return right;
        }

        var left = right - row.Column;
        if (row.Capsule.IsVisible)
        {
            Capsule.Look = CapsuleLook(row.Scene, row.Entry.Kind == BoardEntryKind.Sub);
            Capsule.Paint(context, row.Capsule, new RawRectF(left, mid - height / 2, right, mid + height / 2));
        }

        return left;
    }

    /// <summary>
    /// A name and its detail on one baseline (the prototypes' <c>.name b</c> and <c>.name small</c>, 6 pt apart): the name keeps
    /// its width while it fits (at least 22 pt), the detail takes what remains; both trim with an ellipsis.
    /// </summary>
    protected void DrawName(ID2D1DeviceContext context, string name, in BoardTextStyle nameStyle, Rgba nameColor, string detail,
        in BoardTextStyle detailStyle, Rgba detailColor, float x, float right, float mid)
    {
        var room = right - x;
        if (room <= 4)
        {
            return;
        }

        using var nameLayout = Text.Layout(name, nameStyle);
        var nameWidth = nameLayout.Metrics.WidthIncludingTrailingWhitespace;
        var shown = string.IsNullOrEmpty(detail) ? Math.Min(nameWidth, room) : Math.Max(Math.Min(22, room), Math.Min(nameWidth, room - 6));
        using var nameBrush = BoardPaint.Brush(context, nameColor);
        using var fitted = Text.Layout(name, nameStyle, shown);
        var top = BoardText.Top(fitted, mid);
        context.DrawTextLayout(new Vector2(x, top), fitted, nameBrush, DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
        var rest = room - shown - 6;
        if (string.IsNullOrEmpty(detail) || rest < 8)
        {
            return;
        }

        using var detailLayout = Text.Layout(detail, detailStyle, rest);
        var baseline = top + (fitted.LineMetrics.Length > 0 ? fitted.LineMetrics[0].Baseline : 0);
        var detailTop = baseline - (detailLayout.LineMetrics.Length > 0 ? detailLayout.LineMetrics[0].Baseline : 0);
        using var detailBrush = BoardPaint.Brush(context, detailColor);
        context.DrawTextLayout(new Vector2(x + shown + 6, detailTop), detailLayout, detailBrush, DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
    }

    /// <summary>
    /// A footer item from a localized format (<c>boards.today</c>, <c>boards.peak</c>): the label in <paramref name="label"/>, the
    /// value in <paramref name="value"/>, then <paramref name="suffix"/>; aligned at <paramref name="x"/> by <paramref name="align"/>
    /// (−1 left, 0 center, 1 right).
    /// </summary>
    protected void DrawLabeled(ID2D1DeviceContext context, string format, string valueText, string suffix, in BoardTextStyle label,
        Rgba labelColor, in BoardTextStyle value, Rgba valueColor, float x, float mid, int align)
    {
        var at = format.IndexOf("%@", StringComparison.Ordinal);
        var before = at < 0 ? string.Empty : format[..at];
        var after = (at < 0 ? string.Empty : format[(at + 2)..]) + suffix;
        var widths = Text.Measure(before, label) + Text.Measure(valueText, value) + Text.Measure(after, label);
        var left = align < 0 ? x : align == 0 ? x - widths / 2 : x - widths;
        using var labelBrush = BoardPaint.Brush(context, labelColor);
        using var valueBrush = BoardPaint.Brush(context, valueColor);
        left += Text.Draw(context, before, label, left, mid, labelBrush);
        left += Text.Draw(context, valueText, value, left, mid, valueBrush);
        Text.Draw(context, after, label, left, mid, labelBrush);
    }

    /// <summary>The prototypes' sub-row elbow (<c>.sub .name::before</c>): down from the row above, then right, 5 pt rounded.</summary>
    protected static void Elbow(ID2D1DeviceContext context, float x, float mid, Rgba color)
    {
        using var brush = BoardPaint.Brush(context, color);
        using var factory = context.Factory;
        using var path = factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(new Vector2(x + 0.5f, 0), FigureBegin.Hollow);
            sink.AddLine(new Vector2(x + 0.5f, mid - 5));
            sink.AddArc(new ArcSegment
            {
                Point = new Vector2(x + 5.5f, mid - 0.5f),
                Size = new Vortice.Mathematics.Size(5, 5),
                SweepDirection = SweepDirection.CounterClockwise,
                ArcSize = ArcSize.Small,
            });
            sink.AddLine(new Vector2(x + 7, mid - 0.5f));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        context.DrawGeometry(path, brush, 1);
    }

    /// <summary>The more lines' stack of three source dots (<c>.more .stack</c>).</summary>
    protected static float Stack(ID2D1DeviceContext context, float x, float mid, float size, float overlap, Rgba color, Rgba ring, float glow)
    {
        for (var i = 0; i < 3; i++)
        {
            var cx = x + size / 2 + i * (size - overlap);
            if (glow > 0)
            {
                BoardPaint.Glow(context, color, glow, c =>
                {
                    using var white = BoardPaint.Brush(c, Rgba.White);
                    c.FillEllipse(new Ellipse(new Vector2(cx, mid), size / 2, size / 2), white);
                });
            }

            if (ring.A > 0)
            {
                using var ringBrush = BoardPaint.Brush(context, ring);
                context.FillEllipse(new Ellipse(new Vector2(cx, mid), size / 2 + 1.5f, size / 2 + 1.5f), ringBrush);
            }

            using var brush = BoardPaint.Brush(context, color.MultiplyAlpha(0.85));
            context.FillEllipse(new Ellipse(new Vector2(cx, mid), size / 2, size / 2), brush);
        }

        return 3 * size - 2 * overlap;
    }

    /// <summary>The tools or idle badge in its 10 pt box at (<paramref name="x"/>, centered on <paramref name="mid"/>); generating is a layer.</summary>
    protected static void Badge(ID2D1DeviceContext context, AgentPhase phase, Rgba source, Rgba idle, float x, float mid, bool neon, bool glows)
    {
        switch (phase)
        {
            case AgentPhase.Generating:
                // A layer of its own, pulsed by the scene (BoardPulses).
                break;
            case AgentPhase.Tools:
            {
                var transform = context.Transform;
                context.Transform = Matrix3x2.CreateRotation(MathF.PI / 4, new Vector2(x + 5, mid)) * transform;
                if (neon && glows)
                {
                    BoardPaint.Glow(context, source, 6, c => BoardPaint.StrokeRound(c, x + 2.12f, mid - 2.88f, 5.76f, 5.76f, 2.2f, Rgba.White, 1.1f));
                }

                BoardPaint.StrokeRound(context, x + 2.12f, mid - 2.88f, 5.76f, 5.76f, 2.2f, source.MultiplyAlpha(0.9), 1.1f);
                context.Transform = transform;
                break;
            }

            default:
                Dot(context, x + 5, mid, 2, idle);
                break;
        }
    }

    protected static void Dot(ID2D1DeviceContext context, float cx, float cy, float radius, Rgba color)
    {
        using var brush = BoardPaint.Brush(context, color);
        context.FillEllipse(new Ellipse(new Vector2(cx, cy), radius, radius), brush);
    }

    /// <summary>One glyph of the hero number, its line centered half an em below the box's top (CSS <c>line-height: 1</c>).</summary>
    protected void DrawGlyph(ID2D1DeviceContext context, BoardScene scene, char glyph, Rgba color, Action<ID2D1DeviceContext, Action<ID2D1DeviceContext>>? glow)
    {
        var style = NumberStyle(scene);
        var pad = (float)BoardHeroNumber.GlyphPad;
        var text = glyph.ToString();
        var mid = pad + style.Size / 2;
        glow?.Invoke(context, c =>
        {
            using var white = BoardPaint.Brush(c, Rgba.White);
            Text.Draw(c, text, style, pad, mid, white);
        });
        using var brush = BoardPaint.Brush(context, color);
        Text.Draw(context, text, style, pad, mid, brush);
    }

    /// <summary>The baseline of the hero number in hero coordinates (the unit sits on it).</summary>
    protected float NumberBaseline(BoardScene scene)
    {
        var style = NumberStyle(scene);
        using var layout = Text.Layout("0", style);
        return BoardText.Baseline(layout, (float)scene.Hero.NumberTop + style.Size / 2);
    }

    /// <summary>A glyph's advance, measured once per style (the glyph layers are described on every tick).</summary>
    protected float Advance(char glyph, in BoardTextStyle style)
    {
        if (!_advances.TryGetValue((style, glyph), out var advance))
        {
            advance = Text.Measure(glyph.ToString(), style);
            _advances[(style, glyph)] = advance;
        }

        return advance;
    }

    /// <summary>
    /// The row's content from the tick (cached per tick: the capsule column and the fastest agent).
    /// </summary>
    protected BoardRowContent Content(BoardScene scene, BoardTick tick, BoardEntry entry, double live)
    {
        if (!ReferenceEquals(tick, _columnTick))
        {
            _columnTick = tick;
            _column = Column(scene, tick);
            _fastest = Fastest(tick);
        }

        var palette = scene.Palette;
        var agent = entry.Kind == BoardEntryKind.Group ? entry.Group.Root : entry.Agent;
        var capsule = agent is null || entry.Kind is BoardEntryKind.MoreSubs or BoardEntryKind.MoreGroups ? new EffortCapsule(null, null)
            : EffortCapsule.Of(agent);
        string name;
        var detail = string.Empty;
        var phase = AgentPhase.Idle;
        var interval = string.Empty;
        switch (entry.Kind)
        {
            case BoardEntryKind.Group:
            {
                var root = entry.Group.Root;
                name = root.Project;
                var subs = entry.Group.Subs.Count;
                detail = subs > 0 ? string.Join(" · ", new[] { root.Model, Loc.Format("boards.subCount", subs) }.Where(s => !string.IsNullOrEmpty(s)))
                    : root.Model ?? string.Empty;
                phase = entry.Group.BadgePhase;
                // Timing's leader says so; the other intervals are composed from glyphs by the scene.
                interval = Style == BoardStyle.Timing && entry.Rank == 1 ? Loc["boards.leader"] : string.Empty;
                break;
            }

            case BoardEntryKind.Sub when agent is not null:
                // A sub-agent shows what tells it apart: its own project, else its model.
                name = !string.Equals(agent.Project, entry.Group.Root.Project, StringComparison.Ordinal) ? agent.Project : agent.Model ?? agent.Project;
                phase = agent.Phase;
                break;
            default:
                name = string.Empty;
                break;
        }

        return new BoardRowContent(scene, tick, palette, entry, live, name, detail, phase, agent?.Source ?? entry.Group.Root.Source, capsule,
            _column, agent is not null && string.Equals(agent.Id, _fastest, StringComparison.Ordinal), interval);
    }

    // The part of every signature that a restyle, resize, appearance or language changes (made once per change, not per layer).
    private string Theme(BoardScene scene)
    {
        var key = (scene.Style, scene.Size, scene.Dark, scene.Palette.HighContrast, Loc.LanguageCode, scene.Readouts.Bits);
        if (_theme is not { } theme || theme.Key != key)
        {
            _theme = theme = (key, string.Join('|', scene.Style, scene.Size, scene.Dark ? "d" : "l", scene.Palette.HighContrast ? "hc" : string.Empty,
                Loc.LanguageCode, scene.Readouts.Bits));
        }

        return theme.Text;
    }

    private float Column(BoardScene scene, BoardTick tick)
    {
        var width = 0f;
        foreach (var entry in tick.Entries)
        {
            var agent = entry.Kind switch
            {
                BoardEntryKind.Group => entry.Group.Root,
                BoardEntryKind.Sub => entry.Agent,
                _ => null,
            };
            if (agent is null || (agent.Modes.IsEmpty && agent.SessionModes.IsEmpty && agent.ConfiguredModes.IsEmpty))
            {
                continue;
            }

            Capsule.Look = CapsuleLook(scene, entry.Kind == BoardEntryKind.Sub);
            width = Math.Max(width, Capsule.Measure(EffortCapsule.Of(agent)));
        }

        return MathF.Ceiling(width);
    }

    private static string? Fastest(BoardTick tick)
    {
        AgentStatus? fastest = null;
        var best = 0.0;
        foreach (var entry in tick.Entries)
        {
            if (entry.Kind != BoardEntryKind.Group)
            {
                continue;
            }

            foreach (var agent in entry.Group.Subs.Prepend(entry.Group.Root))
            {
                var live = agent.Live.ValueFor(tick.Metric);
                if (live > best)
                {
                    best = live;
                    fastest = agent;
                }
            }
        }

        return fastest?.Id;
    }

    /// <summary>
    /// Identity of the drawn history (glass's 60 s sparkline): an all-quiet window draws the same flat line however long it is.
    /// </summary>
    protected virtual int HistoryKey(IReadOnlyList<double> history)
    {
        var hash = new HashCode();
        var quiet = true;
        for (var i = Math.Max(0, history.Count - 60); i < history.Count; i++)
        {
            quiet &= history[i] < 0.05;
            hash.Add(Math.Round(history[i], 1));
        }

        if (quiet)
        {
            return 0;
        }

        hash.Add(Math.Min(history.Count, 60));
        return hash.ToHashCode();
    }
}
