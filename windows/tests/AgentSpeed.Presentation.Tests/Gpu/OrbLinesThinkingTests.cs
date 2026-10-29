using AgentSpeed.Core.Intelligence;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Lines;

namespace AgentSpeed.Presentation.Tests.Gpu;

/// <summary>The intelligence readout's thinking line hidden at zero (DESIGN 10, Intelligence readout).</summary>
public partial class OrbLinesTests
{
    /// <summary>125 over 0 tok/s: a source that never reports reasoning.</summary>
    private static readonly IntelligenceSums Unthinking = TestSums.Of(400, 125, 0, 400);

    [Fact]
    public void TheThinkingLineHoldsThreeSecondsThenFadesAsTheIntelligenceLineGlides()
    {
        var fade = default(OrbThinkingFade).Next(zero: true, 10);
        Assert.Equal(13, fade.HideAt);
        Assert.Equal(fade, fade.Next(zero: true, 11.5));

        // Held: fully drawn, nothing moves, no frames; the orb wakes for the hold's end.
        Assert.Equal((1.0, 0.0), (fade.Alpha(12.99, false), fade.Single(12.99, false)));
        Assert.False(fade.IsMoving(12.99, false));
        Assert.False(fade.Hides(12.99));
        Assert.Equal(13, fade.NextChange(12));

        // Then a 0.25 s fade and a 0.3 s smoothstep glide.
        Assert.True(fade.Hides(13));
        Assert.Null(fade.NextChange(13));
        Assert.Equal(0.5, fade.Alpha(13.125, false), 6);
        Assert.Equal(0.15625, fade.Single(13.075, false), 6);
        Assert.Equal(0.5, fade.Single(13.15, false), 6);
        Assert.Equal(0.84375, fade.Single(13.225, false), 6);
        Assert.True(fade.IsMoving(13.28, false));
        Assert.Equal((0.0, 1.0), (fade.Alpha(13.3, false), fade.Single(13.3, false)));
        Assert.False(fade.IsMoving(13.3, false));
    }

    [Fact]
    public void AValueAboveZeroBringsTheThinkingLineBackAtOnce()
    {
        var hidden = default(OrbThinkingFade).Next(zero: true, 10);
        var back = hidden.Next(zero: false, 20);
        Assert.Null(back.HideAt);
        Assert.False(back.Hides(20));
        Assert.Equal([0, 0.5, 1], new[] { 20, 20.125, 20.25 }.Select(t => Math.Round(back.Alpha(t, false), 6)));
        Assert.Equal([1, 0.5, 0], new[] { 20, 20.15, 20.3 }.Select(t => Math.Round(back.Single(t, false), 6)));
        Assert.True(back.IsMoving(20.2, false));
        Assert.False(back.IsMoving(20.3, false));

        // A value back during the hold: nothing had moved, so nothing moves.
        var held = hidden.Next(zero: false, 12);
        Assert.Equal((1.0, 0.0), (held.Alpha(14, false), held.Single(14, false)));
        Assert.False(held.IsMoving(13.1, false));

        // Back half way through: the fade and the glide reverse from where they are.
        var midway = hidden.Next(zero: false, 13.1);
        Assert.Equal(hidden.Alpha(13.1, false), midway.Alpha(13.1, false), 6);
        Assert.Equal(hidden.Single(13.1, false), midway.Single(13.1, false), 6);
        Assert.Equal((1.0, 0.0), (midway.Alpha(13.2, false), Math.Round(midway.Single(13.2, false), 9)));

        // The next zero holds 3 s again.
        var again = back.Next(zero: true, 25);
        Assert.Equal((1.0, 0.0), (again.Alpha(27.9, false), again.Single(27.9, false)));
        Assert.Equal(0.5, again.Alpha(28.125, false), 6);
    }

    [Fact]
    public void ReduceMotionHidesAndBringsBackTheThinkingLineAtOnce()
    {
        var fade = default(OrbThinkingFade).Next(zero: true, 10);
        Assert.Equal((1.0, 0.0), (fade.Alpha(12.9, true), fade.Single(12.9, true)));
        Assert.Equal((0.0, 1.0), (fade.Alpha(13, true), fade.Single(13, true)));
        Assert.False(fade.IsMoving(13.1, true));
        var back = fade.Next(zero: false, 14);
        Assert.Equal((1.0, 0.0), (back.Alpha(14, true), back.Single(14, true)));
    }

