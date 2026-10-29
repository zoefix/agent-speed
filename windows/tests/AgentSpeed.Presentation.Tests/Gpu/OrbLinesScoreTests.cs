using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Gpu;

/// <summary>
/// 性能 on the orb's readout (DESIGN 10, Performance readout; DESIGN 12): the set's value in compact form and its words, the
/// quiet rule, the forms' colors, the provisional mark and the score's motion.
/// </summary>
public partial class OrbLinesTests
{
    /// <summary>176 tok/s of thinking in the window.</summary>
    private static readonly IntelligenceSums Thinking176 = new(1200, 528, 1200);

    private static PerfSetReading Score(int? shown, PerfForm form = PerfForm.Live, bool provisional = false, int count = 1) =>
        shown is null ? PerfSetReading.None : new PerfSetReading(form, shown, provisional, count, null, null);

    private static OrbLinesSample Scored(PerfSetReading score, bool generating = true, IntelligenceSums? sums = null) =>
        OrbLinesText.Intelligence(score, sums ?? Thinking176, generating, ZhHant);

    private static AgentStatus ScoredAgent(string id, string project, PerfForm form, double score, double thinking = 0)
    {
        var set = (form == PerfForm.Live ? TestSums.Live(score) : TestSums.Resting(score)) with { AgentId = id };
        var sums = new IntelligenceSums(thinking > 0 ? 3 * thinking : 0, 3 * thinking, thinking > 0 ? 3 * thinking : 0) { Score = set };
        return new AgentStatus(id, SourceKind.ClaudeCode, project, "model", form == PerfForm.Live ? AgentPhase.Generating : AgentPhase.Idle,
            false, Rate.Zero, null, DateTimeOffset.UnixEpoch)
        {
            Intelligence = new AgentIntelligence(sums, set),
        };
    }

    private static (PerfSetReading Score, IntelligenceSums Sums) Set(params AgentStatus[] agents)
    {
        var sums = agents.Aggregate(IntelligenceSums.Zero, (sum, agent) => sum + agent.Intelligence.Sums);
        return (PerfSetReading.Of(sums, agents), sums);
    }

    private static double ScoreSize(OrbStyle style, double size = 196)
    {
        var placement = OrbLinesPlacement.For(style);
        return placement.ScoreScale * placement.TextSize(size);
    }

    private static OrbGpuOrb Showing(OrbStyle style, OrbLinesSample sample, double size = 196, bool reduceMotion = false, bool highContrast = false)
    {
        var effects = new EffectsPolicy(EffectsIntensity.Standard, reduceMotion, highContrast);
        var orb = new OrbGpuOrb(3, new FakeGlyphs());
        orb.Configure(new OrbGpuConfig(style, size, effects, true, true, Redline, Strings, null, SourceSet.Empty),
            new OrbGpuContext(size, 2, OrbTokens.Dark, effects, highContrast ? OrbContrastColors.Default : null));
        orb.SetData(0, 0, SpeedTier.Idle, SpeedTitle.Idle, [], "today", null);
        orb.SetLines(0, sample, true);
        RenderFor(orb, 0, 1);
        return orb;
    }

    /// <summary>The readout's own shapes and quads, drawn alone into a fresh list at the orb's current time.</summary>
    private static OrbDrawList LinesOnly(OrbGpuOrb orb, OrbStyle style, double size = 196, bool highContrast = false)
    {
        var effects = new EffectsPolicy(EffectsIntensity.Standard, false, highContrast);
        var ctx = new OrbGpuContext(size, 2, OrbTokens.Dark, effects, highContrast ? OrbContrastColors.Default : null);
        var list = new OrbDrawList();
        orb.Lines.Add(list, ctx, orb.Scene, orb.LinesReading!.Value, OrbLinesPlacement.For(style), 0, OrbLinesColors.Standard(ctx),
            OrbTextClip.None, yieldsToPill: false);
        return list;
    }

    private static OrbGpuInstance[] Dots(OrbDrawList list) => [.. list.Instances.ToArray().Where(i => i.Shape == OrbShape.Circle)];

