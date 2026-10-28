using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Tests;

public class NeedleWakeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(12.6, 5)]
    [InlineData(55, 22)]
    [InlineData(200, NeedleWake.MaxLayers)]
    public void LengthsAreWholeSlices(double length, int slices)
    {
        Assert.Equal(slices, NeedleWake.SliceCount(length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(22)]
    public void TheCompositeRisesLinearlyFromTheTailToTheNeedle(int slices)
    {
        Span<NeedleWakeLayer> layers = stackalloc NeedleWakeLayer[NeedleWake.MaxLayers];
        var n = NeedleWake.Layers(slices, NeedleModel.WakeOpacity, layers);
        Assert.Equal(slices, n);
        for (var j = 0; j < n; j++)
        {
            Assert.Equal(NeedleWake.SliceDegrees * (n - j), layers[j].TrailDegrees, 9);
            Assert.InRange(layers[j].Alpha, 0, 1);

            // Slice j from the tail lies under wedges 0..j.
            var keep = 1.0;
            for (var k = 0; k <= j; k++)
            {
                keep *= 1 - layers[k].Alpha;
            }

            var expected = NeedleModel.WakeOpacity * (j + 0.5) / n;
            Assert.InRange(1 - keep, expected - 1e-9, expected + 1e-9);
        }
    }

    [Fact]
    public void NoWakeWithoutLengthOrAlpha()
    {
        Span<NeedleWakeLayer> layers = stackalloc NeedleWakeLayer[NeedleWake.MaxLayers];
        Assert.Equal(0, NeedleWake.Layers(0, 0.32, layers));
        Assert.Equal(0, NeedleWake.Layers(5, 0, layers));
    }
}
