using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Gpu;

public class ReactorDrawTests
{
    private const int Budget = 260;

    private static LedMinimalReactorHarness Create(double size = 148, bool dark = true, EffectsIntensity level = EffectsIntensity.Standard,
        bool reduceMotion = false, bool highContrast = false, int seed = 7) =>
        new(OrbStyle.Reactor, size, dark, level, reduceMotion, highContrast, seed);

    private static int Count(OrbDrawList list, OrbShape shape)
    {
        var count = 0;
        foreach (var instance in list.Instances)
        {
            count += instance.Shape == shape ? 1 : 0;
        }

        return count;
    }

    [Theory]
    [MemberData(nameof(LedMinimalReactorHarness.Matrix), MemberType = typeof(LedMinimalReactorHarness))]
    public void EveryFrameOfTheScenarioIsValid(bool dark, bool highContrast, EffectsIntensity level, double size)
    {
        var most = Create(size, dark, level, highContrast: highContrast).CheckScenario(Budget);
        Assert.InRange(most, 20, Budget);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReduceMotionScenarioIsValid(bool dark) =>
        Create(dark: dark, level: EffectsIntensity.Max, reduceMotion: true).CheckScenario(Budget);

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 12)]
    public void ThumbnailsDrawRotorsAAndCAndTheCore(bool highContrast, int count)
    {
        var harness = Create(48, level: EffectsIntensity.Max, highContrast: highContrast);
        harness.Run(0.5, list => Assert.Equal(count, list.Count));
        harness.Speed(1.8);
        harness.Run(2, list => Assert.Equal(count, list.Count));
    }

    [Fact]
    public void IdleShowsTheRingsAtRestWithoutCorona()
    {
        var harness = Create();
        harness.Run(1);
        var list = harness.List;
        Assert.Equal(0, Count(list, OrbShape.Radial));
        Assert.Equal(1 + 24 + 2 + 3 + 24 + 2 + 2, list.Count);
    }

    [Fact]
    public void SmallOrbsDropNotchesAndCoils()
    {
        var harness = Create(96);
        harness.Run(1);
        Assert.Equal(1 + 4 + 2 + 3 + 12 + 2 + 2, harness.List.Count);
    }

    [Fact]
    public void TheRedlineThrowsLightningAtStandardButNotSoft()
    {
        foreach (var (level, expected) in new[] { (EffectsIntensity.Soft, false), (EffectsIntensity.Standard, true), (EffectsIntensity.Max, true) })
        {
            var harness = Create(level: level);
            harness.Speed(1.8);
            harness.Run(1.5);
            var bolts = false;
            harness.Run(1.5, list => bolts |= Count(list, OrbShape.Capsule) >= 24 + 24);
            Assert.Equal(expected, bolts);
        }
    }

    [Fact]
    public void ActiveDarkAddsTheCoronaAndGlowsAdditively()
    {
        var harness = Create();
        harness.Speed(0.6);
        harness.Run(2);
        var list = harness.List;
        Assert.Equal(1, Count(list, OrbShape.Radial));
        var additive = 0;
        foreach (var batch in list.Batches)
        {
            additive += batch.Blend == OrbBlend.Additive ? batch.Count : 0;
        }

        Assert.True(additive >= 1 + 3 + 3 + 24 + 2 + 1);
    }

    [Fact]
    public void HighContrastDropsCoronaGlowsAndLightning()
    {
        var harness = Create(highContrast: true);
        harness.Speed(1.8);
        harness.Run(3, list =>
        {
            Assert.Equal(0, Count(list, OrbShape.Radial));
            foreach (var instance in list.Instances)
            {
                Assert.Equal(0f, instance.B.W);
            }
        });
    }

    [Fact]
    public void FixedSeedGivesTheSameFrames() =>
        Assert.Equal(Create(level: EffectsIntensity.Max, seed: 11).Fingerprints(), Create(level: EffectsIntensity.Max, seed: 11).Fingerprints());

    [Fact]
    public void WarmBuildAllocatesNothing()
    {
        var harness = Create(240, level: EffectsIntensity.Max);
        harness.Speed(2);
        harness.Run(2);
        var frame = harness.Frame(1 / 60.0);
        harness.Rebuild(frame);
        Assert.True(harness.List.Count > 60);
        AllocationProbe.AssertNone(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                harness.Rebuild(frame);
            }
        });
    }
}
