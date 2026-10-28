using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Tests;

public class EasingTests
{
    public static TheoryData<string> CurveNames => ["OutCubic", "OutQuad", "InOutSine", "OutBack"];

    private static Func<double, double> Curve(string name) => name switch
    {
        "OutCubic" => Easing.OutCubic,
        "OutQuad" => Easing.OutQuad,
        "InOutSine" => Easing.InOutSine,
        "OutBack" => t => Easing.OutBack(t),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(CurveNames))]
    public void CurvesStartAtZeroAndEndAtOne(string name)
    {
        var curve = Curve(name);
        Assert.Equal(0, curve(0), 12);
        Assert.Equal(1, curve(1), 12);
        Assert.Equal(0, curve(-3), 12);
        Assert.Equal(1, curve(7), 12);
        Assert.Equal(0, curve(double.NaN), 12);
    }

    [Fact]
    public void OutBackOvershootsWhileOthersStayInRange()
    {
        Assert.True(Easing.OutBack(0.8) > 1);
        for (var t = 0.0; t <= 1; t += 0.01)
        {
            Assert.InRange(Easing.OutCubic(t), 0, 1);
            Assert.InRange(Easing.OutQuad(t), 0, 1);
            Assert.InRange(Easing.InOutSine(t), 0, 1);
        }
    }

    [Fact]
    public void SmoothStepAndLerp()
    {
        Assert.Equal(0, Easing.SmoothStep(0, 1, -1));
        Assert.Equal(0.5, Easing.SmoothStep(0, 1, 0.5), 12);
        Assert.Equal(1, Easing.SmoothStep(0, 1, 2));
        Assert.Equal(2.5, Easing.Lerp(2, 3, 0.5), 12);
        Assert.Equal(0.7, Easing.Lerp(4, 0.7, 1), 12);
    }
}
