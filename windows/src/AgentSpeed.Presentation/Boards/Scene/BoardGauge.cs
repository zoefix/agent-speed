using AgentSpeed.Presentation.Boards.Gpu;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Gpu;

namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>The sub-gauge's text layers (labels, units, <c>≥</c>, the tag), described and drawn by the style's painter.</summary>
public enum BoardGaugePart
{
    LabelIntelligence,
    LabelThinking,
    LabelCost,
    UnitThinking,
    UnitCost,
    AtLeast,
    Tag,
}

/// <summary>
/// The hero's sub-gauge of layout B (DESIGN 2c, Live metrics): 性能 large — glass's pearl arc, neon's LED digits and 12-segment
/// meter, timing's data box — with 思考 and 費用 smaller beneath it. 性能 is the board's performance score (DESIGN 12): it counts up on
/// a climb with the arc's end and the meter following, crossfades on a fall, rests pale and carries the provisional mark while
/// provisional (<see cref="BoardHeroScore"/>, <see cref="BoardScoreDraw"/>); the other numbers roll with the hero's (0.24 s); 思考
/// folds away after reading 0 for 3 s. Labels, units, <c>≥</c> and the tag are cached text layers; numbers are value glyphs;
/// the arc, meter, dots and box are instanced shapes. Nothing allocates per frame.
/// </summary>
public sealed class BoardGauge
{
    /// <summary>Room around a gauge text layer's text.</summary>
    public const double TextPad = 2;

    /// <summary>The number cell's sample: the table never moves for values up to this wide.</summary>
    public const string NumberSample = "0.00088";

    /// <summary>A wider value widens its cell at once and it narrows this long after the last value that needed it.</summary>
    public const double WideHoldSeconds = 6;

    private static readonly string[] Keys = ["#g.li", "#g.lt", "#g.lc", "#g.ut", "#g.uc", "#g.ge", "#g.tag"];

    private readonly BoardHeroScore _score = new();
    private readonly BoardReadoutNumber _thinking = new();
    private readonly BoardReadoutNumber _cost = new();
    private readonly BoardThinkingFold _fold = new();
    private bool _tag;
    private double _tagStart = double.NegativeInfinity;
    private double _wide;
    private double _wideAt = double.NegativeInfinity;
    private BoardHeroReadouts _readouts = BoardHeroReadouts.Empty;
    private RectBounds? _scoreArea;

    /// <summary>The layer key of a part.</summary>
    public static string KeyOf(BoardGaugePart part) => Keys[(int)part];

    public static IReadOnlyList<BoardGaugePart> Parts { get; } = Enum.GetValues<BoardGaugePart>();

    /// <summary>The readouts on display.</summary>
    public BoardHeroReadouts Readouts => _readouts;

    /// <summary>The 性能 on display, with its motion.</summary>
    public BoardHeroScore Score => _score;

    /// <summary>
    /// Where hovering opens the hero's score card (DESIGN 2c, Tooltip and score card), in canvas points as last drawn: glass's
    /// dial, neon's digits and meter, timing's 性能 row; null without 性能.
    /// </summary>
    public RectBounds? ScoreArea => _scoreArea;

    /// <summary>The width the sub-gauge takes beyond its measured cells while a wider value holds its cell (the hero's left column gives way).</summary>
    public double Extra { get; private set; }

    /// <summary>Applies a tick's readouts at <paramref name="now"/>.</summary>
    public void Apply(BoardTick tick, double now, bool animate, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(tick);
        var readouts = tick.Gauge;
        _readouts = readouts;
        // A hidden 性能 takes its value without motion, so it asks for no frames.
        _score.Apply(readouts.Score, now, animate && tick.Readouts.Intelligence, reduceMotion);
        _thinking.Set(readouts.Thinking, now, animate, reduceMotion);
        var tag = readouts.Cost.State == BoardMoneyState.Tag;
        _cost.Set(tag ? string.Empty : readouts.Cost.Number, now, animate && !tag && !_tag, reduceMotion);
        if (tag != _tag)
        {
            _tag = tag;
            _tagStart = animate ? now : double.NegativeInfinity;
        }

        _fold.Set(tick.ThinkingHidden && tick.Readouts.Thinking, now, animate && !reduceMotion);
    }