    [Fact]
    public void TheHoldStartsWithTheFirstSampleThatReadsZero()
    {
        var rule = new OrbLinesQuietRule();
        rule.Apply(Intelligence(Long), 0);
        Assert.Null(rule.Reading!.Value.Thinking.HideAt);
        Assert.True(rule.Apply(Intelligence(Unthinking), 1));
        Assert.Equal(4, rule.Reading!.Value.Thinking.HideAt);

        // The same zero again keeps the hold, and so does a quiet sample, which carries nothing.
        rule.Apply(Intelligence(Unthinking), 1.5);
        Assert.Equal(4, rule.Reading!.Value.Thinking.HideAt);
        rule.Apply(OrbLinesText.Intelligence(Presentation.Perf.PerfSetReading.None, IntelligenceSums.Zero, generating: false, En), 2);
        Assert.Equal(4, rule.Reading!.Value.Thinking.HideAt);
        rule.Apply(Intelligence(Long), 2.5);
        Assert.Null(rule.Reading!.Value.Thinking.HideAt);

        // A readout that appears at zero holds from its first sample; network speed never hides a line.
        var fresh = new OrbLinesQuietRule();
        fresh.Apply(Intelligence(Unthinking), 7);
        Assert.Equal(10, fresh.Reading!.Value.Thinking.HideAt);
        var traffic = new OrbLinesQuietRule();
        traffic.Apply(Traffic(5000, 0), 0);
        Assert.Equal(default, traffic.Reading!.Value.Thinking);
        Assert.False(traffic.Reading!.Value.Content.ThinkingZero);
    }

