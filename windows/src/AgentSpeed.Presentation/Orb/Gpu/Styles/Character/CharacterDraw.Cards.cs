using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

public sealed partial class CharacterDraw
{
    /// <summary>Sizes of the speed readout for a disc of D points (DESIGN 2a.6), with floors so they stay legible on small orbs.</summary>
    private readonly record struct ReadoutMetrics(double D)
    {
        public double Value => Math.Max(11, 0.068 * D);

        public double Unit => Math.Max(7.5, 0.022 * D);

        public double Title => Math.Max(7.5, 0.021 * D);

        public double Peak => Math.Max(8, 0.022 * D);

        /// <summary>Points per reference unit of the speed body (the reference draws its digits 92 units tall).</summary>
        public double Q => Value / 92;
    }

    /// <summary>Sizes of the cards (DESIGN 2a.6, Readouts): row text t, headline numbers p, padding and row gap.</summary>
    private readonly record struct CardMetrics(double D)
    {
        public double T => Math.Max(8, 0.027 * D);

        public double P => 1.25 * T;

        public double PadX => 0.8 * T;

        public double PadY => 0.55 * T;

        public double RowGap => 0.36 * T;

        public double GlyphGap => 0.24 * T;

        public double CellGap => OrbLinesPlacement.Gap * T;

        /// <summary>A row's height, and its baseline below the row's top.</summary>
        public static double RowHeight(double size) => 0.92 * size;

        public static double Baseline(double size) => 0.82 * size;

        public static double Small(double size) => OrbLinesPlacement.UnitScale * size;
    }

    /// <summary>The colors of the cards: frosted glass, a hairline rim, a soft shadow, a sheen, the value and secondary tokens, her accent.</summary>
    private readonly record struct CardInk(Rgba Fill, Rgba Rim, Rgba Shadow, Rgba Sheen, Rgba Number, Rgba Secondary, Rgba Accent)
    {
        public static CardInk Of(OrbGpuContext ctx, Rgba accent)
        {
            if (ctx.HighContrast)
            {
                var c = ctx.Contrast;
                return new CardInk(c.Window, c.WindowText, Rgba.Transparent, Rgba.Transparent, c.WindowText, c.GrayText, c.Highlight);
            }

            var dark = ctx.Dark;
            var solid = ctx.SolidBody;
            var glass = dark ? new Rgba(18 / 255.0, 20 / 255.0, 29 / 255.0) : Rgba.White;
            var ink = dark ? Rgba.White : Rgba.FromHex("#0B0D12");
            return new CardInk(
                glass.WithAlpha(solid ? 0.97 : dark ? 0.64 : 0.8),
                dark ? Rgba.White.WithAlpha(0.15) : Rgba.Black.WithAlpha(0.085),
                Rgba.Black.WithAlpha(dark ? 0.3 : 0.11),
                Rgba.White.WithAlpha(dark ? 0.1 : 0.75),
                ink.WithAlpha(0.9),
                (dark ? Rgba.White : Rgba.Black).WithAlpha(0.55),
                accent);
        }
    }

    /// <summary>A card's local frame: its center, scale, tilt (radians) and opacity; offsets are unscaled and unrotated.</summary>
    private readonly record struct Pen(double Cx, double Cy, double Scale, double Spin, double Alpha)
    {
        public (double X, double Y) Point(double ox, double oy)
        {
            var c = Math.Cos(Spin);
            var s = Math.Sin(Spin);
            var x = ox * Scale;
            var y = oy * Scale;
            return (Cx + x * c - y * s, Cy + x * s + y * c);
        }
    }

    private static readonly HudCard[] DrawOrder =
        [HudCard.Performance, HudCard.Network, HudCard.Cost, HudCard.Agents, HudCard.Quota, HudCard.Models, HudCard.Model];

    private OrbLinesContent? PerformanceContent => Readings?.Performance.Reading?.Content;

