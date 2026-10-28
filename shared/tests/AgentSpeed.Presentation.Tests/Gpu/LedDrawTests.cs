using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Gpu;

public class LedDrawTests
{
    private static LedMinimalReactorHarness Create(double size = 148, bool dark = true, EffectsIntensity level = EffectsIntensity.Standard,
        bool reduceMotion = false, bool highContrast = false, int seed = 7) =>
        new(OrbStyle.Led, size, dark, level, reduceMotion, highContrast, seed);

    [Theory]
    [MemberData(nameof(LedMinimalReactorHarness.Matrix), MemberType = typeof(LedMinimalReactorHarness))]
    public void EveryFrameOfTheScenarioIsValid(bool dark, bool highContrast, EffectsIntensity level, double size)
    {
        var most = Create(size, dark, level, highContrast: highContrast).CheckScenario(LedDraw.Budget);
        Assert.InRange(most, 36, LedDraw.Budget);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReduceMotionScenarioIsValid(bool dark)
    {
        var harness = Create(dark: dark, level: EffectsIntensity.Max, reduceMotion: true);
        harness.CheckScenario(LedDraw.Budget);
    }

    [Fact]
    public void IdleDrawsTheSilkTheUnlitSegmentsAndTicksOnly()
    {
        var harness = Create();
        harness.Run(0.5);
        Assert.Equal(40, harness.List.Count);
        Assert.Equal(2, harness.List.BatchCount);
        Assert.Equal(new OrbBatch(OrbPass.Led, OrbBlend.Over, 0, 0, 0, 0, LedDraw.SilkMode), harness.List.Batches[0]);
    }

    [Fact]
    public void SignOfLifeLiftsTheUnlitSegments()
    {
        var harness = Create();
        var brightest = 0f;
        harness.Run(3.2, list =>
        {
            for (var i = 0; i < 36; i++)
            {
                brightest = Math.Max(brightest, list.Instances[i].Color.W);
            }
        });
        Assert.InRange(brightest, 0.2f, 0.25f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThumbnailsDrawTheSegmentsWithoutBloom(bool dark)
    {
        var harness = Create(48, dark, EffectsIntensity.Max);
        harness.Run(0.3);
        Assert.Equal(36, harness.List.Count);
        harness.Speed(1.5);
        harness.Run(2, list =>
        {
            Assert.InRange(list.Count, 36, 37);
            foreach (var instance in list.Instances)
            {
                Assert.Equal(OrbShape.RoundedRect, instance.Shape);
                Assert.Equal(0f, instance.B.W);
            }
        });
    }

    [Fact]
    public void RedlineAtMaxAddsTheArcGlowHazeEdgeTwoCometsSparksEmbersAndArcs()
    {
        var harness = Create(240, level: EffectsIntensity.Max);
        harness.Speed(2);
        harness.Run(2.5);
        var counts = new int[Enum.GetValues<OrbShape>().Length];
        var additive = 0;
        var modes = new HashSet<int>();
        foreach (var batch in harness.List.Batches)
        {
            additive += batch.Blend == OrbBlend.Additive ? batch.Count : 0;
            if (batch.Pass == OrbPass.Led)
            {
                modes.Add(batch.Mode);
            }
        }

        foreach (var instance in harness.List.Instances)
        {
            counts[(int)instance.Shape]++;
        }

        Assert.Equal(new HashSet<int> { LedDraw.SilkMode, LedDraw.FlareMode }, modes);
        Assert.True(counts[(int)OrbShape.Capsule] > 54 + 20, $"{counts[(int)OrbShape.Capsule]} haze strands and spark streaks");
        Assert.True(counts[(int)OrbShape.Circle] > 4 + 2 + 20, $"{counts[(int)OrbShape.Circle]} ticks, comet heads, spark heads and embers");
        Assert.Equal(3, counts[(int)OrbShape.Arc]);
        Assert.Equal(1, counts[(int)OrbShape.BandArc]);
        Assert.InRange(counts[(int)OrbShape.RoundedRect], 37, 40);
        Assert.True(counts[(int)OrbShape.Stroke] >= LedArcs.PointCount - 1, $"{counts[(int)OrbShape.Stroke]} arc strokes");
        Assert.True(additive > 100);
        Assert.True(harness.List.HasLed);
    }

    [Fact]
    public void RedlineAmountFollowsTheScene()
    {
        foreach (var level in new[] { EffectsIntensity.Off, EffectsIntensity.Standard })
        {
            var harness = Create(level: level);
            Assert.Equal(0, LedDraw.RedlineAmount(harness.Scene));
            harness.Speed(1.2);
            harness.Run(3);
            harness.Run(1, _ => Assert.InRange(LedDraw.RedlineAmount(harness.Scene), 0.995, 1));
            harness.Speed(0.3);
            harness.Run(3);
            Assert.InRange(LedDraw.RedlineAmount(harness.Scene), 0, 0.01);
        }
    }

    [Fact]
    public void UnavailableShowsTheGaugeAtRest()
    {
        var harness = Create();
        harness.Speed(1.5);
        harness.Run(2);
        harness.Rebuild(harness.Frame(1 / 60.0) with { Unavailable = true });
        var segments = 0;
        foreach (var instance in harness.List.Instances)
        {
            Assert.NotEqual(OrbShape.Arc, instance.Shape);
            if (instance.Shape == OrbShape.RoundedRect)
            {
                segments++;
                Assert.Equal(0.07f, instance.Color.W, 4);
            }
        }

        Assert.Equal(36, segments);
    }

    [Fact]
    public void HighContrastLightsSegmentsInTheHighlightColor()
    {
        var harness = Create(highContrast: true);
        harness.Speed(0.5);
        harness.Run(2);
        var highlight = harness.Context.Contrast.Highlight;
        var lit = 0;
        foreach (var instance in harness.List.Instances)
        {
            if (instance.Shape == OrbShape.RoundedRect && Math.Abs(instance.Color.Z - highlight.B) < 1e-4
                && Math.Abs(instance.Color.X - highlight.R) < 1e-4)
            {
                lit++;
            }
        }

        Assert.InRange(lit, 23, 26);
    }

    [Fact]
    public void FixedSeedGivesTheSameFrames()
    {
        var first = Create(level: EffectsIntensity.Max, seed: 3).Fingerprints();
        var second = Create(level: EffectsIntensity.Max, seed: 3).Fingerprints();
        Assert.Equal(first, second);
    }

    [Fact]
    public void WarmBuildAllocatesNothing()
    {
        var harness = Create(240, level: EffectsIntensity.Max);
        harness.Speed(2);
        harness.Run(2);
        var frame = harness.Frame(1 / 60.0);
        harness.Rebuild(frame);
        Assert.True(harness.List.Count > 200);
        AllocationProbe.AssertNone(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                harness.Rebuild(frame);
            }
        });
    }
}
