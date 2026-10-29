using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Demo;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Boards.Gpu;
using AgentSpeed.Presentation.Boards.Live;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Titles;
using static AgentSpeed.Presentation.Tests.Boards.BoardGroupingTests;
using KeyedLayers = AgentSpeed.Presentation.Tests.Boards.BoardGaugeSceneTests.KeyedLayers;

namespace AgentSpeed.Presentation.Tests.Boards;

/// <summary>性能 on the boards (DESIGN 12; 2c, Live metrics): the hero's motion and provisional mark, the rows, the cards.</summary>
public class BoardPerfScoreTests
{
    private static readonly Localizer Zh = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    private static PerfSetReading Live(int shown, bool provisional = false, int count = 1) => new(PerfForm.Live, shown, provisional, count, null, null);

    private static PerfSetReading Resting(int shown, bool provisional = false, int count = 1) => new(PerfForm.Resting, shown, provisional, count, null, null);

    // ---- The hero's motion (DESIGN 12, Motion) ----

    [Fact]
    public void AChangeOfOnePercentAndThreeCountsWithTheArcAndTheMeterFollowingOnTheLogScale()
    {
        var score = new BoardHeroScore();
        score.Apply(Live(500), 0, animate: false, reduceMotion: false);
        score.Apply(Live(1000), 10, animate: true, reduceMotion: false);
        Assert.Equal(PerfChangeKind.Count, score.Change.Kind);

        // A doubling lands in 0.3 + 0.35 s.
        Assert.Equal(0.65, score.Change.Duration, 9);
        var last = 500;
        for (var t = 10.0; t < 10.65; t += 1 / 60.0)
        {
            var shown = score.Shown(t)!.Value;
            Assert.InRange(shown, last, 1000);
            last = shown;
            var sweep = score.Sweep(t);
            Assert.Equal(sweep.Settled, sweep.Changing, 9);
            Assert.InRange(sweep.First.At - PerfScale.Fraction(shown), -0.002, 0.002);
            Assert.Equal(BoardHeroScore.Lit(shown), score.Meter(t).From);
            Assert.True(score.Animating(t));
        }

        // Quick at first, settling into the final number; 1000 sits at the top of the sweep and lights 6 of 12.
        Assert.True(score.Shown(10.2) > 800);
        Assert.Equal(1000, score.Shown(10.65));
        Assert.False(score.Animating(10.66));
        Assert.Equal(0.5, score.Sweep(11).First.At, 9);
        Assert.Equal(6, BoardHeroScore.Lit(1000));
        Assert.Equal(12, BoardHeroScore.Lit(4000));
        Assert.Equal(0, BoardHeroScore.Lit(200));
        Assert.Equal(("1,000", "1,000"), (score.Text(11, compact: true), score.Text(11, compact: false)));
    }

    [Fact]
    public void AFallCountsDownTheSameWay()
    {
        var score = new BoardHeroScore();
        score.Apply(Live(2000), 0, false, false);
        score.Apply(Live(1000), 1, true, false);
        Assert.Equal((PerfChangeKind.Count, 0.65), (score.Change.Kind, Math.Round(score.Change.Duration, 9)));
        var mid = score.Shown(1.1)!.Value;
        Assert.InRange(mid, 1001, 1999);
        Assert.Null(score.Outgoing(1.1, compact: false));
        Assert.Equal(1000, score.Shown(1.65));
    }

    [Fact]
    public void SmallChangesReplaceTheDigitsAtOnce()
    {
        // +2 is under 3; +8 is under 1 % of 1,000.
        foreach (var to in new[] { 1002, 1008, 992 })
        {
            var score = new BoardHeroScore();
            score.Apply(Live(1000), 0, false, false);
            score.Apply(Live(to), 1, true, false);
            Assert.Equal(PerfChangeKind.Replace, score.Change.Kind);
            Assert.False(score.Animating(1));
            Assert.Equal(PerfNumber.Full(to), score.Text(1, compact: false));
            Assert.Null(score.Outgoing(1, compact: false));
        }
    }

    [Fact]
    public void GlassReadsTheCompactFormAndNeonAndTimingTheFullOne()
    {
        var score = new BoardHeroScore();
        score.Apply(Live(12_840), 0, false, false);
        Assert.Equal("12.8k", score.Text(0, compact: true));
        Assert.Equal("12,840", score.Text(0, compact: false));
        Span<char> text = stackalloc char[BoardHeroScore.MaxLength];
        Assert.Equal("12.8k", new string(text[..score.Format(text, 0, compact: true)]));
        Assert.Equal(0, score.FormatOutgoing(text, 0, compact: true));
    }