    /// <summary>Places the cards for this frame: her outline from the skinned mesh, each card's size and whether it shows, then the wings' layout.</summary>
    private void LayoutCards(double dt, in OrbFrame frame, CharacterPerformer performer, in Geometry g)
    {
        if (_ctx is not { } ctx)
        {
            return;
        }

        var d = ctx.Size;
        var side = ctx.Canvas;
        var step = Math.Max(2, Math.Round(d / 150));
        if (_silhouette is null || _silhouette.Step != step || _silhouette.BandCount != Math.Max(1, (int)Math.Ceiling(side / step)))
        {
            _silhouette = new HudSilhouette(0, side, step);
        }

        var anchors = performer.Trace(_silhouette, (g.FeetX, g.FeetY), g.PointsPerPixel, ref _scratch);
        var time = frame.Time;
        var readouts = Content?.Readouts ?? Settings.Orbs.CharacterReadouts.Default;
        var (sw, sh, drop) = SpeedBox(ctx, frame);
        _speedDrop = drop;
        SyncModels(ctx, frame);
        FitNames(new CardMetrics(d), dt, time, _wings.Still || _wings.Presence[(int)HudCard.Models] == 0);
        SyncQuota(ctx, frame);
        FitQuota(new CardMetrics(d), time, IsSmall(ctx));
        foreach (var card in HudCards.All)
        {
            _cardSizes[(int)card] = card == HudCard.Speed ? (sw, sh) : CardSize(ctx, card, frame);
        }

        bool Has(HudCard card) => _cardSizes[(int)card].W > 0;
        var readings = Readings;
        _cardShown[(int)HudCard.Speed] = true;
        _cardShown[(int)HudCard.Model] = readouts.Model && Has(HudCard.Model);
        _cardShown[(int)HudCard.Performance] = readings?.PerformanceLoud(time) == true && Has(HudCard.Performance);
        _cardShown[(int)HudCard.Network] = readouts.Network && readings?.NetworkLoud(time) == true && Has(HudCard.Network);
        _cardShown[(int)HudCard.Cost] = readouts.Cost && readings?.Cost.IsLoud(time) == true && Has(HudCard.Cost);
        _cardShown[(int)HudCard.Agents] = readouts.Agents && !IsSmall(ctx) && Has(HudCard.Agents);
        _cardShown[(int)HudCard.Models] = readouts.Models && readings?.Models.IsLoud(time) == true && Has(HudCard.Models);
        _cardShown[(int)HudCard.Quota] = readouts.Quota && Content?.Quota.IsEmpty == false && Has(HudCard.Quota);
        if (IsSmall(ctx))
        {
            var room = 2;
            foreach (var card in HudCards.Column)
            {
                if (!_cardShown[(int)card])
                {
                    continue;
                }

                if (room == 0)
                {
                    _cardShown[(int)card] = false;
                }
                else
                {
                    room--;
                }
            }
        }

        _wings.Still = frame.Effects.ReduceMotion || frame.Effects.Level == EffectsIntensity.Off;
        _wings.Balance = !IsSmall(ctx);
        _wings.Update(dt, _hudTime, new HudFrame((0, 0, side, side), _silhouette, d, anchors), _cardSizes, _cardShown);
    }

    /// <summary>The model chip's text: the fastest model, the last one shown, else her own name.</summary>
    private string? ModelName => Content?.ModelName ?? _lastModel ?? Content?.CharacterName;

    // MARK: Text

    private double Advance(string text, OrbFont font, double size)
    {
        if (string.IsNullOrEmpty(text) || Glyphs is not { } glyphs)
        {
            return 0;
        }

        return glyphs.TryGet(OrbTextKey.Create(text, font, size), out var glyph) ? glyph.Advance : 0;
    }

    /// <summary>Digits are measured and drawn character by character, so a changing value never rasterizes a new string.</summary>
    private double DigitsWidth(string text, OrbFont font, double size)
    {
        var width = 0.0;
        foreach (var c in text)
        {
            width += Advance(CharText(c), font, size);
        }

        return width;
    }

    private static readonly string[] Ascii = Enumerable.Range(0, 128).Select(i => ((char)i).ToString()).ToArray();

