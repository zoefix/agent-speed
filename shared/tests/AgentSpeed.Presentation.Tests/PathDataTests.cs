using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Tests;

public class PathDataTests
{
    private const string AllVerbs = "M 0 0 L 10 0 Q 15 5 10 10 C 5 15 0 10 0 5 Z";

    [Fact]
    public void ParsesEveryVerbWithItsPoints()
    {
        var path = PathData.Parse(AllVerbs);

        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Quad, PathVerb.Cubic, PathVerb.Close], path.Segments.Select(s => s.Verb));
        Assert.Equal(new PathSegment(PathVerb.Move, new PointD(0, 0)), path.Segments[0]);
        Assert.Equal(new PathSegment(PathVerb.Line, new PointD(10, 0)), path.Segments[1]);
        Assert.Equal(new PathSegment(PathVerb.Quad, new PointD(15, 5), new PointD(10, 10)), path.Segments[2]);
        Assert.Equal(new PathSegment(PathVerb.Cubic, new PointD(5, 15), new PointD(0, 10), new PointD(0, 5)), path.Segments[3]);
        Assert.Equal(new PointD(0, 0), path.Start);
        Assert.Equal(new PointD(10, 10), path.Segments[2].End);
        Assert.Equal(new PointD(0, 5), path.Segments[3].End);
    }

    [Fact]
    public void ControlPointsAndBoundsIncludeControlHandles()
    {
        var path = PathData.Parse(AllVerbs);

        Assert.Equal(
            [new(0, 0), new(10, 0), new(15, 5), new(10, 10), new(5, 15), new(0, 10), new(0, 5)],
            path.ControlPoints().ToArray());
        Assert.Equal(new RectD(0, 0, 15, 15), path.ControlBounds);
    }

    [Fact]
    public void SampleWalksCurvesInTwelveStepsAndClosesAtTheStart()
    {
        var path = PathData.Parse(AllVerbs);
        var points = path.Sample().ToArray();

        Assert.Equal(1 + 1 + 12 + 12 + 1, points.Length);
        Assert.Equal(new PointD(0, 0), points[0]);
        Assert.Equal(new PointD(10, 0), points[1]);
        AssertNear(new PointD(12.5, 5), points[1 + 6]);
        AssertNear(new PointD(10, 10), points[1 + 12]);
        AssertNear(new PointD(0, 5), points[1 + 12 + 12]);
        Assert.Equal(new PointD(0, 0), points[^1]);

        Assert.Equal(1 + 1 + 4 + 4 + 1, path.Sample(4).Count());
    }

    [Fact]
    public void SampledCubicMatchesBernsteinForm()
    {
        var path = PathData.Parse("M 0 0 C 0 10 10 10 10 0");
        var points = path.Sample().ToArray();

        // t = 0.5 → (5, 7.5)
        AssertNear(new PointD(5, 7.5), points[6]);
        AssertNear(new PointD(10, 0), points[^1]);
    }

    [Fact]
    public void ParsesNegativeAndDecimalNumbers()
    {
        var path = PathData.Parse("M -1.5 -2 L -10.25 3 L 0.5 -0.05");

        Assert.Equal([new(-1.5, -2), new(-10.25, 3), new(0.5, -0.05)], path.ControlPoints().ToArray());
        Assert.Equal(RectD.FromEdges(-10.25, -2, 0.5, 3), path.ControlBounds);
    }

    [Fact]
    public void ParsesSeveralSubpaths()
    {
        var path = PathData.Parse("M 1 1 L 2 2 M 5 5 L 6 6 Z");
        var points = path.Sample().ToArray();

        Assert.Equal(new PointD(5, 5), points[^1]);
        Assert.Equal(new PointD(1, 1), path.Start);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" M 0 0")]
    [InlineData("M 0 0 ")]
    [InlineData("M 0  0")]
    [InlineData("M 0 0 A 1 1 0 0 1 2 2")]
    [InlineData("M 0 0 H 4")]
    [InlineData("m 0 0 l 1 1")]
    [InlineData("L 0 0 L 1 1")]
    [InlineData("Z")]
    [InlineData("M 0")]
    [InlineData("M 0 0 L 1")]
    [InlineData("M 0 0 L 1 1 M")]
    [InlineData("M 1.234 0")]
    [InlineData("M 1. 0")]
    [InlineData("M .5 0")]
    [InlineData("M +1 0")]
    [InlineData("M 1e2 0")]
    [InlineData("M - 0")]
    [InlineData("M 1,5 0")]
    [InlineData("M 0 0 L NaN 1")]
    [InlineData("M\t0 0")]
    [InlineData("M 0 0\nL 1 1")]
    [InlineData("M 0 0 L ١ 1")]
    public void RejectsMalformedPaths(string d) => Assert.Throws<FormatException>(() => PathData.Parse(d));

    [Fact]
    public void RejectsNull() => Assert.Throws<FormatException>(() => PathData.Parse(null!));

    private static void AssertNear(PointD expected, PointD actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
    }
}
