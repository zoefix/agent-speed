using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Tests;

public class NeedleGlowTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.5204998778)]
    [InlineData(1, 0.8427007929)]
    [InlineData(2, 0.9953222650)]
    [InlineData(-1, -0.8427007929)]
    public void ErfMatchesReferenceValues(double x, double expected)
    {
        Near(expected, NeedleGlow.Erf(x), 1e-6);
    }

    [Fact]
    public void AWideStrokeBlursToHalfAtItsEdge()
    {
        Near(0.5, NeedleGlow.StrokeProfile(40, 20), 1e-6);
        Near(1, NeedleGlow.StrokeProfile(40, 0), 1e-6);
        Assert.True(NeedleGlow.StrokeProfile(40, 20 + NeedleGlow.Reach) < 0.003);
    }

    [Fact]
    public void AThinStrokeGlowsLessThanAWideOne()
    {
        Assert.True(NeedleGlow.StrokeProfile(0.5, 0.25) < 0.25);
        Assert.Equal(NeedleGlow.StrokeProfile(1, 0.7), NeedleGlow.StrokeProfile(1, -0.7), 12);
    }

    [Theory]
    [InlineData(0.5, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(8, true)]
    public void StackedLayersReproduceTheProfileInEveryBand(double width, bool core)
    {
        Span<NeedleGlowLayer> layers = stackalloc NeedleGlowLayer[NeedleGlow.MaxLayers];
        var n = NeedleGlow.StrokeLayers(width, 1, core, layers);
        Assert.InRange(n, 6, NeedleGlow.MaxLayers);
        var half = width / 2;
        for (var j = 0; j < n; j++)
        {
            Assert.InRange(layers[j].Alpha, 0, 1);
            if (j > 0)
            {
                Assert.True(layers[j].Extent < layers[j - 1].Extent, "widest first");
            }

            // Band j is covered by layers 0..j.
            var outer = half + layers[j].Extent;
            var inner = j + 1 < n ? half + layers[j + 1].Extent : core ? 0 : half;
            var expected = NeedleGlow.StrokeProfile(width, (outer + inner) / 2);
            Near(expected, NeedleGlow.Composite(layers[..(j + 1)]), 1e-9);
        }
    }

    [Fact]
    public void OpacityScalesTheCompositeExactly()
    {
        Span<NeedleGlowLayer> full = stackalloc NeedleGlowLayer[NeedleGlow.MaxLayers];
        Span<NeedleGlowLayer> faint = stackalloc NeedleGlowLayer[NeedleGlow.MaxLayers];
        var n = NeedleGlow.StrokeLayers(2, 1, false, full);
        Assert.Equal(n, NeedleGlow.StrokeLayers(2, 0.35, false, faint));
        for (var j = 1; j <= n; j++)
        {
            Near(0.35 * NeedleGlow.Composite(full[..j]), NeedleGlow.Composite(faint[..j]), 1e-9);
        }
    }

    [Fact]
    public void TheCoreAddsInnerLayersOnlyToStrokesWideEnoughToShowThem()
    {
        Span<NeedleGlowLayer> layers = stackalloc NeedleGlowLayer[NeedleGlow.MaxLayers];
        Assert.Equal(6, NeedleGlow.StrokeLayers(0.4, 1, true, layers));
        Assert.Equal(9, NeedleGlow.StrokeLayers(4, 1, true, layers));
        Assert.True(layers[8].Extent < 0);

        // Inside a wide blurred stroke the composite approaches its center profile.
        Near(NeedleGlow.StrokeProfile(4, 0.35), NeedleGlow.Composite(layers[..9]), 1e-9);
    }

    [Fact]
    public void TooFewLayerSlotsThrow()
    {
        var layers = new NeedleGlowLayer[3];
        Assert.Throws<ArgumentException>(() => NeedleGlow.StrokeLayers(1, 1, false, layers));
    }

    [Fact]
    public void StackAlphasNeverGoesBackDown()
    {
        Span<double> alphas = stackalloc double[3];
        NeedleGlow.StackAlphas([0.2, 0.1, 0.5], alphas);
        Assert.Equal(0.2, alphas[0], 12);
        Assert.Equal(0, alphas[1], 12);
        Assert.Equal((0.5 - 0.2) / 0.8, alphas[2], 12);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(4)]
    public void ABlurredDiscIsExactAtItsCenter(double radius)
    {
        Near(1 - Math.Exp(-radius * radius / 2), NeedleGlow.BlurredDisc(radius, 0), 1e-6);
    }

    [Fact]
    public void ALargeBlurredDiscLooksLikeABlurredEdge()
    {
        const double radius = 40;
        Near(0.5, NeedleGlow.BlurredDisc(radius, radius), 0.005);
        Near(0.5 * (1 - NeedleGlow.Erf(1 / Math.Sqrt(2))), NeedleGlow.BlurredDisc(radius, radius + 1), 0.005);
        Near(1, NeedleGlow.BlurredDisc(radius, radius - 5), 1e-4);
        Assert.Equal(0, NeedleGlow.BlurredDisc(radius, radius + 10));
    }

    [Fact]
    public void ABlurredDiscMatchesBruteForceIntegration()
    {
        const double radius = 0.8;
        foreach (var rho in new[] { 0.3, 0.8, 1.5, 2.5 })
        {
            Near(BruteForceDisc(radius, rho), NeedleGlow.BlurredDisc(radius, rho), 1e-3);
        }
    }

    [Fact]
    public void ABlurredDiscFallsOffMonotonically()
    {
        var previous = double.MaxValue;
        for (var rho = 0.0; rho < 6; rho += 0.25)
        {
            var value = NeedleGlow.BlurredDisc(1.5, rho);
            Assert.True(value <= previous + 1e-9);
            previous = value;
        }
    }

    [Fact]
    public void ScaledBesselMatchesReferenceValues()
    {
        // e^-x I0(x) at 1, 5 and 20.
        Near(0.4657596076, NeedleGlow.ScaledBesselI0(1), 1e-6);
        Near(0.1835408126, NeedleGlow.ScaledBesselI0(5), 1e-6);
        Near(0.0897803119, NeedleGlow.ScaledBesselI0(20), 1e-6);
    }

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    private static double BruteForceDisc(double radius, double rho)
    {
        const int N = 1200;
        var h = 2 * radius / N;
        var sum = 0.0;
        for (var i = 0; i < N; i++)
        {
            var x = -radius + (i + 0.5) * h;
            for (var j = 0; j < N; j++)
            {
                var y = -radius + (j + 0.5) * h;
                if (x * x + y * y > radius * radius)
                {
                    continue;
                }

                var dx = x - rho;
                sum += Math.Exp(-0.5 * (dx * dx + y * y));
            }
        }

        return sum * h * h / (2 * Math.PI);
    }
}