    private static string CharText(char c) => c < 128 ? Ascii[c] : c.ToString();

    /// <summary>A whole string from local x on a local baseline; returns its advance.</summary>
    private double Put(OrbDrawList list, string text, OrbFont font, double size, in Pen pen, double x, double baseline, Rgba color,
        Rgba? glow = null, double glowSigma = 0, OrbTextClip clip = default)
    {
        if (string.IsNullOrEmpty(text) || Glyphs is not { } glyphs || !glyphs.TryGet(OrbTextKey.Create(text, font, size, 0, glowSigma), out var glyph))
        {
            return 0;
        }

        list.AddInlineText(glyph, pen.Cx + x, pen.Cy + baseline, color.MultiplyAlpha(pen.Alpha), glow?.MultiplyAlpha(pen.Alpha), clip, pen.Scale, pen.Spin,
            pen.Cx, pen.Cy);
        return glyph.Advance;
    }

    private double PutDigits(OrbDrawList list, string text, OrbFont font, double size, in Pen pen, double x, double baseline, Rgba color)
    {
        var dx = 0.0;
        foreach (var c in text)
        {
            dx += Put(list, CharText(c), font, size, pen, x + dx, baseline, color);
        }

        return dx;
    }

    // MARK: Sizes

    private (double W, double H) Plate(double width, double height, in CardMetrics m) => (width + 2 * m.PadX, height + 2 * m.PadY);

    private (double W, double H) CardSize(OrbGpuContext ctx, HudCard card, in OrbFrame frame)
    {
        var m = new CardMetrics(ctx.Size);
        switch (card)
        {
            case HudCard.Model:
                if (ModelName is not { } name)
                {
                    return (0, 0);
                }

                return (Advance(name, OrbFont.Title, m.T) + 0.62 * m.T + 0.42 * m.T + 2 * 0.85 * m.T, CardMetrics.RowHeight(m.T) + 2 * 0.5 * m.T);
            case HudCard.Performance:
                return PerformanceRows(frame, m) is { } rows
                    ? Plate(Math.Max(rows.ScoreWidth, Math.Max(rows.ThinkingShare * rows.ThinkingWidth, rows.LatencyShare * rows.LatencyWidth)), rows.Height, m)
                    : (0, 0);
            case HudCard.Models:
                return ModelsSize(m, frame.Time);
            case HudCard.Quota:
                return QuotaSize(m, frame.Time);
            case HudCard.Network:
                if (Readings?.Network.Reading is not { Content: { Kind: OrbLinesKind.Traffic } traffic })
                {
                    return (0, 0);
                }

                var (number, unit) = TrafficCells(m.T, traffic.Bits);
                var line = OrbLinesPlacement.ArrowWidth * m.T + 2 * m.CellGap + number + unit;
                return Plate(line, 2 * CardMetrics.RowHeight(m.T) + m.RowGap, m);
            case HudCard.Cost:
                return Readings?.Cost.Money is { } money ? Plate(CostWidth(money, m), CardMetrics.RowHeight(m.P), m) : (0, 0);
            case HudCard.Agents:
                var width = 0.0;
                var count = 0;
                if (Content?.TodayLine is { } today)
                {
                    width = Advance(today, OrbFont.Info, m.T);
                    count++;
                }

                if (Content?.AgentsLine is { } agents)
                {
                    width = Math.Max(width, Advance(agents, OrbFont.Info, m.T));
                    count++;
                }

                return count == 0 ? (0, 0) : Plate(width, count * CardMetrics.RowHeight(m.T) + (count - 1) * m.RowGap, m);
            default:
                return (0, 0);
        }
    }

    private (double Number, double Unit) TrafficCells(double t, bool bits)
    {
        var unit = Advance(bits ? "Mb/s" : "MB/s", OrbFont.Unit, CardMetrics.Small(t));
        var number = DigitsWidth("8.88", OrbFont.TrafficNumber, t);
        return (number > 0 ? number : 2.2 * t, unit > 0 ? unit : 2.2 * t);
    }