    /// <summary>The intelligence number's quads (size p, the larger ones on the top line).</summary>
    private static OrbTextInstance[] Digits(OrbDrawList list, double p) =>
        [.. list.Texts.ToArray().Where(q => Math.Abs(q.Rect.W - 4 - Math.Round(p * 4) / 4) < 0.01).OrderBy(q => q.Rect.X)];

    [Fact]
    public void TheReadoutShowsTheHighestScoreOfTheOrbsAgentsAndNamesItsSupplier()
    {
        var (score, sums) = Set(
            ScoredAgent("a1", "nova", PerfForm.Live, 1286.2, thinking: 128),
            ScoredAgent("a2", "rocket", PerfForm.Live, 410),
            ScoredAgent("a3", "atlas", PerfForm.Resting, 9500));
        Assert.Equal((PerfForm.Live, 1286, 2), (score.Form, score.Shown, score.Count));

        var content = OrbLinesText.Intelligence(score, sums, generating: true, ZhHant).Content!;
        Assert.Equal(new OrbLine("性能", "1,286", string.Empty), content.First);
        Assert.Equal(new OrbLineScore(1286, false, false), content.Score);
        Assert.Equal("性能 1,286，2 個 agent 中最高，思考每秒 128 個 token", content.Spoken);
        Assert.Equal("性能 1,286，2 個 agent 中最高", content.SpokenAlone);

        // The Windows tooltip: perf.tooltip.top with the supplying agent's project, then the reference note.
        Assert.Equal("性能 1,286 · 最高：nova\n1000 為參考水準，沒有上限", content.Detail);
    }

    [Fact]
    public void FromTenThousandTheReadoutShowsTheCompactFormAndSaysTheFullOne()
    {
        var content = Scored(Score(12_840)).Content!;
        Assert.Equal("12.8k", content.First.Number);
        Assert.Equal("性能 12,840，思考每秒 176 個 token", content.Spoken);
    }

    [Fact]
    public void ProvisionalAndRestingScoresSayItAndCarryTheirForm()
    {
        var provisional = Scored(Score(912, provisional: true, count: 2)).Content!;
        Assert.Equal(new OrbLineScore(912, false, true), provisional.Score);
        Assert.Equal("性能 912，初步（樣本少），2 個 agent 中最高，思考每秒 176 個 token", provisional.Spoken);

        var resting = Scored(Score(1540, PerfForm.Resting, count: 2), generating: false, sums: IntelligenceSums.Zero).Content!;
        Assert.Equal(new OrbLineScore(1540, true, false), resting.Score);
        Assert.True(resting.ThinkingZero);
        Assert.Equal("性能 1,540，閒置中，2 個 agent 中最高", resting.SpokenAlone);
    }

    [Fact]
    public void NoScoreShowsADashWhileItsAgentsGenerateAndNothingOtherwise()
    {
        var reading = Scored(PerfSetReading.None);
        Assert.True(reading.Loud);
        var content = reading.Content!;
        Assert.Equal("–", content.First.Number);
        Assert.Equal(OrbLineScore.None, content.Score);
        Assert.Equal("性能：尚無分數，思考每秒 176 個 token", content.Spoken);
        Assert.Equal("性能：尚無分數", content.SpokenAlone);
        Assert.Equal("1000 為參考水準，沒有上限", content.Detail);

        var quiet = Scored(PerfSetReading.None, generating: false);
        Assert.False(quiet.Loud);
        Assert.Null(quiet.Content);
    }

    [Fact]
    public void ARestingScoreKeepsTheReadoutUpWithoutAnyGeneration()
    {
        var rule = new OrbLinesQuietRule();
        Assert.True(rule.Apply(Scored(Score(720, PerfForm.Resting), generating: false, IntelligenceSums.Zero), 0));
        foreach (var time in new[] { 30.0, 300, 590 })
        {
            rule.Apply(Scored(Score(720, PerfForm.Resting), generating: false, IntelligenceSums.Zero), time);
            Assert.Equal(time + OrbLinesQuietRule.QuietAfter, rule.Reading!.Value.QuietAt);
        }

        // A set with nothing to show and no agent generating never brings a readout up.
        Assert.False(new OrbLinesQuietRule().Apply(Scored(PerfSetReading.None, generating: false), 0));
    }

