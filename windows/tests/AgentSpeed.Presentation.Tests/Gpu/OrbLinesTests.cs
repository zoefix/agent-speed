using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Tests.Gpu;

/// <summary>The orb's two-line readout (DESIGN 10, Floating orb readout and Intelligence readout): quiet rule, placement, centering and frames.</summary>
public partial class OrbLinesTests
{
    private const double Redline = 2000;
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);
    private static readonly OrbReadoutStrings Strings = new("tok/s", "NEW PEAK",
        Enum.GetValues<SpeedTitle>().Select(t => t.ToString().ToUpperInvariant()).ToArray(), true);

    private static OrbGpuOrb Create(OrbStyle style, double size = 148, bool reduceMotion = false)
    {
        var effects = new EffectsPolicy(EffectsIntensity.Standard, reduceMotion, false);
        var orb = new OrbGpuOrb(3, new FakeGlyphs());
        orb.Configure(new OrbGpuConfig(style, size, effects, true, true, Redline, Strings, null, SourceSet.Empty),
            new OrbGpuContext(size, 2, OrbTokens.Dark, effects));
        orb.SetData(0, 0, SpeedTier.Idle, SpeedTitle.Idle, [], "today", null);
        return orb;
    }

    private static OrbLinesSample Traffic(double rx, double tx, bool bits = false) => OrbLinesText.Traffic(new TrafficRate(rx, tx), bits, En);

    /// <summary>133 over 176 tok/s.</summary>
    private static readonly IntelligenceSums Long = TestSums.Of(1200, 132.8, 528, 1200);

    /// <summary>69 over 59 tok/s.</summary>
    private static readonly IntelligenceSums Short = TestSums.Of(400, 68.85, 177, 400);

    private static OrbLinesSample Intelligence(IntelligenceSums sums, Localizer? localizer = null) =>
        OrbLinesText.Intelligence(PerfSetReading.Of(sums, []), sums, generating: true, localizer ?? En);

    /// <param name="Pieces">Quads in the line: glyph, each character and the unit.</param>
    /// <param name="Snapped">Every piece starts on a device pixel (2 px per point).</param>
    private readonly record struct DrawnLine(double Left, double Right, double Baseline, double Size, int Pieces = 1, bool Snapped = true);

    /// <summary>
    /// The readout's own quads at <paramref name="size"/> (no stack shift), in lines from the top. FakeGlyphs pads each quad
    /// by 2 pt around the advance and puts the baseline 0.75 × size below the text's top.
    /// </summary>
    private static DrawnLine[] Drawn(OrbGpuOrb orb, OrbStyle style, double size)
    {
        var effects = new EffectsPolicy(EffectsIntensity.Standard, false, false);
        var ctx = new OrbGpuContext(size, 2, OrbTokens.Dark, effects);
        var list = new OrbDrawList();
        orb.Lines.Add(list, ctx, orb.Scene, orb.LinesReading!.Value, OrbLinesPlacement.For(style), 0, OrbLinesColors.Standard(ctx),
            OrbTextClip.None, yieldsToPill: false);
        var quads = list.Texts.ToArray()
            .Select(q => new DrawnLine(q.Rect.X + 2, q.Rect.X + q.Rect.Z - 2, q.Rect.Y + 2 + 0.75 * (q.Rect.W - 4), q.Rect.W - 4,
                Snapped: Math.Abs(q.Rect.X * 2 - Math.Round(q.Rect.X * 2)) < 1e-3))
            .OrderBy(q => q.Baseline)
            .ToArray();
        var lines = new List<List<DrawnLine>>();
        foreach (var quad in quads)
        {
            if (lines.Count == 0 || quad.Baseline - lines[^1][0].Baseline > 1)
            {
                lines.Add([]);
            }

            lines[^1].Add(quad);
        }

        return lines.Select(l => new DrawnLine(l.Min(q => q.Left), l.Max(q => q.Right), l[0].Baseline, l.Max(q => q.Size), l.Count,
            l.All(q => q.Snapped))).ToArray();
    }

    /// <summary>FakeGlyphs' advance of a string at a size (the atlas quantizes sizes to 0.25 pt).</summary>
    private static double Advance(string text, double size) => text.Length * (float)(Math.Round(size * 4) / 4) * 0.56;

    private static double RenderFor(OrbGpuOrb orb, double from, double seconds)
    {
        var t = from;
        while (t < from + seconds)
        {
            t += 1 / 60.0;
            orb.Render(t);
        }

        return t;
    }

    [Fact]
    public void TheQuietRuleShowsLoudTrafficAndFadesTenSecondsAfterIt()
    {
        var rule = new OrbLinesQuietRule();
        Assert.False(rule.Apply(Traffic(400, 400, false), 0));
        Assert.Null(rule.Reading);

        Assert.True(rule.Apply(Traffic(1200, 0, false), 1));
        var shown = rule.Reading!.Value;
        Assert.Equal(1, shown.ShownAt);
        Assert.Equal(11, shown.QuietAt);
        Assert.Equal(0, shown.Alpha(1, false));
        Assert.Equal(1, shown.Alpha(1 + OrbLinesReading.FadeIn, false), 6);

        // Quiet samples keep the readout (with their values) until 10 s after the last loud one.
        Assert.True(rule.Apply(Traffic(10, 0, false), 5));
        Assert.Equal(11, rule.Reading!.Value.QuietAt);
        Assert.Equal("10", rule.Reading!.Value.Content.First.Number);
        Assert.Equal(1, rule.Reading!.Value.Alpha(10.9, false), 6);
        Assert.InRange(rule.Reading!.Value.Alpha(11.3, false), 0.49, 0.51);
        Assert.True(rule.Reading!.Value.IsGone(11.6));

        // Once gone, quiet samples change nothing (no frames); a loud one brings it back with a fade in.
        Assert.False(rule.Apply(Traffic(5, 5, false), 12));
        Assert.True(rule.Apply(Traffic(5000, 0, false), 13));
        Assert.Equal(13, rule.Reading!.Value.ShownAt);
    }

    [Fact]
    public void ReturningWhileFadingOutStartsFromTheCurrentOpacity()
    {
        var rule = new OrbLinesQuietRule();
        rule.Apply(Traffic(5000, 0, false), 0);
        rule.Apply(Traffic(5000, 0, false), 10.3);
        var reading = rule.Reading!.Value;
        Assert.Equal(0.5, reading.Alpha(10.3, false), 6);
        Assert.Equal(1, reading.Alpha(10.3 + 0.5 * OrbLinesReading.FadeIn, false), 6);
        Assert.Equal(20.3, reading.QuietAt, 6);
    }

    [Fact]
    public void ReduceMotionMakesTheFadesInstant()
    {
        var rule = new OrbLinesQuietRule();
        rule.Apply(Traffic(5000, 0, true), 0);
        var reading = rule.Reading!.Value;
        Assert.Equal(1, reading.Alpha(0, reduceMotion: true));
        Assert.Equal(0, reading.Alpha(10, reduceMotion: true));
        Assert.False(reading.IsFading(0.1, reduceMotion: true));
        Assert.Equal("Kb/s", reading.Content.First.Unit);
    }

    [Fact]
    public void IntelligenceIsLoudWhileItHasAScoreOrAnAgentGeneratesAndKeepsItsLastValueWhileQuiet()
    {
        var rule = new OrbLinesQuietRule();
        var active = TestSums.Of(400, 132.8, 176, 400);
        Assert.True(rule.Apply(Intelligence(active), 1));
        var shown = rule.Reading!.Value.Content;
        Assert.Equal(OrbLinesKind.Intelligence, shown.Kind);
        Assert.Equal(new OrbLine(En["intel.glyph"], "133", string.Empty), shown.First);
        Assert.Equal(new OrbLine(En["intel.thinking.glyph"], "59", En["unit.tokS"]), shown.Second);
        Assert.Equal(En.Format("a11y.orb.intel", "133", "59"), shown.Spoken);
        Assert.Equal(En["perf.tooltip.note"], shown.Detail);

        // Agents that generate before any of them has a score: `–`, never 0.
        Assert.True(rule.Apply(OrbLinesText.Intelligence(PerfSetReading.None, IntelligenceSums.Zero, generating: true, En), 5));
        Assert.Equal("–", rule.Reading!.Value.Content.First.Number);
        Assert.Equal(15, rule.Reading!.Value.QuietAt);

        // Nothing at all: it keeps its last value, fades 10 s after the last loud sample, and a quiet sample of a gone
        // readout changes nothing.
        var quiet = OrbLinesText.Intelligence(PerfSetReading.None, IntelligenceSums.Zero, generating: false, En);
        Assert.False(rule.Apply(quiet, 6));
        Assert.True(rule.Reading!.Value.IsGone(15.6));
        Assert.False(rule.Apply(quiet, 16));
        Assert.False(new OrbLinesQuietRule().Apply(quiet, 0));
    }

    [Fact]
    public void SwitchingModesStartsTheOtherReadoutFromScratch()
    {
        var rule = new OrbLinesQuietRule();
        rule.Apply(Traffic(50_000, 0), 0);
        var intelligence = TestSums.Of(300, 92);
        Assert.True(rule.Apply(Intelligence(intelligence), 1));
        Assert.Equal((OrbLinesKind.Intelligence, 1.0), (rule.Reading!.Value.Content.Kind, rule.Reading!.Value.ShownAt));
        Assert.True(rule.Apply(Traffic(0, 0), 2));
        Assert.Null(rule.Reading);
    }

    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Reactor)]
    [InlineData(OrbStyle.Needle)]
    [InlineData(OrbStyle.Minimal)]
    public void TheIntelligenceLinesAreEachCenteredOnTheOrbTheLargerOnTop(OrbStyle style)
    {
        var orb = Create(style, size: 196);
        orb.SetLines(0, Intelligence(Long, ZhHant), true);
        RenderFor(orb, 0, 1);
        Assert.True(orb.LinesVisible(1));

        var placement = OrbLinesPlacement.For(style);
        var t = placement.TextSize(196);
        var lines = Drawn(orb, style, 196);
        Assert.Equal(2, lines.Length);
        var (intelligence, thinking) = (lines[0], lines[1]);
        var (top, bottom) = placement.Baselines(0.75 * 196, 196, OrbLinesKind.Intelligence);
        Assert.Equal(top, intelligence.Baseline, 3);
        Assert.Equal(bottom, thinking.Baseline, 3);

        // Each line on its own: centered on the orb by its width (within the device pixels its pieces snap to).
        foreach (var line in lines)
        {
            Assert.InRange((line.Left + line.Right) / 2, 0.75 * 196 - 0.5, 0.75 * 196 + 0.5);
            Assert.True(line.Snapped);
        }

        Assert.NotEqual(intelligence.Left, thinking.Left, 1);

        // 性能 · 133; 思考 · 176 · tok/s, except in the needle's hub, whose dial prints tok/s.
        Assert.Equal(1 + 3, intelligence.Pieces);
        Assert.Equal(style == OrbStyle.Needle ? 1 + 3 : 1 + 3 + 1, thinking.Pieces);
        Assert.Equal(style == OrbStyle.Needle, orb.UnitOnDial);

        // The 性能 number at p = 1.25 t (the needle's hub: 1.1 t), the thinking number at t.
        Assert.Equal(Math.Round(placement.ScoreScale * t * 4) / 4, intelligence.Size, 3);
        Assert.Equal(Math.Round(t * 4) / 4, thinking.Size, 3);
    }

    [Fact]
    public void ALineMovesOnlyWhenItsWidthChanges()
    {
        var orb = Create(OrbStyle.Led, size: 196);
        orb.SetLines(0, Intelligence(Long), true);
        RenderFor(orb, 0, 1);
        var before = Drawn(orb, OrbStyle.Led, 196);

        // 132 over 412 tok/s: the same digit counts, so neither line moves.
        orb.SetLines(1, Intelligence(TestSums.Of(2000, 132.36, 1236, 2000)), true);
        var same = Drawn(orb, OrbStyle.Led, 196);
        Assert.Equal(("132", "412"), (orb.LinesReading!.Value.Content.First.Number, orb.LinesReading!.Value.Content.Second.Number));
        Assert.Equal(before.Select(l => l.Left), same.Select(l => l.Left));

        // 69 over 59: both lines narrow, so both recenter once 性能 has counted down.
        orb.SetLines(2, Intelligence(Short), true);
        RenderFor(orb, 2, 1);
        var narrower = Drawn(orb, OrbStyle.Led, 196);
        Assert.Equal(("69", "59"), (orb.LinesReading!.Value.Content.First.Number, orb.LinesReading!.Value.Content.Second.Number));
        Assert.True(narrower[0].Left > before[0].Left);
        Assert.True(narrower[1].Left > before[1].Left);
    }

    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Reactor)]
    [InlineData(OrbStyle.Needle)]
    [InlineData(OrbStyle.Minimal)]
    public void TheIntelligenceReadoutFitsWithItsWidestNumbersWhateverItShows(OrbStyle style)
    {
        var draw = new OrbLinesDraw(new FakeGlyphs());
        var placement = OrbLinesPlacement.For(style);
        var shown = Intelligence(Long, ZhHant).Content!;
        var quiet = Intelligence(Short, ZhHant).Content!;
        var fits = 0;
        for (var s = 64.0; s <= 320; s += 2)
        {
            var t = placement.TextSize(s);
            var p = placement.ScoreScale * t;
            var (intelligence, thinking) = draw.WidestLines(placement, s, shown);
            var unit = placement.ThinkingUnit ? 0.16 * t + Advance("tok/s", 0.82 * t) : 0;
            Assert.Equal(Advance("性能", 0.82 * p) + 0.20 * p + 4 * 0.56 * p + Advance(",", p), intelligence, 3);
            Assert.Equal(Advance("思考", 0.82 * t) + 0.16 * t + 3 * 0.56 * t + Advance(".", t) + unit, thinking, 3);
            var (top, bottom) = placement.Baselines(0, s, OrbLinesKind.Intelligence);
            var inHub = placement.HubReach is not { } reach
                || (Math.Sqrt(intelligence * intelligence / 4 + top * top) <= reach * s && Math.Sqrt(thinking * thinking / 4 + bottom * bottom) <= reach * s);
            Assert.Equal(Math.Max(intelligence, thinking) <= placement.Fit * s && inHub, draw.Fits(placement, s, shown));
            Assert.Equal(draw.Fits(placement, s, shown), draw.Fits(placement, s, quiet));
            fits += draw.Fits(placement, s, shown) ? 1 : 0;
        }

        Assert.InRange(fits, 1, 128);
    }

    [Fact]
    public void TheNeedlesLinesKeepTheirBottomCornersInsideTheHub()
    {
        var draw = new OrbLinesDraw(new FakeGlyphs());
        var needle = OrbLinesPlacement.Needle;
        var shown = Intelligence(Long, ZhHant).Content!;
        Assert.False(needle.ThinkingUnit);

        // At 100 pt (the 7 pt floor) 8,888 at 1.1 t is wider than the 0.285 S width; from the size where both lines fit
        // that width, their bottom corners must also stay within the 0.223 S reach.
        var (intelligence, _) = draw.WidestLines(needle, 100, shown);
        Assert.True(intelligence > 0.285 * 100);
        Assert.False(draw.Fits(needle, 100, shown));
        Assert.True(draw.Fits(needle, 196, shown));
        for (var s = 100.0; s <= 196; s += 2)
        {
            var (top, bottom) = needle.Baselines(0, s, OrbLinesKind.Intelligence);
            var (first, second) = draw.WidestLines(needle, s, shown);
            var inside = Math.Sqrt(first * first / 4 + top * top) <= 0.223 * s && Math.Sqrt(second * second / 4 + bottom * bottom) <= 0.223 * s;
            Assert.Equal(Math.Max(first, second) <= 0.285 * s && inside, draw.Fits(needle, s, shown));
        }

        // The network readout keeps its width rule alone.
        Assert.True(draw.Fits(needle, 140, Traffic(1_280_000, 64_000).Content));
    }

    [Fact]
    public void TheThinkingLineSitsOnTheNearestBaselineAndTheLargerLineKeepsTheCapTopInTheHub()
    {
        // Above the value: the thinking line on the nearest baseline, the intelligence line one pitch above it.
        var (intelligence, thinking) = OrbLinesPlacement.Led.Baselines(100, 148, OrbLinesKind.Intelligence);
        Assert.Equal(100 - 0.245 * 148, thinking, 6);
        Assert.Equal(thinking - 1.32 * 7.4, intelligence, 6);

        // Reactor lowers its thinking line to c − 0.155 S so the larger line clears the core's rim; ↑ stays at c − 0.175 S.
        var (reactorIntelligence, reactorThinking) = OrbLinesPlacement.Reactor.Baselines(100, 148, OrbLinesKind.Intelligence);
        Assert.Equal(100 - 0.155 * 148, reactorThinking, 6);
        Assert.Equal(reactorThinking - 1.32 * 7, reactorIntelligence, 6);
        Assert.Equal(100 - 0.175 * 148, OrbLinesPlacement.Reactor.Baselines(100, 148, OrbLinesKind.Traffic).Bottom, 6);

        // Below it (needle, whose 性能 line is 1.1 t): that line drops by its extra cap height, the thinking line one pitch below.
        var t = 0.046 * 196;
        var (top, bottom) = OrbLinesPlacement.Needle.Baselines(100, 196, OrbLinesKind.Intelligence);
        Assert.Equal(100 + 0.108 * 196 + 0.70 * (1.1 * t - t), top, 6);
        Assert.Equal(top + 1.32 * t, bottom, 6);
        Assert.Equal(OrbLinesPlacement.Needle.Baselines(100, 196, OrbLinesKind.Traffic).Top - 0.70 * t, top - 0.70 * 1.1 * t, 6);
    }

    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Minimal)]
    public void TheLargerIntelligenceLineRaisesTheBlocksTopForTheStackShift(OrbStyle style)
    {
        var placement = OrbLinesPlacement.For(style);
        var layout = OrbReadoutLayout.For(style);
        var t = placement.TextSize(196);
        var traffic = placement.StackShift(layout, 196, OrbLinesKind.Traffic);

        // Half the extra cap height, but still never closer to the agent dots than the clearance.
        var titleBottom = layout.TitleBaseline * 196 + 0.22 * OrbReadoutLayout.TextSize(layout.TitleSize, 196);
        var limit = layout.DotsY is { } dots ? dots * 196 - OrbLinesPlacement.DotDiameter(196) / 2 - 0.05 * 196 - titleBottom : double.MaxValue;
        var intelligence = placement.StackShift(layout, 196, OrbLinesKind.Intelligence);
        Assert.True(intelligence > traffic);
        Assert.Equal(Math.Min(traffic + 0.70 * (1.25 * t - t) / 2, limit), intelligence, 6);

        var orb = Create(style, size: 196);
        orb.SetLines(0, Intelligence(Long), true);
        RenderFor(orb, 0, 1);
        Assert.Equal(placement.StackShift(layout, 196, OrbLinesKind.Intelligence), orb.ReadoutShift, 6);
    }

    [Fact]
    public void TheNetworkReadoutKeepsItsTable()
    {
        var orb = Create(OrbStyle.Led, size: 196);
        orb.SetLines(0, Traffic(1_280_000, 64_000), true);
        RenderFor(orb, 0, 1);
        var t = OrbLinesPlacement.Led.TextSize(196);
        var lines = Drawn(orb, OrbStyle.Led, 196);
        var (down, up) = OrbLinesPlacement.Led.Baselines(0.75 * 196, 196, OrbLinesKind.Traffic);
        Assert.Equal(down, lines[0].Baseline, 3);
        Assert.Equal(up, lines[1].Baseline, 3);

        // Both arrows centered in the 0.50 t box at the shared left edge of a line centered on the orb.
        var left = 0.75 * 196 - orb.Lines.LineWidth(t, bits: false) / 2;
        Assert.All(lines, line => Assert.Equal(left + (0.50 * t - Advance("↓", t)) / 2, line.Left, 3));
        Assert.Equal(OrbLinesPlacement.Led.StackShift(OrbReadoutLayout.Led, 196, OrbLinesKind.Traffic), orb.ReadoutShift, 6);
    }

    [Theory]
    [InlineData(OrbStyle.Led, 0.0950)]
    [InlineData(OrbStyle.Liquid, 0.0950)]
    [InlineData(OrbStyle.Minimal, 0.09854)]
    [InlineData(OrbStyle.Reactor, 0)]
    [InlineData(OrbStyle.Needle, 0)]
    public void TheStackShiftCentersBlockAndValue(OrbStyle style, double expected)
    {
        var placement = OrbLinesPlacement.For(style);
        var layout = OrbReadoutLayout.For(style);
        Assert.Equal(expected, placement.StackShift(layout, 148, OrbLinesKind.Traffic) / 148, 3);
        Assert.Equal(0, placement.StackShift(layout, 148, OrbLinesKind.Traffic, 0));
        Assert.Equal(placement.StackShift(layout, 148, OrbLinesKind.Traffic), placement.StackShift(layout, 148, OrbLinesKind.Traffic, 1), 9);
    }

    [Fact]
    public void SmallOrbsStopTheShiftClearOfTheAgentDots()
    {
        // At 96 pt the 7 pt floor makes the block relatively tall: the title would come within 0.05 S of the dots.
        var layout = OrbReadoutLayout.Led;
        var shift = OrbLinesPlacement.Led.StackShift(layout, 96, OrbLinesKind.Traffic);
        Assert.Equal(0.0929, shift / 96, 3);
        var titleBottom = (layout.TitleBaseline * 96 + shift) + 0.22 * OrbReadoutLayout.TextSize(layout.TitleSize, 96);
        var dotsTop = layout.DotsY!.Value * 96 - OrbLinesPlacement.DotDiameter(96) / 2;
        Assert.Equal(0.05 * 96, dotsTop - titleBottom, 6);
    }

    [Fact]
    public void LinesStackDownloadOverUpload()
    {
        var (down, up) = OrbLinesPlacement.Led.Baselines(100, 148, OrbLinesKind.Traffic);
        Assert.Equal(100 - 0.245 * 148, up, 6);
        Assert.Equal(up - 1.32 * 7.4, down, 6);
        var (needleDown, needleUp) = OrbLinesPlacement.Needle.Baselines(100, 148, OrbLinesKind.Traffic);
        Assert.Equal(100 + 0.108 * 148, needleDown, 6);
        Assert.True(needleUp > needleDown);
    }

    [Fact]
    public void TheReadoutDrawsCenteredAboveTheValueAndMovesTheStackDown()
    {
        var orb = Create(OrbStyle.Led);
        orb.Render(0.1);
        var valueBefore = orb.List.Texts.ToArray().Max(t => t.Rect.W);
        var restingY = orb.List.Texts.ToArray().Select(t => t.Rect.Y + t.Rect.W).ToArray();

        Assert.True(orb.SetLines(0.1, Traffic(1_280_000, 64_000, false), true));
        var t = RenderFor(orb, 0.1, 1);
        var list = orb.List;
        Assert.True(orb.LinesVisible(t));
        // Two lines of arrow, number characters and unit: at least 2 × (1 + 4 + 1) quads beyond the readout's.
        Assert.True(list.TextCount >= restingY.Length + 12);
        Assert.Equal(0.0950 * 148, orb.ReadoutShift, 1);
        var block = list.Texts.ToArray().Where(q => q.Rect.Y + q.Rect.W < 0.75 * 148 - 0.10 * 148).ToArray();
        Assert.NotEmpty(block);
        var left = block.Min(q => q.Rect.X);
        var right = block.Max(q => q.Rect.X + q.Rect.Z);
        Assert.InRange((left + right) / 2, 0.75 * 148 - 6, 0.75 * 148 + 6);
        Assert.True(valueBefore > 0);
    }

    [Fact]
    public void FadesAskForThirtyFramesAQuietOrbForNone()
    {
        var orb = Create(OrbStyle.Minimal);
        RenderFor(orb, 0, 0.5);
        Assert.Equal(FrameClass.None, orb.Demand);

        orb.SetLines(0.5, Traffic(5000, 5000, false), true);
        orb.Render(0.55);
        Assert.True(orb.Demand >= FrameClass.Fps30);
        var t = RenderFor(orb, 0.55, 1);
        Assert.Equal(FrameClass.None, orb.Demand);

        // 10 s after the last loud sample it fades out at 30 fps, then draws nothing.
        orb.SetLines(t, Traffic(0, 0, false), true);
        orb.Render(10.6);
        Assert.True(orb.Demand >= FrameClass.Fps30);
        orb.Render(11.5);
        orb.Render(11.6);
        Assert.Equal(FrameClass.None, orb.Demand);
        Assert.False(orb.LinesVisible(11.6));
        Assert.Equal(0, orb.ReadoutShift, 6);
    }

    [Fact]
    public void ThumbnailsAndUnavailableOrbsDrawNoReadout()
    {
        var small = Create(OrbStyle.Led, size: 60);
        small.SetLines(0, Traffic(50_000, 0, false), true);
        RenderFor(small, 0, 0.5);
        Assert.False(small.LinesVisible(0.5));

        var unavailable = Create(OrbStyle.Led);
        unavailable.SetData(0, 0, SpeedTier.Idle, SpeedTitle.Idle, [], string.Empty, "Not found");
        unavailable.SetLines(0, Traffic(50_000, 0, false), true);
        RenderFor(unavailable, 0, 0.5);
        Assert.False(unavailable.LinesVisible(0.5));
    }

    [Fact]
    public void AReadoutThatDoesNotFitIsNotDrawn()
    {
        // Needle needs S ≈ 137: below that neither the readout nor the dial legend appears.
        var orb = Create(OrbStyle.Needle, size: 112);
        orb.SetLines(0, Traffic(50_000, 0, false), true);
        RenderFor(orb, 0, 0.5);
        Assert.False(orb.LinesVisible(0.5));
        Assert.False(orb.UnitOnDial);
    }

    [Fact]
    public void TheNeedlePrintsItsUnitOnTheDialUnderTheNeedle()
    {
        var orb = Create(OrbStyle.Needle, size: 196);
        orb.SetLines(0, null, true);
        RenderFor(orb, 0, 0.3);
        Assert.True(orb.UnitOnDial);
        Assert.True(orb.List.OverlayTextStart >= 1);
        Assert.Contains(orb.List.Batches.ToArray(), b => b.Pass == OrbPass.Text);

        orb.SetLines(0.3, null, false);
        RenderFor(orb, 0.3, 0.3);
        Assert.False(orb.UnitOnDial);
        Assert.Equal(0, orb.List.OverlayTextStart);
    }
}