    private double CostWidth(LiveMoney money, in CardMetrics m)
    {
        var width = Advance(Content?.CostGlyph ?? string.Empty, OrbFont.Title, CardMetrics.Small(m.P)) + m.GlyphGap * 1.25
            + DigitsWidth(money.Number, OrbFont.TrafficNumber, m.P);
        if (money.AtLeast)
        {
            width += Advance("≥", OrbFont.Unit, CardMetrics.Small(m.P)) + 0.08 * m.P;
        }

        if (!money.Unknown)
        {
            width += m.CellGap + Advance(money.Unit, OrbFont.Unit, CardMetrics.Small(m.T));
        }

        return width;
    }

    private sealed record PerformanceLayout(OrbScoreFrame? Score, string ScoreText, string? Outgoing, double ScoreWidth, OrbLine? Thinking,
        double ThinkingWidth, double ThinkingAlpha, double ThinkingShare, double Height)
    {
        /// <summary>The 首字 row (DESIGN 2a.6): its line, width, opacity and share of its room as it fades in or out.</summary>
        public OrbLine? Latency { get; init; }

        public double LatencyWidth { get; init; }

        public double LatencyAlpha { get; init; }

        public double LatencyShare { get; init; }
    }

    private PerformanceLayout? PerformanceRows(in OrbFrame frame, in CardMetrics m)
    {
        if (Readings?.Performance.Reading is not { Content: { Kind: OrbLinesKind.Intelligence } content } reading)
        {
            return null;
        }

        var readouts = Content?.Readouts ?? Settings.Orbs.CharacterReadouts.Default;
        var reduce = frame.Effects.ReduceMotion;
        OrbScoreFrame? score = null;
        var scoreText = string.Empty;
        string? outgoing = null;
        var scoreWidth = 0.0;
        if (readouts.Intelligence)
        {
            var s = reading.Score.Frame(frame.Time, reduce);
            score = s;
            scoreText = OrbScoreMotion.Text(s.Shown);
            outgoing = s.Outgoing is { } o ? OrbScoreMotion.Text(o) : null;
            scoreWidth = Advance(content.First.Glyph, OrbFont.Title, CardMetrics.Small(m.P)) + m.GlyphGap * 1.25
                + Math.Max(DigitsWidth(scoreText, OrbFont.TrafficNumber, m.P), outgoing is null ? 0 : DigitsWidth(outgoing, OrbFont.TrafficNumber, m.P));
        }

        OrbLine? thinking = null;
        double thinkingWidth = 0, thinkingAlpha = 1, thinkingShare = 1;
        if (readouts.Thinking)
        {
            thinking = content.Second;
            thinkingWidth = Advance(content.Second.Glyph, OrbFont.Title, CardMetrics.Small(m.T)) + m.GlyphGap
                + DigitsWidth(content.Second.Number, OrbFont.TrafficNumber, m.T) + m.CellGap + Advance(content.Second.Unit, OrbFont.Unit, CardMetrics.Small(m.T));
            if (score is not null)
            {
                thinkingAlpha = reading.Thinking.Alpha(frame.Time, reduce);
                thinkingShare = 1 - Easing.SmoothStep(0, 1, reading.Thinking.Single(frame.Time, reduce));
            }
        }

        OrbLine? latency = null;
        double latencyWidth = 0, latencyAlpha = 0, latencyShare = 0;
        var fade = Readings?.Latency ?? OrbLatencyFade.None;
        if (readouts.Latency && fade.Latency is { } shown && !fade.IsGone(frame.Time))
        {
            latency = shown.Line;
            latencyWidth = Advance(shown.Line.Glyph, OrbFont.Title, CardMetrics.Small(m.T)) + m.GlyphGap
                + DigitsWidth(shown.Line.Number, OrbFont.TrafficNumber, m.T) + m.CellGap + Advance(shown.Line.Unit, OrbFont.Unit, CardMetrics.Small(m.T));
            latencyAlpha = fade.Alpha(frame.Time);
            latencyShare = fade.Share(frame.Time, reduce);
        }

        if (score is null && thinking is null && latency is null)
        {
            return null;
        }

        var height = 0.0;
        if (score is not null)
        {
            height += CardMetrics.RowHeight(m.P);
        }

        if (thinking is not null)
        {
            height += thinkingShare * ((score is not null ? m.RowGap : 0) + CardMetrics.RowHeight(m.T));
        }

        if (latency is not null)
        {
            height += latencyShare * ((score is not null || thinking is not null ? m.RowGap : 0) + CardMetrics.RowHeight(m.T));
        }

        return new PerformanceLayout(score, scoreText, outgoing, scoreWidth, thinking, thinkingWidth, thinkingAlpha, thinkingShare, height)
        {
            Latency = latency,
            LatencyWidth = latencyWidth,
            LatencyAlpha = latencyAlpha,
            LatencyShare = latencyShare,
        };
    }