    public void Settle()
    {
        _score.Settle();
        _thinking.Settle();
        _cost.Settle();
        _fold.Settle();
        _tagStart = double.NegativeInfinity;
    }

    /// <summary>
    /// 性能's count or crossfade, a roll, the tag's crossfade or the fold runs (60 fps; a count at most 0.8 s, the rest
    /// within the hero roll's frames).
    /// </summary>
    public bool Animating(double now) =>
        _score.Animating(now) || _thinking.Animating(now) || _cost.Animating(now) || _fold.Moving(now) ||
        now - _tagStart < BoardReadoutNumber.FadeSeconds;

    /// <summary>The cells measured from the layers (labels, <c>≥</c>, units) and the number glyphs, the wider value held 6 s.</summary>
    public BoardGaugeCells Cells(BoardStyle style, BoardSize size, BoardReadouts readouts, IBoardLayers layers, double now)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var spec = BoardGaugeSpec.For(style, size);
        var timing = style == BoardStyle.Timing;
        var label = Math.Max(readouts.Thinking ? Text(layers, BoardGaugePart.LabelThinking) : 0, readouts.Cost ? Text(layers, BoardGaugePart.LabelCost) : 0);
        if (timing && readouts.Intelligence)
        {
            label = Math.Max(label, Text(layers, BoardGaugePart.LabelIntelligence));
        }

        var unit = Math.Max(readouts.Thinking ? Text(layers, BoardGaugePart.UnitThinking) : 0, readouts.Cost ? Text(layers, BoardGaugePart.UnitCost) : 0);
        var tracking = TableTracking(style, spec);
        var sample = BoardValueGlyphs.Measure(layers, NumberSample, BoardValueSet.GaugeTable, tracking);
        var need = Math.Max(readouts.Thinking ? BoardValueGlyphs.Measure(layers, _thinking.Text, BoardValueSet.GaugeTable, tracking) : 0,
            readouts.Cost ? _tag ? Text(layers, BoardGaugePart.Tag) : BoardValueGlyphs.Measure(layers, _cost.Text, BoardValueSet.GaugeTable, tracking) : 0);
        if (need > Math.Max(sample, _wide) + 0.25)
        {
            _wide = need;
            _wideAt = now;
        }
        else if (need > sample + 0.25)
        {
            _wideAt = now;
        }
        else if (_wide > 0 && now - _wideAt >= WideHoldSeconds)
        {
            _wide = 0;
        }

