using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

public class LiquidSurfaceSamplingTests
{
    private const double R = LiquidGeometry.GlassRadius;

    [Fact]
    public void ASurfaceThatCanReachTheCenterLineSpansTheWholeGlass()
    {
        Assert.Equal(R, LiquidSurfaceSampling.VisibleHalfWidth(0, 0));
        Assert.Equal(R, LiquidSurfaceSampling.VisibleHalfWidth(0.03, 0.05));
        Assert.Equal(R, LiquidSurfaceSampling.VisibleHalfWidth(-0.03, 0.05));
    }

    [Theory]
    [InlineData(0.382, 0)]
    [InlineData(-0.3, 0.02)]
    [InlineData(0.2, 0.06)]
    public void TheBandIsTheChordAtTheNearestReachableHeight(double meanY, double margin)
    {
        var nearest = Math.Abs(meanY) - margin;
        Assert.Equal(Math.Sqrt(R * R - nearest * nearest), LiquidSurfaceSampling.VisibleHalfWidth(meanY, margin), 12);
    }

    [Fact]
    public void SamplesAreEvenlySpacedWithBothEndsIncluded()
    {
        Span<double> xs = stackalloc double[5];
        LiquidSurfaceSampling.FillSampleX(xs, 0.2);
        Assert.Equal([-0.2, -0.1, 0, 0.1, 0.2], xs.ToArray(), (a, b) => Math.Abs(a - b) < 1e-12);
    }

    [Theory]
    [InlineData(0.05, EffectsIntensity.Standard)]
    [InlineData(0.5, EffectsIntensity.Soft)]
    [InlineData(1.0, EffectsIntensity.Standard)]
    [InlineData(1.8, EffectsIntensity.Max)]
    public void BeyondTheBandEverySurfacePointIsOutsideTheGlass(double ratio, EffectsIntensity level)
    {
        var model = new LiquidModel(3);
        var driver = new EnergyOrbDriver(model) { Size = 148 }.Speed(ratio).Level(level);
        driver.Snap();
        var pulses = 0;
        driver.Watch(4, () =>
        {
            if (driver.Time - 10 > pulses * 0.7)
            {
                pulses++;
                model.Pulse(driver.Heat, 1.25);
            }

            var margin = LiquidSurfaceSampling.Margin(model.WaveAmplitude, model.ChopAmplitude, model.RippleCount > 0);
            var half = LiquidSurfaceSampling.VisibleHalfWidth(model.MeanSurfaceY, margin);
            for (var k = 0; k <= 16; k++)
            {
                var x = half + (R - half) * k / 16;
                foreach (var sign in new[] { -1.0, 1.0 })
                {
                    foreach (var y in new[] { model.SurfaceY(sign * x), model.BackSurfaceY(sign * x) })
                    {
                        Assert.True(x * x + y * y >= R * R - 1e-9, $"({sign * x:F4}, {y:F4}) is inside the glass beyond half {half:F4}");
                    }
                }
            }
        });
        Assert.True(model.RippleCount > 0 || ratio < 0.1);
    }
}