    // MARK: Drawing

    /// <summary>The column, the model chip, then the speed over everything.</summary>
    private void DrawCards(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame, OrbScene scene)
    {
        var accent = Content?.Accent ?? Rgba.FromHex("#D97757");
        var ink = CardInk.Of(ctx, accent);
        var m = new CardMetrics(ctx.Size);
        foreach (var card in DrawOrder)
        {
            var place = _wings.Places[(int)card];
            var size = _wings.Sizes[(int)card];
            if (place.Alpha <= 0.01 || size.W <= 0 || size.H <= 0)
            {
                continue;
            }

            var pen = new Pen(place.X, place.Y, place.Scale, place.Spin, place.Alpha);
            DrawCard(list, ctx, card, pen, size, m, ink, _wings.IsAtHerRight(card), frame);
        }

        var speed = _wings.Places[(int)HudCard.Speed];
        var dropped = _speedDrop * speed.Scale;
        var center = (X: speed.X + dropped * Math.Sin(speed.Spin), Y: speed.Y - dropped * Math.Cos(speed.Spin));
        DrawSpeed(list, ctx, frame, scene, new Pen(center.X, center.Y, speed.Scale, speed.Spin, speed.Alpha));
    }

    private void DrawCard(OrbDrawList list, OrbGpuContext ctx, HudCard card, in Pen pen, (double W, double H) box, in CardMetrics m, in CardInk ink,
        bool right, in OrbFrame frame)
    {
        var left = -box.W / 2 + m.PadX;
        var top = -box.H / 2 + m.PadY;
        switch (card)
        {
            case HudCard.Model when ModelName is { } name:
            {
                DrawPlate(list, pen, box, ink, m, box.H / 2, null);
                var x = -box.W / 2 + 0.85 * m.T;
                var dot = 0.62 * m.T;
                var baseline = -CardMetrics.RowHeight(m.T) / 2 + CardMetrics.Baseline(m.T);
                var (dx, dy) = pen.Point(x + dot / 2, baseline - 0.36 * m.T);
                list.Add(OrbGpuInstance.Circle(dx, dy, dot / 2 * pen.Scale, ink.Accent.MultiplyAlpha(pen.Alpha)));
                Put(list, name, OrbFont.Title, m.T, pen, x + dot + 0.42 * m.T, baseline, ink.Number);
                break;
            }

            case HudCard.Performance when PerformanceRows(frame, m) is { } rows && PerformanceContent is { } content:
            {
                DrawPlate(list, pen, box, ink, m, null, right ? -1 : 1);
                var y = top;
                if (rows.Score is { } score)
                {
                    var baseline = y + CardMetrics.Baseline(m.P);
                    var x = left + Put(list, content.First.Glyph, OrbFont.Title, CardMetrics.Small(m.P), pen, left, baseline, ink.Secondary) + m.GlyphGap * 1.25;
                    var color = Rgba.Lerp(ink.Number, ink.Secondary, score.Pale);
                    if (rows.Outgoing is { } outgoing)
                    {
                        PutDigits(list, outgoing, OrbFont.TrafficNumber, m.P, pen, x, baseline, color.MultiplyAlpha(score.OutgoingAlpha));
                    }

                    PutDigits(list, rows.ScoreText, OrbFont.TrafficNumber, m.P, pen, x, baseline, color.MultiplyAlpha(score.Alpha));
                    y += CardMetrics.RowHeight(m.P) + m.RowGap;
                }

                if (rows.Thinking is { } thinking)
                {
                    var share = rows.Score is not null ? rows.ThinkingShare : 1;
                    if (rows.ThinkingAlpha > 0.01)
                    {
                        DrawRow(list, thinking, pen, left, y - (1 - share) * (CardMetrics.RowHeight(m.T) + m.RowGap), rows.ThinkingAlpha, m, ink);
                    }

                    y += share * (CardMetrics.RowHeight(m.T) + m.RowGap);
                }

                if (rows.Latency is { } latency && rows.LatencyAlpha > 0.01)
                {
                    DrawRow(list, latency, pen, left, y, rows.LatencyAlpha, m, ink);
                }

                break;
            }

            case HudCard.Network when Readings?.Network.Reading is { Content: { Kind: OrbLinesKind.Traffic } traffic }:
            {
                DrawPlate(list, pen, box, ink, m, null, right ? -1 : 1);
                var cells = TrafficCells(m.T, traffic.Bits);
                DrawTraffic(list, traffic.First, pen, left, top, cells, m, ink);
                DrawTraffic(list, traffic.Second, pen, left, top + CardMetrics.RowHeight(m.T) + m.RowGap, cells, m, ink);
                break;
            }

            case HudCard.Cost when Readings?.Cost.Money is { } money:
            {
                DrawPlate(list, pen, box, ink, m, null, right ? -1 : 1);
                var baseline = top + CardMetrics.Baseline(m.P);
                var x = left + Put(list, Content?.CostGlyph ?? string.Empty, OrbFont.Title, CardMetrics.Small(m.P), pen, left, baseline, ink.Secondary)
                    + m.GlyphGap * 1.25;
                if (money.AtLeast)
                {
                    x += Put(list, "≥", OrbFont.Unit, CardMetrics.Small(m.P), pen, x, baseline, ink.Secondary) + 0.08 * m.P;
                }

                x += PutDigits(list, money.Number, OrbFont.TrafficNumber, m.P, pen, x, baseline, money.Unknown ? ink.Secondary : ink.Number);
                if (!money.Unknown)
                {
                    Put(list, money.Unit, OrbFont.Unit, CardMetrics.Small(m.T), pen, x + m.CellGap, baseline, ink.Secondary);
                }

                break;
            }

            case HudCard.Models:
                DrawModels(list, pen, box, m, ink, right, frame.Time, ctx.HighContrast);
                break;

            case HudCard.Quota:
                DrawQuota(list, ctx, pen, box, m, ink, right, frame.Time);
                break;

            case HudCard.Agents:
            {
                DrawPlate(list, pen, box, ink, m, null, right ? -1 : 1);
                var y = top;
                if (Content?.TodayLine is { } today)
                {
                    Put(list, today, OrbFont.Info, m.T, pen, left, y + CardMetrics.Baseline(m.T), ink.Number);
                    y += CardMetrics.RowHeight(m.T) + m.RowGap;
                }

                if (Content?.AgentsLine is { } agents)
                {
                    Put(list, agents, OrbFont.Info, m.T, pen, left, y + CardMetrics.Baseline(m.T), ink.Secondary);
                }

                break;
            }
        }
    }

