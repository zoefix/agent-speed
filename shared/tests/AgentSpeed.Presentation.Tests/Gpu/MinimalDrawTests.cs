using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Gpu;

public class MinimalDrawTests
{
    private const int Budget = 8;

    private static LedMinimalReactorHarness Create(double size = 148, bool dark = true, EffectsIntensity level = EffectsIntensity.Standard,
        bool reduceMotion = false, bool highContrast = false, int seed = 7) =>
        new(OrbStyle.Minimal, size, dark, level, reduceMotion, highContrast, seed);

    private static int Glows(OrbDrawList list)
    {
        var glows = 0;
        foreach (var instance in list.Instances)
        {
            glows += instance.Extra.X == 0 && instance.Color.X > 0 ? 1 : 0;
        }

        return glows;
    }

    [Theory]
    [MemberData(nameof(LedMinimalReactorHarness.Matrix), MemberType = typeof(LedMinimalReactorHarness))]
    public void EveryFrameOfTheScenarioIsValid(bool dark, bool highContrast, EffectsIntensity level, double size)
    {
        var harness = Create(size, dark, level, highContrast: highContrast);
        Assert.InRange(harness.CheckScenario(Budget), 3, 7);
        Assert.Equal(1, harness.List.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReduceMotionScenarioIsValid(bool dark) =>
        Create(dark: dark, level: EffectsIntensity.Max, reduceMotion: true).CheckScenario(Budget);

    [Fact]
    public void IdleDrawsOnlyTheTrack()
    {
        var harness = Create();
        harness.Run(3.2, list =>
        {
            Assert.Equal(1, list.Count);
            Assert.Equal(OrbShape.Ring, list.Instances[0].Shape);
        });
    }

    [Fact]
    public void BeyondTheRedlineTheSecondLapCastsAShadowOnTheFirst()
    {
        var harness = Create();
        harness.Speed(1.5);
        harness.Run(2);
        var list = harness.List;
        Assert.Equal(7, list.Count);
        var shadow = list.Instances[3];
        Assert.Equal(OrbShape.BandArc, shadow.Shape);
        Assert.Equal(0f, shadow.Extra.X);
        Assert.Equal(0f, shadow.Color.X);
        Assert.Equal(0.55f, shadow.Color.W, 4);
        Assert.Equal(2, Glows(list));
        Assert.Equal(OrbShape.Circle, list.Instances[6].Shape);
    }

    [Theory]
    [InlineData(true, EffectsIntensity.Off, 0)]
    [InlineData(false, EffectsIntensity.Max, 0)]
    [InlineData(true, EffectsIntensity.Soft, 1)]
    public void TheRingGlowsOnlyInDarkWithEffects(bool dark, EffectsIntensity level, int glows)
    {
        var harness = Create(dark: dark, level: level);
        harness.Speed(0.6);
        harness.Run(2);
        Assert.Equal(glows, Glows(harness.List));
    }

    [Fact]
    public void ThePeakTintsTheRingTowardGold()
    {
        var harness = Create();
        harness.Speed(0.3);
        harness.Run(2);
        var before = harness.List.Instances[2].Color;
        harness.Peak();
        harness.Run(0.35);
        var during = harness.List.Instances[2].Color;
        var gold = Rgba.FromHex("#FACC15");
        Assert.True(Math.Abs(during.X - gold.R) < Math.Abs(before.X - gold.R));
    }

    [Fact]
    public void FixedSeedGivesTheSameFrames() =>
        Assert.Equal(Create(level: EffectsIntensity.Max).Fingerprints(), Create(level: EffectsIntensity.Max).Fingerprints());

    [Fact]
    public void WarmBuildAllocatesNothing()
    {
        var harness = Create(240, level: EffectsIntensity.Max);
        harness.Speed(1.8);
        harness.Run(2);
        var frame = harness.Frame(1 / 60.0);
        harness.Rebuild(frame);
        Assert.True(harness.List.Count >= 6);
        AllocationProbe.AssertNone(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                harness.Rebuild(frame);
            }
        });
    }
}