    [Fact]
    public void AfterItsAgentsLeaveTheOrbKeepsItsLastScoreWhileItFades()
    {
        var rule = new OrbLinesQuietRule();
        rule.Apply(Scored(Score(720, PerfForm.Resting, count: 2), generating: false, IntelligenceSums.Zero), 0);
        var last = rule.Reading!.Value;

        // The agents leave the list: the set has no score and nobody generates. The pale 720 stays, unchanged, for the 10 s
        // and the 0.6 s fade; then the readout is gone and further quiet samples change nothing.
        Assert.False(rule.Apply(Scored(PerfSetReading.None, generating: false), 1));
        Assert.Equal(last, rule.Reading!.Value);
        Assert.Equal("720", rule.Reading!.Value.Content.First.Number);
        Assert.Equal(new OrbLineScore(720, true, false), rule.Reading!.Value.Content.Score);
        Assert.Equal(0.5, rule.Reading!.Value.Alpha(10.3, false), 6);
        Assert.True(rule.Reading!.Value.IsGone(10.6));
        Assert.False(rule.Apply(Scored(PerfSetReading.None, generating: false), 11));
    }

    [Fact]
    public void AChangeOfAtLeastOnePercentAndThreeCountsWithEaseOutCubic()
    {
        var motion = OrbScoreMotion.Start(new OrbLineScore(600, false, false)).Next(new OrbLineScore(1200, false, false), 10);
        var duration = PerfMotion.CountDuration(600, 1200);
        Assert.Equal(0.65, duration, 9);

        Assert.Equal(600, motion.Frame(10, false).Shown);
        var mid = motion.Frame(10 + duration / 2, false);
        Assert.Equal((int)Math.Round(600 + 600 * (1 - Math.Pow(0.5, 3)), MidpointRounding.AwayFromZero), mid.Shown);
        Assert.Equal((1.0, 0.0), (mid.Alpha, mid.OutgoingAlpha));
        Assert.Equal(1200, motion.Frame(10 + duration, false).Shown);

        // Frames at 30 fps only for its duration.
        Assert.True(motion.IsMoving(10 + duration - 0.01, false));
        Assert.False(motion.IsMoving(10 + duration + 1e-9, false));

        // A fall counts down the same way.
        var fall = OrbScoreMotion.Start(new OrbLineScore(1200, false, false)).Next(new OrbLineScore(600, false, false), 0);
        Assert.InRange(fall.Frame(0.1, false).Shown!.Value, 601, 1199);
        Assert.Equal(0.0, fall.Frame(0.1, false).OutgoingAlpha);
        Assert.Equal(600, fall.Frame(0.65, false).Shown);
    }

    [Fact]
    public void SmallChangesReplaceTheDigitsAndDashesCrossfade()
    {
        var start = OrbScoreMotion.Start(new OrbLineScore(1000, false, false));

        // +5 is under 1 % of 1,000; +2 is under 3.
        foreach (var to in new[] { 1005, 998 })
        {
            var small = start.Next(new OrbLineScore(to, false, false), 5);
            Assert.Equal(to, small.Frame(5, false).Shown);
            Assert.False(small.IsMoving(5, false));
        }

        // `–` ↔ a number crossfades in 0.15 s.
        var none = start.Next(OrbLineScore.None, 5);
        var half = none.Frame(5.05, false);
        Assert.Equal(((int?)null, 1000), (half.Shown, half.Outgoing));
        Assert.Equal(1, half.Alpha + half.OutgoingAlpha, 9);
        Assert.False(none.Frame(5 + PerfMotion.CrossfadeSeconds, false).OutgoingAlpha > 0);

        // The color goes pale with it over its own 0.2 s.
        Assert.True(none.IsMoving(5.19, false));
        Assert.False(none.IsMoving(5 + PerfMotion.FormSeconds, false));
        Assert.Equal(PerfNumber.Dash, OrbScoreMotion.Text(null));
        Assert.Equal("12.8k", OrbScoreMotion.Text(12_840));
    }