    /// <summary>A row at <c>t</c> of the 性能 card (思考, 首字): the label and unit in the secondary token, the number in the value token.</summary>
    private void DrawRow(OrbDrawList list, in OrbLine line, in Pen pen, double left, double top, double alpha, in CardMetrics m, in CardInk ink)
    {
        var baseline = top + CardMetrics.Baseline(m.T);
        var x = left + Put(list, line.Glyph, OrbFont.Title, CardMetrics.Small(m.T), pen, left, baseline, ink.Secondary.MultiplyAlpha(alpha)) + m.GlyphGap;
        x += PutDigits(list, line.Number, OrbFont.TrafficNumber, m.T, pen, x, baseline, ink.Number.MultiplyAlpha(alpha)) + m.CellGap;
        Put(list, line.Unit, OrbFont.Unit, CardMetrics.Small(m.T), pen, x, baseline, ink.Secondary.MultiplyAlpha(alpha));
    }

    /// <summary>One traffic row: the arrow, the number right-aligned in the 8.88 cell, the unit left-aligned in its cell.</summary>
    private void DrawTraffic(OrbDrawList list, in OrbLine line, in Pen pen, double left, double top, (double Number, double Unit) cells, in CardMetrics m,
        in CardInk ink)
    {
        var baseline = top + CardMetrics.Baseline(m.T);
        var box = OrbLinesPlacement.ArrowWidth * m.T;
        if (Glyphs is { } glyphs && glyphs.TryGet(OrbTextKey.Create(line.Glyph, OrbFont.Arrow, m.T), out var arrow))
        {
            list.AddInlineText(arrow, pen.Cx + left + (box - arrow.Advance) / 2, pen.Cy + baseline, ink.Secondary.MultiplyAlpha(pen.Alpha), null,
                OrbTextClip.None, pen.Scale, pen.Spin, pen.Cx, pen.Cy);
        }

        var numberLeft = left + box + m.CellGap;
        PutDigits(list, line.Number, OrbFont.TrafficNumber, m.T, pen, numberLeft + cells.Number - DigitsWidth(line.Number, OrbFont.TrafficNumber, m.T), baseline,
            ink.Number);
        Put(list, line.Unit, OrbFont.Unit, CardMetrics.Small(m.T), pen, numberLeft + cells.Number + m.CellGap, baseline, ink.Secondary);
    }