    [Fact]
    public void ADashAndANumberCrossfade()
    {
        var score = new BoardHeroScore();
        score.Apply(PerfSetReading.None, 0, false, false);
        Assert.Equal("–", score.Text(0, compact: true));
        Assert.Equal(default, score.Sweep(0));
        score.Apply(Live(1000), 1, true, false);
        Assert.Equal((PerfChangeKind.Crossfade, 0.15), (score.Change.Kind, score.Change.Duration));
        Assert.Equal("–", score.Outgoing(1.05, compact: true));
        var sweep = score.Sweep(1.05);
        Assert.Equal((0, 0.5), (sweep.Settled, sweep.Changing));
        Assert.Equal(0, sweep.First.Alpha);
        Assert.True(sweep.Second.Alpha > 0);
    }

    [Fact]
    public void ReduceMotionCrossfadesEveryChangeAndShowsTheProvisionalMarkAtOnce()
    {
        var score = new BoardHeroScore();
        score.Apply(Live(500), 0, false, true);
        score.Apply(Live(1000, provisional: true), 1, true, true);
        Assert.Equal((PerfChangeKind.Crossfade, 0.15), (score.Change.Kind, score.Change.Duration));
        Assert.Equal(1, score.Proof(1));

        // Growing under Reduce Motion: the new stretch fades in over the old arc.
        var sweep = score.Sweep(1.05);
        Assert.Equal((PerfScale.Fraction(500), 0.5), (sweep.Settled, sweep.Changing));
        Assert.Equal(score.Fade(1.05).Incoming, sweep.ChangingAlpha, 9);
        Assert.False(score.Animating(1.16));
    }

    [Fact]
    public void LiveAndRestingCrossfadeTheColorEvenWithReduceMotion()
    {
        foreach (var reduce in new[] { false, true })
        {
            var score = new BoardHeroScore();
            score.Apply(Live(700), 0, false, reduce);
            Assert.Equal(1, score.Liveness(0));
            score.Apply(Resting(700), 1, true, reduce);
            Assert.InRange(score.Liveness(1.1), 0.01, 0.99);
            Assert.True(score.Animating(1.1));
            Assert.Equal(0, score.Liveness(1.2));
            Assert.False(score.Animating(1.21));
        }
    }

    [Fact]
    public void TheProvisionalMarkFadesInAndOutWithTheState()
    {
        var score = new BoardHeroScore();
        score.Apply(Live(700), 0, false, false);
        Assert.Equal(0, score.Proof(0));
        score.Apply(Live(700, provisional: true), 1, true, false);
        Assert.InRange(score.Proof(1.1), 0.01, 0.99);
        Assert.Equal(1, score.Proof(1.25));
        score.Apply(Live(700), 2, true, false);
        Assert.InRange(score.Proof(2.1), 0.01, 0.99);
        Assert.Equal(0, score.Proof(2.25));
    }