    [Fact]
    public void ACountInterruptedByANewValueStartsFromTheIntegerItReached()
    {
        var climbing = OrbScoreMotion.Start(new OrbLineScore(100, false, false)).Next(new OrbLineScore(900, false, false), 0);
        var reached = climbing.Frame(0.2, false).Shown!.Value;
        Assert.InRange(reached, 101, 899);

        var retarget = climbing.Next(new OrbLineScore(950, false, false), 0.2);
        Assert.Equal(reached, retarget.Frame(0.2, false).Shown);
        Assert.Equal(950, retarget.Frame(0.2 + PerfMotion.CountDuration(reached, 950), false).Shown);

        // A fall mid-climb counts down from the integer on display.
        var dropped = climbing.Next(new OrbLineScore(500, false, false), 0.2);
        Assert.Equal(reached, dropped.Frame(0.2, false).Shown);
        Assert.Equal(500, dropped.Frame(0.2 + PerfMotion.CountDuration(reached, 500), false).Shown);
    }

    [Fact]
    public void ReduceMotionCrossfadesEveryChangeAndKeepsTheColorFade()
    {
        var start = OrbScoreMotion.Start(new OrbLineScore(400, false, true));
        var rise = start.Next(new OrbLineScore(800, true, false), 3);
        var frame = rise.Frame(3 + PerfMotion.CrossfadeSeconds / 2, true);
        Assert.Equal((800, 400), (frame.Shown, frame.Outgoing));
        Assert.True(rise.IsMoving(3.1, true));
        Assert.Equal(800, rise.Frame(3 + PerfMotion.CrossfadeSeconds, true).Shown);

        // Live → resting still crossfades the color in 0.2 s; the provisional mark goes at once.
        Assert.Equal(0.5, rise.Frame(3.1, true).Pale, 9);
        Assert.Equal(0, rise.Frame(3, true).Mark);
        Assert.True(rise.IsMoving(3.19, true));
        Assert.False(rise.IsMoving(3.2, true));
    }

    [Fact]
    public void TheColorAndTheProvisionalMarkReverseFromWhereTheyStand()
    {
        var start = OrbScoreMotion.Start(new OrbLineScore(600, false, false));
        var resting = start.Next(new OrbLineScore(600, true, true), 0);
        Assert.Equal(0.5, resting.Frame(0.1, false).Pale, 9);
        Assert.Equal(0.4, resting.Frame(0.1, false).Mark, 9);

        var live = resting.Next(new OrbLineScore(600, false, false), 0.1);
        Assert.Equal(0.5, live.Frame(0.1, false).Pale, 9);
        Assert.Equal(0.4, live.Frame(0.1, false).Mark, 9);
        Assert.Equal(0.0, live.Frame(0.2, false).Pale, 9);
        Assert.Equal(0.0, live.Frame(0.2, false).Mark, 9);
        Assert.False(live.IsMoving(0.2, false));
        Assert.False(live.IsMoving(0.1 + 0.4 * PerfProvisionalMark.FadeSeconds + 1e-9, false));
    }