    /// <summary>The plate: a soft shadow, the hairline rim, the frosted fill, a sheen along the top and her accent on the edge facing her.</summary>
    private static void DrawPlate(OrbDrawList list, in Pen pen, (double W, double H) box, in CardInk ink, in CardMetrics m, double? corner, double? spine)
    {
        var s = pen.Scale;
        var a = pen.Alpha;
        var degrees = pen.Spin * 180 / Math.PI;
        var halfW = box.W / 2 * s;
        var halfH = box.H / 2 * s;
        var r = Math.Min(corner ?? 0.72 * m.T, box.H / 2) * s;
        var (sx, sy) = pen.Point(0, 0.2 * m.T);
        if (ink.Shadow.A > 0)
        {
            list.Add(OrbGpuInstance.RoundedRect(sx, sy, halfW, halfH, degrees, r, ink.Shadow.MultiplyAlpha(a), core: 0, glow: 0.9 * m.T * s, glowAlpha: 1));
        }

        list.Add(OrbGpuInstance.RoundedRect(pen.Cx, pen.Cy, halfW + 0.6, halfH + 0.6, degrees, r + 0.6, ink.Rim.MultiplyAlpha(a)));
        list.Add(OrbGpuInstance.RoundedRect(pen.Cx, pen.Cy, halfW, halfH, degrees, r, ink.Fill.MultiplyAlpha(a)));
        var inset = r / s + 0.15 * m.T;
        if (box.W / 2 > inset && ink.Sheen.A > 0)
        {
            var y = -box.H / 2 + 0.75;
            var (x0, y0) = pen.Point(-box.W / 2 + inset, y);
            var (x1, y1) = pen.Point(box.W / 2 - inset, y);
            list.Add(OrbGpuInstance.Capsule(x0, y0, x1, y1, 0.9 * s, ink.Sheen.MultiplyAlpha(a)));
        }

        if (spine is not { } edge)
        {
            return;
        }

        var sxp = edge * (box.W / 2 - 0.3 * m.T);
        var h = 0.3 * box.H;
        var (ax, ay) = pen.Point(sxp, -h);
        var (bx, by) = pen.Point(sxp, h);
        list.Add(OrbGpuInstance.Capsule(ax, ay, bx, by, Math.Max(1.2, 0.15 * m.T) * s, ink.Accent.MultiplyAlpha(0.9 * a)));
    }
}