    // ---- Drawing (DESIGN 12, Provisional mark; 2c, Sub-gauge) ----

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(2.0, false)]
    [InlineData(1.5, true)]
    public void TheUnderlineSpreadsItsDotsAsTheProvisionalMarkRuleSays(double scale, bool highContrast)
    {
        var list = new BoardDrawList { Scale = scale };
        var color = new Rgba(1, 1, 1, 0.9);
        BoardScoreDraw.Underline(list, 10, 30, 50, 19, color, 1, highContrast);
        var d = PerfProvisionalMark.Diameter(19, scale, highContrast);
        var centers = PerfProvisionalMark.UnderlineCenters(30, d);
        var dots = list.Instances.ToArray();
        Assert.Equal(centers.Count, dots.Length);
        Assert.Equal(centers.Count, BoardScoreDraw.UnderlineCount(30, d));
        for (var i = 0; i < dots.Length; i++)
        {
            Assert.Equal(OrbShape.Circle, (OrbShape)(int)dots[i].A.W);
            Assert.Equal(d / 2, dots[i].B.X, 4);

            // On the device pixel grid, within half a pixel of the rule's place.
            Assert.InRange(dots[i].A.X - (10 + centers[i]), -0.5 / scale - 1e-4, 0.5 / scale + 1e-4);
            Assert.InRange(dots[i].A.Y - (50 + 0.22 * 19), -0.5 / scale - 1e-4, 0.5 / scale + 1e-4);
            Assert.Equal(0.9 * (highContrast ? 1 : 0.7), dots[i].Color.W, 4);
        }
    }

    [Fact]
    public void AProvisionalGlassHeroDrawsDotsAHollowTipAndTheUnderline()
    {
        var (scene, list, _) = Hero(BoardStyle.Glass, Live(600, provisional: true));
        var spec = BoardGaugeSpec.For(BoardStyle.Glass, BoardSize.M);
        var (cx, cy) = DialCenter(list, spec);
        var instances = list.Instances.ToArray();
        var dots = instances.Where(i => IsArcDot(i, spec, cx, cy)).ToArray();
        var length = BoardGaugeSpec.ArcSweep * Math.PI / 180 * spec.ArcRadius;
        Assert.Equal((int)Math.Floor(PerfScale.Fraction(600) * length / (2 * spec.ArcStroke) + 1e-6) + 1, dots.Length);

        // No solid value arc: only the track's band arc at the dial.
        Assert.Single(instances, i => Is(i, OrbShape.BandArc) && Near(i, cx, cy) && i.Extra.X > 0);
        var ring = Assert.Single(instances, i => Is(i, OrbShape.Ring) && i.Extra.X > 0 && Math.Abs(Distance(i, cx, cy) - spec.ArcRadius) < 1e-2);
        Assert.True(ring.B.Y >= BoardScoreDraw.TipRingShare * spec.TipRadius - 1e-4);
        Assert.Equal(spec.TipRadius, ring.B.X + ring.B.Y / 2, 3);

        // The digits' dotted underline, 0.22 × the size under their baseline.
        var baseline = BoardCanvas.Margin + scene.Metrics.HeroTop + Placement(scene).MainBaseline;
        Assert.True(instances.Count(i => Is(i, OrbShape.Circle) && Math.Abs(i.A.Y - (baseline + 0.22 * spec.MainSize)) <= 0.51) >= 2);
    }

    [Fact]
    public void GlassMarksTheReferenceAtTheTopOfTheSweepWhateverTheScore()
    {
        foreach (var score in new[] { Live(600), Live(4000), PerfSetReading.None })
        {
            var (_, list, _) = Hero(BoardStyle.Glass, score);
            var spec = BoardGaugeSpec.For(BoardStyle.Glass, BoardSize.M);
            var (cx, cy) = DialCenter(list, spec);

            // A radial line at 270°: straight above the centre, from 1 inside the track's inner edge, 0.05 D long, 1 wide.
            var outer = spec.ArcRadius - spec.ArcStroke / 2 - 1;
            var tick = Assert.Single(list.Instances.ToArray(), i => Is(i, OrbShape.Capsule) && Math.Abs(i.A.X - cx) < 1e-3 && i.A.Y < cy &&
                Math.Abs(i.B.X - 0.05 * spec.Dial) < 1e-3);
            Assert.Equal(cy - outer + 0.025 * spec.Dial, tick.A.Y, 3);
            Assert.Equal(1, tick.B.Y, 6);
        }
    }

    [Fact]
    public void AnEmptyDialDrawsNoDotAfterAProvisionalScoreLeavesAndTheLowestDrawsOnlyItsRing()
    {
        var spec = BoardGaugeSpec.For(BoardStyle.Glass, BoardSize.M);
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = BoardReadouts.All };
        var scene = new BoardScene(BoardStyle.Glass, BoardSize.M, 3);
        scene.Configure(BoardStyle.Glass, BoardSize.M, new BoardEffectsPolicy(Settings.EffectsIntensity.Off));
        var tick = feed.Step(BoardStyle.Glass, BoardSize.M) with { Events = [], NewPeak = false };
        scene.Apply(tick with { Gauge = tick.Gauge with { Score = Live(600, provisional: true) } }, 0);
        scene.Settle();
        scene.Apply(tick with { Gauge = tick.Gauge with { Score = PerfSetReading.None } }, 1);
        var list = new BoardDrawList();
        scene.Render(1.05, new KeyedLayers(), list);
        var (cx, cy) = DialCenter(list, spec);

        // While it fades out, the outgoing arc keeps its dots.
        Assert.Contains(list.Instances.ToArray(), i => IsArcDot(i, spec, cx, cy));
        list.Reset();
        scene.Render(3, new KeyedLayers(), list);
        Assert.DoesNotContain(list.Instances.ToArray(), i => IsArcDot(i, spec, cx, cy) || Is(i, OrbShape.Ring));

        // At or under 250 the arc is empty (f = 0): the ring alone marks the start.
        var (_, low, _) = Hero(BoardStyle.Glass, Live(200, provisional: true));
        Assert.DoesNotContain(low.Instances.ToArray(), i => IsArcDot(i, spec, cx, cy));
        Assert.Single(low.Instances.ToArray(), i => Is(i, OrbShape.Ring) && i.Extra.X > 0 && Math.Abs(Distance(i, cx, cy) - spec.ArcRadius) < 1e-2);
    }

    [Fact]
    public void ASettledGlassHeroDrawsTheSolidArcAndTipWithoutAMark()
    {
        var (scene, list, _) = Hero(BoardStyle.Glass, Live(600));
        var spec = BoardGaugeSpec.For(BoardStyle.Glass, BoardSize.M);
        var (cx, cy) = DialCenter(list, spec);
        var instances = list.Instances.ToArray();

        // The track, the glow and the gradient pieces.
        Assert.True(instances.Count(i => Is(i, OrbShape.BandArc) && Near(i, cx, cy)) >= 3);
        Assert.DoesNotContain(instances, i => Is(i, OrbShape.Ring) && Near(i, cx, cy, spec.ArcRadius + 1));
        var baseline = BoardCanvas.Margin + scene.Metrics.HeroTop + Placement(scene).MainBaseline;
        Assert.DoesNotContain(instances, i => Is(i, OrbShape.Circle) && Math.Abs(i.A.Y - (baseline + 0.22 * spec.MainSize)) <= 0.51);
    }

    [Fact]
    public void ARestingGlassArcDrawsAtThirtyFivePercentWithoutGlow()
    {
        var (_, live, _) = Hero(BoardStyle.Glass, Live(600));
        var (_, resting, _) = Hero(BoardStyle.Glass, Resting(600));
        var spec = BoardGaugeSpec.For(BoardStyle.Glass, BoardSize.M);
        var (cx, cy) = DialCenter(resting, spec);
        var pieces = resting.Instances.ToArray().Where(i => Is(i, OrbShape.BandArc) && Near(i, cx, cy)).Skip(1).ToArray();
        Assert.NotEmpty(pieces);
        Assert.All(pieces, p => Assert.True(p.Extra.X > 0 && p.Extra.Y == 0, "no glow"));
        var livePieces = live.Instances.ToArray().Where(i => Is(i, OrbShape.BandArc) && Near(i, cx, cy) && i.Extra.X > 0).Skip(1).ToArray();
        Assert.Equal(0.35, pieces[0].Color.W / livePieces[0].Color.W, 3);
    }

    [Fact]
    public void HighContrastKeepsARestingArcApartFromItsTrack()
    {
        var (_, list, _) = Hero(BoardStyle.Glass, Resting(600), highContrast: true);
        var spec = BoardGaugeSpec.For(BoardStyle.Glass, BoardSize.M);
        var (cx, cy) = DialCenter(list, spec);
        var arcs = list.Instances.ToArray().Where(i => Is(i, OrbShape.BandArc) && Near(i, cx, cy)).ToArray();
        var track = arcs[0];
        Assert.All(arcs.Skip(1), a => Assert.True(a.Color.W >= track.Color.W + 0.3, $"{a.Color.W} vs {track.Color.W}"));
        Assert.DoesNotContain(list.Instances.ToArray(), i => i.Extra.Y > 0 && Near(i, cx, cy, spec.ArcRadius + 4));
    }

    [Fact]
    public void AProvisionalNeonHeroDrawsItsLitSegmentsHollow()
    {
        var (_, list, _) = Hero(BoardStyle.Neon, Live(600, provisional: true));
        var instances = list.Instances.ToArray();
        var hollow = instances.Where(i => Is(i, OrbShape.RoundedRing) && Math.Abs(i.B.Y - (1.5 - PerfProvisionalMark.HollowInset)) < 1e-3).ToArray();
        Assert.Equal(BoardHeroScore.Lit(600), hollow.Length);
        Assert.All(hollow, h => Assert.Equal(PerfProvisionalMark.HollowStroke, h.Extra.Z, 3));

        // No filled lit segment, no glow on the meter.
        var segments = instances.Where(i => Is(i, OrbShape.RoundedRect) && Math.Abs(i.B.Y - 1.5) < 1e-3).ToArray();
        Assert.Equal(12 - BoardHeroScore.Lit(600), segments.Length);
        Assert.All(segments, s => Assert.Equal(0, s.Extra.Y));
    }

    [Fact]
    public void ARestingNeonMeterLightsAtThirtyFivePercentWithoutGlow()
    {
        var (_, live, _) = Hero(BoardStyle.Neon, Live(600));
        var (_, resting, _) = Hero(BoardStyle.Neon, Resting(600));
        RoundedRect(live, out var liveLit);
        RoundedRect(resting, out var restLit);
        Assert.Equal(BoardHeroScore.Lit(600), restLit.Length);
        Assert.All(restLit, s => Assert.Equal(0, s.Extra.Y));
        Assert.Equal(0.35, restLit[0].Color.W / liveLit[0].Color.W, 3);

        static void RoundedRect(BoardDrawList list, out OrbGpuInstance[] lit)
        {
            var mind = BoardGaugePalette.For(BoardStyle.Neon, BoardPalette.For(BoardStyle.Neon, true)).Mind;
            lit = [.. list.Instances.ToArray().Where(i => Is(i, OrbShape.RoundedRect) && Math.Abs(i.B.Y - 1.5) < 1e-3 &&
                Math.Abs(i.Color.X - mind.R) < 1e-3 && Math.Abs(i.Color.Z - mind.B) < 1e-3)];
        }
    }

    [Fact]
    public void AProvisionalTimingHeroUnderlinesItsDigits()
    {
        var (scene, list, _) = Hero(BoardStyle.Timing, Live(9, provisional: true));
        var spec = BoardGaugeSpec.For(BoardStyle.Timing, BoardSize.M);
        var baseline = BoardCanvas.Margin + scene.Metrics.HeroTop + Placement(scene).MainBaseline;
        var dots = list.Instances.ToArray().Where(i => Is(i, OrbShape.Circle) && Math.Abs(i.A.Y - (baseline + 0.22 * spec.MainSize)) <= 0.51).ToArray();
        Assert.Equal(2, dots.Length);
        Assert.NotNull(scene.Gauge.ScoreArea);
    }

    [Theory]
    [InlineData(BoardStyle.Glass)]
    [InlineData(BoardStyle.Neon)]
    [InlineData(BoardStyle.Timing)]
    public void TheHerosScoreAreaIsWhereHoveringOpensItsCard(BoardStyle style)
    {
        var (scene, _, _) = Hero(style, Live(600));
        var area = scene.Gauge.ScoreArea!.Value;
        var spec = BoardGaugeSpec.For(style, BoardSize.M);
        var placement = Placement(scene);
        Assert.Equal(BoardCanvas.Margin + scene.Metrics.HeroTop + placement.Top, area.Top, 3);
        Assert.Equal(style == BoardStyle.Timing ? placement.MainRow : spec.MainHeight, area.Bottom - area.Top, 3);
        Assert.True(scene.HitScore((area.Left + area.Right) / 2, (area.Top + area.Bottom) / 2));
        Assert.False(scene.HitScore(area.Left - 1, area.Top - 1));
        // With 性能 off there is nothing to hover.
        var off = Hero(style, Live(600), readouts: BoardReadouts.All with { Intelligence = false }).Scene;
        Assert.Null(off.Gauge.ScoreArea);
        Assert.False(off.HitScore((area.Left + area.Right) / 2, (area.Top + area.Bottom) / 2));
    }

    // ---- Frames (DESIGN 2c, Frame policy) ----

    [Fact]
    public void ACountRendersAtSixtyForItsDurationOnlyAndAnIdleBoardRendersNothing()
    {
        var scene = new BoardScene(BoardStyle.Glass, BoardSize.M, 3);
        scene.Configure(BoardStyle.Glass, BoardSize.M, new BoardEffectsPolicy(Settings.EffectsIntensity.Off));
        var layers = new KeyedLayers();
        var list = new BoardDrawList();
        scene.Apply(Quiet(Resting(400)), 0);
        scene.Settle();
        Render(scene, layers, list, 0, 0.2);
        Assert.Equal(FrameClass.None, scene.Demand(0.2));
        scene.Apply(Quiet(Live(800)), 1);
        Assert.Equal(FrameClass.Fps60, scene.Demand(1.0));
        Render(scene, layers, list, 1, 0.6);
        Assert.Equal(FrameClass.Fps60, scene.Demand(1.62));
        Assert.Equal(FrameClass.None, scene.Demand(1.68));

        // A tick that changes nothing renders nothing new.
        scene.Apply(Quiet(Live(800)), 2);
        Assert.Equal(FrameClass.None, scene.Demand(2));
    }

    [Theory]
    [InlineData(BoardStyle.Glass)]
    [InlineData(BoardStyle.Neon)]
    [InlineData(BoardStyle.Timing)]
    public void CountAndCrossfadeFramesAllocateNothing(BoardStyle style)
    {
        var scene = new BoardScene(style, BoardSize.M, 3);
        scene.Configure(style, BoardSize.M, new BoardEffectsPolicy(Settings.EffectsIntensity.Off));
        var layers = new PlainLayers();
        var list = new BoardDrawList();
        BoardTick At(int? hero, double row)
        {
            var agent = Agent("a", 120) with { Intelligence = BoardLiveMetricsTests.Scored(row, PerfForm.Live, true, "a") };
            var tick = RowsTick([agent], style: style);
            return tick with { Gauge = tick.Gauge with { Score = hero is { } h ? Live(h, provisional: true) : PerfSetReading.None } };
        }

        void Frames(double from)
        {
            for (var t = from; t < from + 0.8; t += 1 / 60.0)
            {
                list.Reset();
                scene.Render(t, layers, list);
            }
        }

        scene.Apply(At(300, 800), 0);
        scene.Settle();
        scene.Apply(At(12_000, 600), 1);
        Frames(1);
        scene.Apply(At(null, 800), 2);
        scene.Apply(At(95_000, 500), 2.001);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Frames(2.001);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ARefreshedHoverTestsWhereTheRowsAreHeading()
    {
        var a = Agent("a", 300);
        var b = Agent("b", 100);
        var scene = new BoardScene(BoardStyle.Glass, BoardSize.M, 3);
        scene.Configure(BoardStyle.Glass, BoardSize.M, new BoardEffectsPolicy(Settings.EffectsIntensity.Off));
        scene.Apply(RowsTick([a, b]), 0);
        scene.Settle();
        var first = scene.Rows.Single(r => r.Key == "a");
        var x = BoardCanvas.Margin + scene.Metrics.ListLeft + 20;
        var y = BoardCanvas.Margin + scene.Metrics.ListTop + first.Y + 4;
        // b passes a: right after the tick the rows have not moved yet, but b is heading for the first place.
        scene.Apply(RowsTick([b with { Live = new Rate(900, 3600) }, a]), 1);
        Assert.Equal("a", scene.HitRow(x, y)!.Key);
        Assert.Equal("b", scene.HitRow(x, y, atRest: true)!.Key);
    }

    // ---- Rows (DESIGN 2c, Ranking rows) ----

    [Fact]
    public void AProvisionalRowUnderlinesItsDigitsInsideTheRow()
    {
        var root = Agent("a", 120) with { Intelligence = BoardLiveMetricsTests.Scored(86, PerfForm.Live, true, "a") };
        var (scene, list, tick) = Rows([root]);
        var row = scene.Rows.Single(r => r.Key == "a");
        Assert.Equal(1, row.IntelProof(100));
        var top = BoardCanvas.Margin + scene.Metrics.ListTop + row.Y;

        // A two-line row's 性能 is 13 pt on the figures' centred baseline.
        var size = BoardRowColumns.IntelligenceSize(BoardStyle.Glass, sub: false, twoLine: true);
        Assert.Equal(13, size);
        var d = PerfProvisionalMark.Diameter(size, 1);
        var dots = list.Instances.ToArray().Where(i => Is(i, OrbShape.Circle) && Math.Abs(i.B.X - d / 2) < 1e-4 &&
            Math.Abs(i.A.Y - (top + scene.FigureBaseline(row.Entry) + 0.22 * size)) <= 0.51).ToArray();

        // "86" is 14 wide in the test's glyphs: n = ⌊(14 − 1) / 2.4⌋ + 1.
        Assert.Equal(6, dots.Length);
        var right = BoardCanvas.Margin + scene.Metrics.ListLeft + scene.Metrics.ListWidth - scene.Columns.IntelligenceRight;
        Assert.All(dots, dot => Assert.InRange(dot.A.X, right - 14 - 0.5, right + 0.5));
        Assert.True(tick.Entries[0].Readout!.IsProvisional);
    }

    [Fact]
    public void RowDigitsChangeInPlaceUpOrDownAndADashCrossfades()
    {
        var root = Agent("a", 120);
        var scene = new BoardScene(BoardStyle.Glass, BoardSize.M, 3);
        scene.Configure(BoardStyle.Glass, BoardSize.M, new BoardEffectsPolicy(Settings.EffectsIntensity.Off));
        BoardTick At(double score, bool provisional = false) =>
            RowsTick([root with { Intelligence = BoardLiveMetricsTests.Scored(score, PerfForm.Live, provisional, "a") }]);
        scene.Apply(At(400), 0);
        scene.Settle();
        var row = scene.Rows.Single(r => r.Key == "a");

        // No count on rows: a climb or a fall replaces the digits, whatever its size; from 10,000 the compact form.
        scene.Apply(At(800), 1);
        Assert.Equal(("800", (string?)null), (row.IntelText, row.IntelOutgoing(1.05)));
        scene.Apply(At(12_840), 2);
        Assert.Equal(("12.8k", (string?)null), (row.IntelText, row.IntelOutgoing(2.05)));
        scene.Apply(At(600), 3);
        Assert.Equal(("600", (string?)null), (row.IntelText, row.IntelOutgoing(3.05)));

        // Reduce Motion: every change is a 0.15 s crossfade.
        scene.Configure(BoardStyle.Glass, BoardSize.M, new BoardEffectsPolicy(Settings.EffectsIntensity.Off, ReduceMotion: true));
        scene.Apply(At(700), 4);
        Assert.Equal("600", row.IntelOutgoing(4.1));
        Assert.InRange(row.IntelFade(4.1).Outgoing, 0.01, 0.99);
        Assert.Null(row.IntelOutgoing(4.16));
        Assert.False(row.IntelOutgoingProvisional);

        // A provisional score going to none crossfades with `–`; its underline fades out with its digits.
        scene.Configure(BoardStyle.Glass, BoardSize.M, new BoardEffectsPolicy(Settings.EffectsIntensity.Off));
        scene.Apply(At(800, provisional: true), 5);
        scene.Apply(RowsTick([root]), 6);
        Assert.Equal(("–", "800", true, false), (row.IntelText, row.IntelOutgoing(6.05), row.IntelOutgoingProvisional, row.IntelHasScore));
        Assert.Null(row.IntelOutgoing(6.16));
    }

    // ---- Score cards (DESIGN 2c, Tooltip and score card) ----

    [Fact]
    public void AGroupRowsCardIsItsSupplyingMembersAndNoneIsTheRoots()
    {
        var root = Agent("r", 120) with { Intelligence = BoardLiveMetricsTests.Scored(400, PerfForm.Live, false, "r") };
        var sub = Agent("s", 60, "r") with { Intelligence = BoardLiveMetricsTests.Scored(860, PerfForm.Live, true, "s") };
        var tick = RowsTick([root, sub], BoardSize.L);
        var group = tick.Entries.First(e => e.Kind == BoardEntryKind.Group);
        var card = BoardScoreCards.Row(group, tick.Metric, Zh)!;
        Assert.Equal("s", card.AgentId);
        Assert.Equal(sub.Project, card.Subject.Project);
        Assert.Equal(1, card.Subject.SubAgents);
        Assert.Equal(860, card.Subject.Evaluation!.Shown);
        Assert.True(card.Subject.Evaluation.Provisional);
        Assert.Equal(0, card.Subject.TopCount);
        Assert.Equal(Zh.Format("win.boards.tip.speedSubs", SpeedFormat.Speed(group.Group.Live), SpeedFormat.Speed(group.Group.SubLive)),
            card.Subject.FactLines![^1]);
        Assert.Equal("s", BoardScoreCards.For(tick, "r", Zh)!.AgentId);

        var subRow = tick.Entries.First(e => e.Kind == BoardEntryKind.Sub);
        var own = BoardScoreCards.Row(subRow, tick.Metric, Zh)!;
        Assert.Equal(("s", 0), (own.AgentId, own.Subject.SubAgents));
        Assert.Equal([Zh.Format("win.boards.tip.speed", SpeedFormat.Speed(60))], own.Subject.FactLines);

        var bare = RowsTick([Agent("n", 5)]);
        var none = BoardScoreCards.Row(bare.Entries[0], bare.Metric, Zh)!;
        Assert.Equal(("n", (int?)null), (none.AgentId, none.Subject.Evaluation?.Shown));
    }

    [Fact]
    public void TheHerosCardNamesTheHighestOfNAndNothingOpensWhileNone()
    {
        var a = Agent("a", 120) with { Intelligence = BoardLiveMetricsTests.Scored(1540, PerfForm.Resting, false, "a") };
        var b = Agent("b", 60) with { Intelligence = BoardLiveMetricsTests.Scored(550, PerfForm.Resting, false, "b") };
        var tick = RowsTick([a, b]);
        var card = BoardScoreCards.For(tick, BoardScoreCards.HeroKey, Zh)!;
        Assert.Equal(("a", 2), (card.AgentId, card.Subject.TopCount));

        // Resting with N ≥ 2: 閒置 · 2 個 agent 中最高.
        Assert.Equal(Zh["phase.idle"] + " · " + Zh.Format("perf.card.top", 2), PerfCard.Build(card.Subject, Zh).Eyebrow);

        Assert.Null(BoardScoreCards.For(RowsTick([Agent("n", 5)]), BoardScoreCards.HeroKey, Zh));
        Assert.Null(BoardScoreCards.For(tick with { Readouts = BoardReadouts.All with { Intelligence = false } }, BoardScoreCards.HeroKey, Zh));
        Assert.Null(BoardScoreCards.For(tick with { Readouts = BoardReadouts.All with { Intelligence = false } }, "a", Zh));
        Assert.Null(BoardScoreCards.For(tick, "missing", Zh));
    }

    [Fact]
    public void ARowsHelpTextIsItsCardWhileIntelligenceShows()
    {
        var root = Agent("r", 120) with { Intelligence = BoardLiveMetricsTests.Scored(912, PerfForm.Live, true, "r") };
        var tick = RowsTick([root]);
        var group = tick.Entries[0];
        var help = BoardScoreCards.Description(tick, group, Zh);
        Assert.StartsWith("性能 912，初步（樣本少）", help, StringComparison.Ordinal);
        Assert.Contains(Zh["perf.card.footnote"], help, StringComparison.Ordinal);
        Assert.Empty(BoardScoreCards.Description(tick with { Readouts = BoardReadouts.All with { Intelligence = false } }, group, Zh));
    }

    // ---- Helpers ----

    private static bool Is(in OrbGpuInstance i, OrbShape shape) => (OrbShape)(int)i.A.W == shape;

    private static double Distance(in OrbGpuInstance i, double x, double y) => Math.Sqrt(Math.Pow(i.A.X - x, 2) + Math.Pow(i.A.Y - y, 2));

    private static bool Near(in OrbGpuInstance i, double x, double y, double within = 1e-3) => Distance(i, x, y) <= within;

    // A dot of glass's provisional arc: a circle one stroke wide on the arc's radius, not a glow.
    private static bool IsArcDot(in OrbGpuInstance i, BoardGaugeSpec spec, double cx, double cy) =>
        Is(i, OrbShape.Circle) && Math.Abs(i.B.X - spec.ArcStroke / 2) < 1e-3 && i.Extra.X > 0 && Math.Abs(Distance(i, cx, cy) - spec.ArcRadius) < 1e-2;

    // The dial's centre: the track, the only band arc of its radius spanning the whole sweep.
    private static (double X, double Y) DialCenter(BoardDrawList list, BoardGaugeSpec spec)
    {
        var track = list.Instances.ToArray().First(i => Is(i, OrbShape.BandArc) && Math.Abs(i.B.X - spec.ArcRadius) < 1e-3 &&
            Math.Abs(i.Extra.W - BoardGaugeSpec.ArcSweep * Math.PI / 180) < 1e-3);
        return (track.A.X, track.A.Y);
    }

    private static BoardGaugePlacement Placement(BoardScene scene) =>
        BoardGaugeLayout.Place(scene.Style, scene.Size, scene.Readouts, scene.Gauge.Cells(scene.Style, scene.Size, scene.Readouts, new KeyedLayers(), 100),
            scene.Hero.Width, scene.Hero.Height, scene.Tick!.ThinkingHidden ? 1 : 0, scene.Tick.ThinkingHidden ? 0 : 1);

    // A board after one tick of the team story with its hero's 性能 replaced, rendered at rest.
    private static (BoardScene Scene, BoardDrawList List, BoardTick Tick) Hero(BoardStyle style, PerfSetReading score, bool highContrast = false,
        BoardReadouts? readouts = null)
    {
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = readouts ?? BoardReadouts.All };
        var scene = new BoardScene(style, BoardSize.M, 3);
        scene.Configure(style, BoardSize.M, new BoardEffectsPolicy(ReduceMotion: true, HighContrast: highContrast));
        var tick = feed.Step(style, BoardSize.M);
        tick = tick with { Gauge = tick.Gauge with { Score = score } };
        scene.Apply(tick, 0);
        scene.Settle();
        var list = new BoardDrawList();
        scene.Render(100, new KeyedLayers(), list);
        return (scene, list, tick);
    }

    // A glass board at M whose rows are these agents' groups, worded with every readout on, rendered at rest.
    private static (BoardScene Scene, BoardDrawList List, BoardTick Tick) Rows(IReadOnlyList<AgentStatus> agents)
    {
        var tick = RowsTick(agents);
        var scene = new BoardScene(BoardStyle.Glass, BoardSize.M, 3);
        scene.Configure(BoardStyle.Glass, BoardSize.M, new BoardEffectsPolicy(ReduceMotion: true));
        scene.Apply(tick, 0);
        scene.Settle();
        var list = new BoardDrawList();
        scene.Render(100, new KeyedLayers(), list);
        return (scene, list, tick);
    }

    private static BoardTick RowsTick(IReadOnlyList<AgentStatus> agents, BoardSize size = BoardSize.M, BoardStyle style = BoardStyle.Glass)
    {
        var texts = new BoardTexts(Zh, TitleTheme.Vehicles);
        var groups = BoardGrouping.Group(agents, SpeedMetric.Output);
        var entries = BoardReadoutRows.Word(BoardLayoutPlan.Arrange(groups, BoardSizes.Limits(size), BoardMetrics.For(style, size, BoardReadouts.All),
            SpeedMetric.Output), BoardReadouts.All, texts);
        var total = groups.Sum(g => g.Live);
        return new BoardTick(total, SpeedFormat.Speed(total), SpeedTier.Cruise, 2000, SpeedMetric.Output, false, entries, [], false)
        {
            Readouts = BoardReadouts.All,
            Gauge = texts.Hero(Core.Intelligence.IntelligenceSums.Zero, BoardFeed.Highest(agents), CostReading.Zero),
        };
    }

    private static BoardTick Quiet(PerfSetReading score) => new(0, "–", SpeedTier.Idle, 2000, SpeedMetric.Output, false, [], [], false)
    {
        Readouts = BoardReadouts.All,
        Gauge = BoardHeroReadouts.Empty with { Score = score },
    };

    private static void Render(BoardScene scene, IBoardLayers layers, BoardDrawList list, double from, double seconds)
    {
        for (var t = from; t <= from + seconds; t += 1 / 60.0)
        {
            list.Reset();
            scene.Render(t, layers, list);
        }
    }

    /// <summary>Every layer exists, glyphs with a baseline; looking one up allocates nothing.</summary>
    private sealed class PlainLayers : IBoardLayers
    {
        public bool TryGet(string key, out BoardLayerSlot slot)
        {
            slot = key.StartsWith("#v", StringComparison.Ordinal)
                ? new BoardLayerSlot(0, 0, 0.1f, 0.1f, 11, 24, Baseline: 16)
                : new BoardLayerSlot(0, 0, 0.5f, 0.1f, 40, 16, Baseline: 12);
            return true;
        }
    }
}