    [Fact]
    public void LiveAndRestingTakeTheNumberAndTheUnitTokens()
    {
        var p = ScoreSize(OrbStyle.Led);
        var orb = Showing(OrbStyle.Led, Scored(Score(860)));
        Assert.All(Digits(LinesOnly(orb, OrbStyle.Led), p), q => Assert.Equal(0.78, q.Color.W, 3));

        // Resting: the unit token (white 50 %), reached over a 0.2 s crossfade of the color.
        orb.SetLines(1, Scored(Score(860, PerfForm.Resting), generating: false, IntelligenceSums.Zero), true);
        orb.Render(1.1);
        Assert.All(Digits(LinesOnly(orb, OrbStyle.Led), p), q => Assert.Equal(0.64, q.Color.W, 3));
        Assert.Equal(FrameClass.Fps30, orb.Demand);
        RenderFor(orb, 1.1, 0.5);
        Assert.All(Digits(LinesOnly(orb, OrbStyle.Led), p), q => Assert.Equal(0.50, q.Color.W, 3));
        Assert.Equal(FrameClass.None, orb.Demand);

        // `–` takes the unit token as well.
        var none = Showing(OrbStyle.Led, Scored(PerfSetReading.None));
        Assert.All(Digits(LinesOnly(none, OrbStyle.Led), p), q => Assert.Equal(0.50, q.Color.W, 3));
    }

    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Reactor)]
    [InlineData(OrbStyle.Needle)]
    [InlineData(OrbStyle.Minimal)]
    public void AProvisionalScoreSitsOnDotsThatTakeNoWidth(OrbStyle style)
    {
        var settled = Showing(style, Scored(Score(9)));
        var orb = Showing(style, Scored(Score(9, provisional: true)));
        Assert.Empty(Dots(LinesOnly(settled, style)));
        Assert.Equal(Drawn(settled, style, 196).Select(l => l.Left), Drawn(orb, style, 196).Select(l => l.Left));

        var p = ScoreSize(style);
        var list = LinesOnly(orb, style);
        var digits = Digits(list, p);
        var left = digits[0].Rect.X + 2;
        var width = 0.56 * p;
        var baseline = Drawn(orb, style, 196)[0].Baseline;
        var mark = PerfProvisionalMark.Underline(p, width, 2);
        var dots = Dots(list);
        Assert.Equal(mark.Centers.Count, dots.Length);
        Assert.All(dots, dot =>
        {
            Assert.Equal(mark.Diameter / 2, dot.B.X, 4);
            Assert.InRange(dot.A.Y, baseline + 0.22 * p - 0.25 - 1e-4, baseline + 0.22 * p + 0.25 + 1e-4);
            Assert.Equal(Math.Round((dot.A.Y - dot.B.X) * 2), (dot.A.Y - dot.B.X) * 2, 3);
            Assert.Equal(Math.Round((dot.A.X - dot.B.X) * 2), (dot.A.X - dot.B.X) * 2, 3);
            Assert.Equal(0.78 * PerfProvisionalMark.Opacity, dot.Color.W, 3);
        });

        // From the number's left edge to its right edge, the dots spread evenly.
        Assert.Equal(left, dots[0].A.X - dots[0].B.X, 3);
        Assert.InRange(dots[^1].A.X + dots[^1].B.X, left + width - 0.5, left + width + 0.5);
    }

    [Fact]
    public void HighContrastDrawsTheDotsInTheTextColorAtFullStrengthAndThicker()
    {
        var orb = Showing(OrbStyle.Led, Scored(Score(340, provisional: true)), highContrast: true);
        var p = ScoreSize(OrbStyle.Led);
        var dots = Dots(LinesOnly(orb, OrbStyle.Led, highContrast: true));
        Assert.NotEmpty(dots);
        Assert.All(dots, dot =>
        {
            Assert.Equal(PerfProvisionalMark.Diameter(p, 2, highContrast: true) / 2, dot.B.X, 4);
            Assert.Equal((1f, 1f, 1f, 1f), (dot.Color.X, dot.Color.Y, dot.Color.Z, dot.Color.W));
        });
    }

    [Fact]
    public void TheProvisionalMarkFadesWithTheStateAndAtOnceUnderReduceMotion()
    {
        var orb = Showing(OrbStyle.Minimal, Scored(Score(600)));
        orb.SetLines(1, Scored(Score(600, provisional: true)), true);
        orb.Render(1 + PerfProvisionalMark.FadeSeconds / 2);
        Assert.Equal(0.78 * 0.7 * 0.5, Dots(LinesOnly(orb, OrbStyle.Minimal))[0].Color.W, 3);
        Assert.Equal(FrameClass.Fps30, orb.Demand);
        RenderFor(orb, 1.125, 0.5);
        Assert.Equal(FrameClass.None, orb.Demand);

        var reduced = Showing(OrbStyle.Minimal, Scored(Score(600)), reduceMotion: true);
        reduced.SetLines(1, Scored(Score(600, provisional: true)), true);
        reduced.Render(1.01);
        Assert.Equal(0.78 * 0.7, Dots(LinesOnly(reduced, OrbStyle.Minimal))[0].Color.W, 3);
        Assert.Equal(FrameClass.None, reduced.Demand);
    }

    [Fact]
    public void ACountAsksForThirtyFramesForItsDurationAndAStillScoreForNone()
    {
        var orb = Showing(OrbStyle.Minimal, Scored(Score(200, provisional: true)));
        Assert.Equal(FrameClass.None, orb.Demand);
        var still = orb.List.Fingerprint();
        orb.Render(1.5);
        Assert.Equal(still, orb.List.Fingerprint());

        Assert.True(orb.SetLines(2, Scored(Score(600, provisional: true)), true));
        orb.Render(2.02);
        Assert.Equal(FrameClass.Fps30, orb.Demand);
        var duration = PerfMotion.CountDuration(200, 600);
        orb.Render(2 + duration - 0.02);
        Assert.Equal(FrameClass.Fps30, orb.Demand);
        orb.Render(2 + duration + 0.01);
        Assert.Equal(FrameClass.None, orb.Demand);
        Assert.Equal(600, orb.LinesReading!.Value.Score.Frame(2 + duration, false).Shown);
    }

    [Fact]
    public void AWarmCountFrameWithItsProvisionalMarkAllocatesNothing()
    {
        var orb = Showing(OrbStyle.Led, Scored(Score(5000, provisional: true)), size: 240);
        orb.SetLines(1, Scored(Score(57_000, provisional: true)), true);
        var time = 1.0;
        orb.Render(time += 1 / 60.0);
        orb.Render(time += 1 / 60.0);
        AllocationProbe.AssertNone(() => orb.Render(time += 1 / 120.0));
    }

    [Fact]
    public void TheLiquidSplitsTheDotsAtItsSurfaceAndDrawsEachOnce()
    {
        var orb = Showing(OrbStyle.Liquid, Scored(Score(88, provisional: true)));
        orb.SetData(1, Redline * 1.1, HeatMath.RawTier(Redline * 1.1, Redline), SpeedTitles.Raw(Redline * 1.1, Redline),
            [SourceKind.Codex], "today", null);
        var time = RenderFor(orb, 1, 3);
        Assert.True(orb.List.HasLiquid);

        // The CPU surface is the shader's, which the model drives.
        var model = (LiquidModel)((LiquidDraw)orb.Style!).Model;
        var u = orb.List.Liquid;
        for (var i = 0; i <= 8; i++)
        {
            var x = -0.4 + 0.1 * i;
            var expected = u.Frame.Y + model.SurfaceY(x) * 196;
            Assert.InRange(u.SurfaceY(u.Frame.X + x * 196), expected - 0.02, expected + 0.02);
        }

        var ctx = orb.Context!;
        var reading = orb.LinesReading!.Value;
        var above = new OrbDrawList();
        var below = new OrbDrawList();
        foreach (var (list, isBelow) in new[] { (above, false), (below, true) })
        {
            list.Liquid = orb.List.Liquid;
            list.AddLiquid(0);
            orb.Lines.Add(list, ctx, orb.Scene, reading, OrbLinesPlacement.Liquid, 0, OrbLinesColors.Standard(ctx), OrbTextClip.Liquid(isBelow),
                yieldsToPill: false);
        }

        var p = OrbLinesPlacement.Liquid.ScoreScale * OrbLinesPlacement.Liquid.TextSize(196);
        var all = Dots(above).Concat(Dots(below)).ToArray();
        Assert.Equal(PerfProvisionalMark.Underline(p, 2 * 0.56 * p, 2).Centers.Count, all.Length);
        Assert.All(Dots(above), dot => Assert.True(dot.A.Y <= u.SurfaceY(dot.A.X)));
        Assert.All(Dots(below), dot => Assert.True(dot.A.Y > u.SurfaceY(dot.A.X)));
        Assert.True(time > 3);
    }
}
