using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Tests;

public class ReactorArcGradientTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(120)]
    [InlineData(175)]
    public void TheTailAndHeadMapToTheChordEnds(double length)
    {
        Assert.Equal(0, ReactorArcGradient.ChordOffset(0, length), 12);
        Assert.Equal(0.5, ReactorArcGradient.ChordOffset(0.5, length), 12);
        Assert.Equal(1, ReactorArcGradient.ChordOffset(1, length), 12);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(45)]
    [InlineData(120)]
    [InlineData(175)]
    public void OffsetsAreTheProjectionOfTheArcPointOntoTheChord(double length)
    {
        const double radius = 0.425;
        var tail = 37.0;
        var (tx, ty) = Polar(radius, tail);
        var (hx, hy) = Polar(radius, tail + length);
        var (dx, dy) = (hx - tx, hy - ty);
        var chord2 = dx * dx + dy * dy;
        var previous = -1.0;
        for (var i = 0; i <= 20; i++)
        {
            var u = i / 20.0;
            var (px, py) = Polar(radius, tail + length * u);
            var expected = ((px - tx) * dx + (py - ty) * dy) / chord2;
            var offset = ReactorArcGradient.ChordOffset(u, length);
            Assert.Equal(expected, offset, 9);
            Assert.True(offset > previous);
            previous = offset;
        }
    }

    [Fact]
    public void LengthsAreClampedBelowAHalfTurn()
    {
        Assert.Equal(ReactorArcGradient.ChordOffset(0.3, ReactorArcGradient.MaxLengthDegrees), ReactorArcGradient.ChordOffset(0.3, 300), 12);
        Assert.InRange(ReactorArcGradient.ChordOffset(0.3, double.NaN), 0, 1);
    }

    private static (double X, double Y) Polar(double radius, double degrees) =>
        (radius * Math.Cos(degrees * Math.PI / 180), radius * Math.Sin(degrees * Math.PI / 180));
}