        var number = Math.Max(sample, _wide);
        Extra = Math.Max(0, number - sample);
        return new BoardGaugeCells(label, Text(layers, BoardGaugePart.AtLeast), number, unit);
    }

    /// <summary>
    /// Draws the sub-gauge in a hero whose top-left corner is (<paramref name="x"/>, <paramref name="y"/>) in canvas points, at
    /// <paramref name="alpha"/> (a switch change's crossfade).
    /// </summary>
    public void Emit(BoardDrawList list, IBoardLayers layers, BoardStyle style, BoardSize size, BoardReadouts readouts, BoardHeroLayout hero,
        BoardGaugePalette palette, double x, double y, double alpha, double now)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentNullException.ThrowIfNull(palette);
        _scoreArea = null;
        if (!readouts.Hero || alpha <= 0.002)
        {
            return;
        }

        var cells = Cells(style, size, readouts, layers, now);
        var p = BoardGaugeLayout.Place(style, size, readouts, cells, hero.Width, hero.Height, _fold.Glide(now), _fold.Alpha(now));
        var spec = BoardGaugeSpec.For(style, size);
        switch (style)
        {
            case BoardStyle.Timing:
                Timing(list, layers, readouts, spec, p, palette, x, y, alpha, now);
                break;
            default:
                Table(list, layers, style, readouts, spec, p, palette, x, y, alpha, now);
                break;
        }
    }

    // Glass and neon: the dial or the digits and meter, then the table rows.
    private void Table(BoardDrawList list, IBoardLayers layers, BoardStyle style, BoardReadouts readouts, BoardGaugeSpec spec, in BoardGaugePlacement p,
        BoardGaugePalette palette, double x, double y, double alpha, double now)
    {
        var cx = x + p.CenterX;
        var neon = style == BoardStyle.Neon;
        if (readouts.Intelligence)
        {
            var (live, proof) = _score.Look(now);
            if (!neon)
            {
                BoardScoreDraw.Dial(list, spec, cx, y + p.DialCenterY, palette, _score.Sweep(now), live, proof, alpha);
            }

            Place(list, layers, BoardGaugePart.LabelIntelligence, cx, 0, y + p.MainLabelBaseline, palette.Quiet, alpha);
            var mainTracking = (neon ? -0.03 : -0.02) * spec.MainSize;
            if (neon)
            {
                if (palette.Glows || palette.Ghost.A > 0)
                {
                    Ghost(list, layers, cx, y + p.MainBaseline, mainTracking, palette.Ghost, alpha, now);
                }

                EmitScore(list, layers, style, palette, cx, 0, y + p.MainBaseline, spec.MainSize, mainTracking, alpha, now);
                BoardScoreDraw.Meter(list, spec, cx, y + p.MeterTop, palette, _score.Meter(now), live, proof, alpha);
            }
            else
            {
                EmitScore(list, layers, style, palette, cx, 0, y + p.MainBaseline, spec.MainSize, mainTracking, alpha, now);
            }

            // Glass: the dial box; neon: the digits and the meter (the label's top to the meter's bottom).
            _scoreArea = RectBounds.From(cx - spec.Dial / 2, y + p.Top, spec.Dial, spec.MainHeight);
        }

        var tracking = TableTracking(style, spec);
        if (readouts.Thinking)
        {
            var a = alpha * p.ThinkingAlpha;
            var thinkingQuiet = _readouts.Quiet || _readouts.ThinkingZero;
            Place(list, layers, BoardGaugePart.LabelThinking, x + p.TableLeft, -1, y + p.ThinkingBaseline, palette.Quiet, a);
            TableNumber(list, layers, _thinking, neon, palette, thinkingQuiet, x + p.NumberRight, y + p.ThinkingBaseline, spec.TableSize, tracking, a, now);
            Place(list, layers, BoardGaugePart.UnitThinking, x + p.UnitLeft, -1, y + p.ThinkingBaseline, palette.Quiet, a);
        }

        if (readouts.Cost)
        {
            var money = _readouts.Cost;
            var baseline = y + p.CostBaseline;
            Place(list, layers, BoardGaugePart.LabelCost, x + p.TableLeft, -1, baseline, palette.Quiet, alpha);
            if (money.AtLeast)
            {
                Place(list, layers, BoardGaugePart.AtLeast, x + p.AtLeastLeft, -1, baseline, palette.Quiet, alpha);
            }

            Cost(list, layers, neon, palette, x + p.NumberRight, baseline, spec.TableSize, tracking, alpha, now);
            if (money.HasUnit)
            {
                Place(list, layers, BoardGaugePart.UnitCost, x + p.UnitLeft, -1, baseline, palette.Quiet, alpha);
            }
        }
    }

    // Timing's data box: the slab, the tab column, the separators and the inner stroke as shapes; tab labels centred in their
    // rows; every value on one right edge, units and ≥ on the number's baseline.
    private void Timing(BoardDrawList list, IBoardLayers layers, BoardReadouts readouts, BoardGaugeSpec spec, in BoardGaugePlacement p,
        BoardGaugePalette palette, double x, double y, double alpha, double now)
    {
        var left = x + p.Left;
        var top = y + p.Top;
        var right = left + p.Width;
        if (palette.Box.A > 0)
        {
            list.Add(OrbGpuInstance.RoundedRect(left + p.Width / 2, top + p.Height / 2, p.Width / 2, p.Height / 2, 0, 5, palette.Box.MultiplyAlpha(alpha)));
        }

        if (palette.Tab.A > 0 && p.TabWidth > 0)
        {
            var tab = palette.Tab.MultiplyAlpha(alpha);
            list.Add(OrbGpuInstance.RoundedRect(left + p.TabWidth / 2, top + p.Height / 2, p.TabWidth / 2, p.Height / 2, 0, Math.Min(5, p.TabWidth / 2), tab));
            var square = Math.Min(5, p.TabWidth / 2);
            list.Add(OrbGpuInstance.RoundedRect(left + p.TabWidth - square / 2, top + p.Height / 2, square / 2, p.Height / 2, 0, 0, tab));
        }

        var tracking = -0.03 * spec.TableSize;
        var tabX = left + p.TabWidth / 2;
        if (readouts.Intelligence)
        {
            Place(list, layers, BoardGaugePart.LabelIntelligence, tabX, 0, top + p.MainRow / 2, palette.TabMainInk, alpha, centred: true);
            EmitScore(list, layers, BoardStyle.Timing, palette, right - spec.RightPad - spec.UnitGap, 1, y + p.MainBaseline, spec.MainSize,
                -0.04 * spec.MainSize, alpha, now);
            _scoreArea = RectBounds.From(left, top, p.Width, p.MainRow);
        }

        if (readouts.Thinking)
        {
            var a = alpha * p.ThinkingAlpha;
            var rowTop = y + p.ThinkingTop;
            var separator = Math.Abs(p.ThinkingTop - p.Top) > 0.01;
            if (separator)
            {
                Separator(list, left, right, rowTop, palette.BoxLine, a);
            }

            var sep = separator ? 1 : 0;
            Place(list, layers, BoardGaugePart.LabelThinking, tabX, 0, rowTop + sep + (p.SmallRow - sep) / 2, palette.TabInk, a, centred: true);
            var quiet = _readouts.Quiet || _readouts.ThinkingZero;
            _thinking.Emit(list, layers, BoardValueSet.GaugeTable, x + p.NumberRight, 1, y + p.ThinkingBaseline, spec.TableSize, tracking,
                quiet ? palette.Quiet : palette.Number, 1, a, now);
            Place(list, layers, BoardGaugePart.UnitThinking, x + p.UnitLeft, -1, y + p.ThinkingBaseline, palette.Quiet, a);
        }

        if (readouts.Cost)
        {
            var rowTop = y + p.CostTop;
            var separator = Math.Abs(p.CostTop - p.Top) > 0.01;
            if (separator)
            {
                Separator(list, left, right, rowTop, palette.BoxLine, alpha);
            }

            var sep = separator ? 1 : 0;
            Place(list, layers, BoardGaugePart.LabelCost, tabX, 0, rowTop + sep + (p.SmallRow - sep) / 2, palette.TabInk, alpha, centred: true);
            var baseline = y + p.CostBaseline;
            var width = Cost(list, layers, false, palette, x + p.NumberRight, baseline, spec.TableSize, tracking, alpha, now);
            var money = _readouts.Cost;
            if (money.AtLeast && layers.TryGet(KeyOf(BoardGaugePart.AtLeast), out var ge))
            {
                // ≥ sits right before the number (flex gap 3 and its 1 pt margin).
                Place(list, layers, BoardGaugePart.AtLeast, x + p.NumberRight - width - 4 - (ge.Width - 2 * TextPad), -1, baseline, palette.Quiet, alpha);
            }

            if (money.HasUnit)
            {
                Place(list, layers, BoardGaugePart.UnitCost, x + p.UnitLeft, -1, baseline, palette.Quiet, alpha);
            }
        }

        list.Add(OrbGpuInstance.RoundedRing(left + p.Width / 2, top + p.Height / 2, p.Width / 2 - 0.5, p.Height / 2 - 0.5, 4.5, 1,
            palette.BoxLine.MultiplyAlpha(alpha)));
    }

    // The cost number, or the tag (crossfading with the number).
    private double Cost(BoardDrawList list, IBoardLayers layers, bool neon, BoardGaugePalette palette, double right, double baseline, double size,
        double tracking, double alpha, double now)
    {
        var t = Math.Clamp((now - _tagStart) / BoardReadoutNumber.FadeSeconds, 0, 1);
        var tagAlpha = _tag ? t : 1 - t;
        var width = 0.0;
        if (tagAlpha > 0.002 && layers.TryGet(KeyOf(BoardGaugePart.Tag), out var tag))
        {
            width = tag.Width - 2 * TextPad;
            Place(list, layers, BoardGaugePart.Tag, right, 1, baseline, palette.Quiet, alpha * tagAlpha);
        }

        if (!_tag || tagAlpha < 1)
        {
            var quiet = _readouts.Cost.Quiet;
            width = Math.Max(width, TableNumber(list, layers, _cost, neon, palette, quiet, right, baseline, size, tracking, alpha * (1 - tagAlpha), now));
        }

        return width;
    }

    private static double TableNumber(BoardDrawList list, IBoardLayers layers, BoardReadoutNumber number, bool neon, BoardGaugePalette palette, bool quiet,
        double right, double baseline, double size, double tracking, double alpha, double now)
    {
        var baked = neon && palette.Glows && !quiet;
        var set = neon && !baked ? BoardValueSet.GaugeTablePlain : BoardValueSet.GaugeTable;
        return number.Emit(list, layers, set, right, 1, baseline, size, tracking, quiet ? palette.Quiet : palette.Number, baked ? 0 : 1, alpha, now);
    }

    // 性能's number: the shown integer (a count's), the outgoing one while a crossfade runs, each in its live or resting color
    // (neon live: the baked LED digits with their glow), with the dotted underline of an provisional score.
    private void EmitScore(BoardDrawList list, IBoardLayers layers, BoardStyle style, BoardGaugePalette palette, double x, int align, double baseline,
        double size, double tracking, double alpha, double now)
    {
        var (outgoing, incoming) = _score.Fade(now);
        var has = _score.HasScore;
        var compact = style == BoardStyle.Glass;
        Span<char> text = stackalloc char[BoardHeroScore.MaxLength];
        var length = _score.Format(text, now, compact);
        ScoreText(list, layers, style, palette, text[..length], has ? _score.Liveness(now) : 0, has ? _score.Proof(now) : 0, x, align,
            baseline, size, tracking, alpha * incoming);
        length = _score.FormatOutgoing(text, now, compact);
        if (length > 0)
        {
            var old = text[..length];
            var number = !IsDash(old);
            ScoreText(list, layers, style, palette, old, number && _score.OutgoingLive ? 1 : 0, number && _score.OutgoingProvisional ? 1 : 0, x, align,
                baseline, size, tracking, alpha * outgoing);
        }
    }

    private static bool IsDash(ReadOnlySpan<char> text) => text.SequenceEqual(BoardHeroReadouts.Dash);

    // A plain loop: MemoryExtensions.ContainsAnyInRange allocates on every call here, and frames must allocate nothing.
    private static bool HasDigit(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    private static void ScoreText(BoardDrawList list, IBoardLayers layers, BoardStyle style, BoardGaugePalette palette, ReadOnlySpan<char> text, double live,
        double proof, double x, int align, double baseline, double size, double tracking, double alpha)
    {
        if (alpha <= 0.002)
        {
            return;
        }

        var color = Rgba.Lerp(palette.Quiet, palette.Number, live);
        var plain = style == BoardStyle.Neon ? BoardValueSet.GaugeMainPlain : BoardValueSet.GaugeMain;
        double width;
        if (style == BoardStyle.Neon && palette.Glows)
        {
            width = BoardValueGlyphs.EmitOnBaseline(list, layers, text, BoardValueSet.GaugeMain, x, align, baseline, tracking, palette.Number, 0,
                alpha * live);
            BoardValueGlyphs.EmitOnBaseline(list, layers, text, plain, x, align, baseline, tracking, palette.Quiet, 1, alpha * (1 - live));
        }
        else
        {
            width = BoardValueGlyphs.EmitOnBaseline(list, layers, text, plain, x, align, baseline, tracking, color, 1, alpha);
        }

        if (proof > 0.002 && width > 0 && !IsDash(text))
        {
            var left = align < 0 ? x : align == 0 ? x - width / 2 : x - width;
            // Windows high contrast: the dots in the text color, whatever the number's form.
            BoardScoreDraw.Underline(list, left, width, baseline, size, palette.HighContrast ? palette.Number : color, alpha * proof,
                palette.HighContrast);
        }
    }

    // Neon's ghost segments behind the number: its text with every digit as 8 and the comma kept (8,888 behind 1,286).
    private void Ghost(BoardDrawList list, IBoardLayers layers, double cx, double baseline, double tracking, Rgba ghost, double alpha, double now)
    {
        Span<char> eights = stackalloc char[BoardHeroScore.MaxLength];
        eights = eights[.._score.Format(eights, now, compact: false)];
        if (ghost.A <= 0 || !HasDigit(eights))
        {
            return;
        }

        for (var i = 0; i < eights.Length; i++)
        {
            eights[i] = char.IsAsciiDigit(eights[i]) ? '8' : eights[i];
        }

        BoardValueGlyphs.EmitOnBaseline(list, layers, eights, BoardValueSet.GaugeMainPlain, cx, 0, baseline, tracking, ghost.WithAlpha(1), 1,
            alpha * ghost.A);
    }

    private static void Separator(BoardDrawList list, double left, double right, double top, Rgba color, double alpha) =>
        list.Add(OrbGpuInstance.RoundedRect((left + right) / 2, top + 0.5, (right - left) / 2, 0.5, 0, 0, color.MultiplyAlpha(alpha)));

    // A text layer anchored at x (align −1 left, 0 centre, 1 right) with its baseline at y (or, centred, its middle at y), tinted.
    private static void Place(BoardDrawList list, IBoardLayers layers, BoardGaugePart part, double x, int align, double y, Rgba tint, double alpha,
        bool centred = false)
    {
        if (alpha <= 0.002 || !layers.TryGet(KeyOf(part), out var slot))
        {
            return;
        }

        var width = slot.Width - 2 * TextPad;
        var left = align < 0 ? x : align == 0 ? x - width / 2 : x - width;
        var top = centred ? y - slot.Height / 2 : y - (double.IsFinite(slot.Baseline) ? slot.Baseline : slot.Height / 2);
        list.AddLayer(BoardLayerInstance.Create(slot, left - TextPad, top, alpha * tint.A, tint.WithAlpha(1), 1));
    }

    private static double Text(IBoardLayers layers, BoardGaugePart part) =>
        layers.TryGet(KeyOf(part), out var slot) ? Math.Max(0, slot.Width - 2 * TextPad) : 0;

    /// <summary>The table numbers' letter spacing (glass none, timing −0.03 em, neon −0.02 em).</summary>
    public static double TableTracking(BoardStyle style, BoardGaugeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return style switch
        {
            BoardStyle.Timing => -0.03 * spec.TableSize,
            BoardStyle.Neon => -0.02 * spec.TableSize,
            _ => 0,
        };
    }
}