    [Fact]
    public void ARateThatShowsAsZeroCountsAsZero()
    {
        // 0.1 reasoning tokens in the 3 s window read 0.0 tok/s; 0.3 read 0.1.
        Assert.True(Intelligence(TestSums.Of(400, 125, 0.1, 400)).Content!.ThinkingZero);
        Assert.False(Intelligence(TestSums.Of(400, 125, 0.3, 400)).Content!.ThinkingZero);
        Assert.True(Intelligence(Unthinking).Content!.ThinkingZero);
        Assert.False(Intelligence(Long).Content!.ThinkingZero);
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "性能 125")]
    [InlineData(LanguageCodes.ZhHans, "性能 125")]
    [InlineData(LanguageCodes.En, "performance 125")]
    [InlineData(LanguageCodes.Ja, "性能 125")]
    public void TheLabelReadsTheIntelligenceAloneOnceTheThinkingLineHides(string language, string alone)
    {
        var localizer = new Localizer(Localizer.LoadEmbeddedTable(), language);
        var rule = new OrbLinesQuietRule();
        rule.Apply(Intelligence(Unthinking, localizer), 0);
        var reading = rule.Reading!.Value;
        Assert.Equal(localizer.Format("a11y.orb.intel", "125", "0"), reading.Spoken(2.9));
        Assert.Equal(alone, reading.Spoken(3));
        Assert.Equal(alone, localizer.Format("a11y.orb.intel.only", "125"));

        rule.Apply(Intelligence(Long, localizer), 4);
        Assert.Equal(localizer.Format("a11y.orb.intel", "133", "176"), rule.Reading!.Value.Spoken(4));
    }

    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Reactor)]
    [InlineData(OrbStyle.Needle)]
    [InlineData(OrbStyle.Minimal)]
    public void TheIntelligenceLineAloneSitsOnTheNearestBaseline(OrbStyle style)
    {
        var orb = Create(style, size: 196);
        orb.SetLines(0, Intelligence(Unthinking, ZhHant), true);
        RenderFor(orb, 0, 1);
        orb.Render(2.9);
        var placement = OrbLinesPlacement.For(style);
        var (top, bottom) = placement.Baselines(0.75 * 196, 196, OrbLinesKind.Intelligence);
        var held = Drawn(orb, style, 196);
        Assert.Equal(2, held.Length);
        Assert.Equal(top, held[0].Baseline, 3);
        Assert.Equal(bottom, held[1].Baseline, 3);

        orb.Render(3.3);
        var lines = Drawn(orb, style, 196);
        var line = Assert.Single(lines);

        // The nearest baseline: the thinking line's above the value (reactor c − 0.155 S), its own below it (needle).
        var t = placement.TextSize(196);
        var expected = style switch
        {
            OrbStyle.Reactor => 0.75 * 196 - 0.155 * 196,
            OrbStyle.Needle => 0.75 * 196 + 0.108 * 196 + 0.70 * (1.1 * t - t),
            OrbStyle.Minimal => 0.75 * 196 - 0.228 * 196,
            _ => 0.75 * 196 - 0.245 * 196,
        };
        Assert.Equal(expected, line.Baseline, 3);
        Assert.Equal(placement.SingleBaseline(0.75 * 196, 196), line.Baseline, 3);
        Assert.Equal(style == OrbStyle.Needle ? top : bottom, line.Baseline, 3);

        // 性能 · 125, centered and on device pixels; fit is unchanged, so the readout stays.
        Assert.Equal(1 + 3, line.Pieces);
        Assert.Equal(Math.Round(placement.ScoreScale * t * 4) / 4, line.Size, 3);
        Assert.InRange((line.Left + line.Right) / 2, 0.75 * 196 - 0.5, 0.75 * 196 + 0.5);
        Assert.True(line.Snapped);
        Assert.True(orb.LinesVisible(3.3));
        Assert.Equal(style == OrbStyle.Needle, orb.UnitOnDial);
    }

    [Fact]
    public void TheIntelligenceLineGlidesWhileTheThinkingLineFades()
    {
        var orb = Create(OrbStyle.Led, size: 196);
        orb.SetLines(0, Intelligence(Unthinking, ZhHant), true);
        RenderFor(orb, 0, 1);
        var placement = OrbLinesPlacement.Led;
        var (top, bottom) = placement.Baselines(0.75 * 196, 196, OrbLinesKind.Intelligence);
        var full = ThinkingAlpha(orb, bottom);

        // 0.15 s in: half way along the smoothstep, the thinking line at 40 % of its opacity.
        orb.Render(3.15);
        var lines = Drawn(orb, OrbStyle.Led, 196);
        Assert.Equal(2, lines.Length);
        Assert.Equal((top + bottom) / 2, lines[0].Baseline, 3);
        Assert.Equal(bottom, lines[1].Baseline, 3);
        Assert.Equal(0.4 * full, ThinkingAlpha(orb, bottom), 3);

        // Back above 0: the thinking line fades in at once and the intelligence line glides back up.
        orb.Render(3.4);
        orb.SetLines(5, Intelligence(Long, ZhHant), true);
        orb.Render(5.15);
        lines = Drawn(orb, OrbStyle.Led, 196);
        Assert.Equal((top + bottom) / 2, lines[0].Baseline, 3);
        Assert.Equal(0.6 * full, ThinkingAlpha(orb, bottom), 3);
        orb.Render(5.3);
        Assert.Equal(top, Drawn(orb, OrbStyle.Led, 196)[0].Baseline, 3);
        Assert.Equal(full, ThinkingAlpha(orb, bottom), 3);
    }

    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Minimal)]
    public void TheStackShiftFollowsTheGlideToTheSingleLinesTop(OrbStyle style)
    {
        var placement = OrbLinesPlacement.For(style);
        var layout = OrbReadoutLayout.For(style);
        var t = placement.TextSize(196);
        var two = placement.StackShift(layout, 196, OrbLinesKind.Intelligence);
        var alone = placement.StackShift(layout, 196, OrbLinesKind.Intelligence, single: true);

        // The block's top becomes the single line's cap top, nearest − 0.70 p, still clear of the agent dots.
        var valueTop = (layout.ValueBaseline - 0.70 * layout.ValueSize) * 196;
        var centered = (valueTop - (placement.NearestBaseline * 196 - 0.70 * 1.25 * t)) / 2;
        var titleBottom = layout.TitleBaseline * 196 + 0.22 * OrbReadoutLayout.TextSize(layout.TitleSize, 196);
        var limit = layout.DotsY is { } dots ? dots * 196 - OrbLinesPlacement.DotDiameter(196) / 2 - 0.05 * 196 - titleBottom : double.MaxValue;
        Assert.Equal(Math.Min(Math.Max(0, centered), limit), alone, 6);
        Assert.True(alone < two);

        var orb = Create(style, size: 196);
        orb.SetLines(0, Intelligence(Unthinking), true);
        RenderFor(orb, 0, 1);
        Assert.Equal(two, orb.ReadoutShift, 6);
        orb.Render(3.15);
        Assert.Equal(Easing.Lerp(two, alone, 0.5), orb.ReadoutShift, 6);
        orb.Render(3.3);
        Assert.Equal(alone, orb.ReadoutShift, 6);

        orb.SetLines(5, Intelligence(Long), true);
        orb.Render(5);
        Assert.Equal(alone, orb.ReadoutShift, 6);
        orb.Render(5.15);
        Assert.Equal(Easing.Lerp(two, alone, 0.5), orb.ReadoutShift, 6);
        orb.Render(5.3);
        Assert.Equal(two, orb.ReadoutShift, 6);
    }

    [Theory]
    [InlineData(OrbStyle.Reactor)]
    [InlineData(OrbStyle.Needle)]
    public void TheFixedReadoutsKeepTheirStackWhenTheThinkingLineHides(OrbStyle style)
    {
        Assert.Equal(0, OrbLinesPlacement.For(style).StackShift(OrbReadoutLayout.For(style), 196, OrbLinesKind.Intelligence, single: true));
        var orb = Create(style, size: 196);
        orb.SetLines(0, Intelligence(Unthinking), true);
        RenderFor(orb, 0, 1);
        orb.Render(3.3);
        Assert.Equal(0, orb.ReadoutShift);
    }

    [Fact]
    public void TheHoldCostsNoFramesAndTheGlideRunsAtThirtyFramesPerSecond()
    {
        var orb = Create(OrbStyle.Minimal);
        orb.SetLines(0, Intelligence(Unthinking), true);
        RenderFor(orb, 0, 1);
        Assert.Equal(FrameClass.None, orb.Demand);

        // A static orb wakes at the hold's end, then renders the fade and glide at 30 fps.
        Assert.Equal(3, orb.NextWake);
        orb.Render(3.05);
        Assert.Equal(FrameClass.Fps30, orb.Demand);
        orb.Render(3.31);
        Assert.Equal(FrameClass.None, orb.Demand);

        // Then it wakes for the quiet rule's fade, 10 s after the last loud sample.
        Assert.Equal(10, orb.NextWake);
        orb.Render(10.6);
        Assert.False(orb.LinesVisible(10.6));
        Assert.Null(orb.NextWake);
    }

    [Fact]
    public void ReduceMotionHidesTheThinkingLineWithoutFrames()
    {
        var orb = Create(OrbStyle.Minimal, size: 196, reduceMotion: true);
        orb.SetLines(0, Intelligence(Unthinking), true);
        RenderFor(orb, 0, 1);
        orb.Render(3);
        Assert.Single(Drawn(orb, OrbStyle.Minimal, 196));
        Assert.Equal(FrameClass.None, orb.Demand);
        var alone = OrbLinesPlacement.Minimal.StackShift(OrbReadoutLayout.Minimal, 196, OrbLinesKind.Intelligence, single: true);
        Assert.Equal(alone, orb.ReadoutShift, 6);
    }

    /// <summary>The thinking line's number opacity: its quads in the number color on <paramref name="baseline"/>.</summary>
    private static double ThinkingAlpha(OrbGpuOrb orb, double baseline)
    {
        var effects = orb.Scene.Effects;
        var ctx = new OrbGpuContext(196, 2, OrbTokens.Dark, effects);
        var list = new OrbDrawList();
        orb.Lines.Add(list, ctx, orb.Scene, orb.LinesReading!.Value, OrbLinesPlacement.Led, 0, OrbLinesColors.Standard(ctx), OrbTextClip.None,
            yieldsToPill: false);
        return list.Texts.ToArray()
            .Where(q => Math.Abs(q.Rect.Y + 2 + 0.75 * (q.Rect.W - 4) - baseline) < 1)
            .Max(q => q.Color.W);
    }
}
