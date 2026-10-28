using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Tests;

public class HeroTourTests
{
    private const double Noise = 0.035 + 0.025 + 0.015;

    [Fact]
    public void StartsAtZero()
    {
        Assert.Equal(0, HeroTour.Value(HeroTourScenario.Tour, 0));
        Assert.Equal(0, HeroTour.Value(HeroTourScenario.Startups, 0));
        Assert.Equal(0, HeroTour.Value(HeroTourScenario.Tour, -3));
        Assert.Equal(0, HeroTour.Value(HeroTourScenario.Tour, double.NaN));
        Assert.Equal(0, HeroTour.Value(HeroTourScenario.Tour, double.PositiveInfinity));
    }

    [Fact]
    public void Durations()
    {
        Assert.Equal(56, HeroTour.Duration(HeroTourScenario.Tour));
        Assert.Equal(39, HeroTour.Duration(HeroTourScenario.Startups));
    }

    [Theory]
    [InlineData(HeroTourScenario.Tour, 3, 3)]
    [InlineData(HeroTourScenario.Tour, 6, 10)]
    [InlineData(HeroTourScenario.Tour, 12, 19)]
    [InlineData(HeroTourScenario.Tour, 22, 78)]
    [InlineData(HeroTourScenario.Tour, 32, 320)]
    [InlineData(HeroTourScenario.Tour, 37.5, 700)]
    [InlineData(HeroTourScenario.Tour, 41, 230)]
    [InlineData(HeroTourScenario.Tour, 44, 26)]
    [InlineData(HeroTourScenario.Tour, 52, 38)]
    [InlineData(HeroTourScenario.Startups, 2, 10)]
    [InlineData(HeroTourScenario.Startups, 18.5, 450)]
    [InlineData(HeroTourScenario.Startups, 27.5, 18)]
    [InlineData(HeroTourScenario.Startups, 36, 120)]
    public void ReachesEachKeyAfterTheRamp(HeroTourScenario scenario, double at, double value)
    {
        foreach (var after in new[] { 0.46, 1.0, 1.9 })
        {
            var v = HeroTour.Value(scenario, at + after);
            Assert.InRange(v, value * (1 - Noise), value * (1 + Noise));
        }
    }

    [Fact]
    public void RampsLogarithmicallyBetweenKeys()
    {
        // Key (6, 10) after (3, 3): halfway through the 0.45 s ramp the value is the geometric mean.
        var t = 6 + 0.225;
        var noise = 0.035 * Math.Sin(t * 2.1) + 0.025 * Math.Sin(t * 5.3 + 1) + 0.015 * Math.Sin(t * 11.7 + 2);
        Assert.Equal(Math.Sqrt(3 * 10) * (1 + noise), HeroTour.Value(HeroTourScenario.Tour, t), 9);

        var start = 6.0;
        var startNoise = 0.035 * Math.Sin(start * 2.1) + 0.025 * Math.Sin(start * 5.3 + 1) + 0.015 * Math.Sin(start * 11.7 + 2);
        Assert.Equal(3 * (1 + startNoise), HeroTour.Value(HeroTourScenario.Tour, start), 9);
    }

    [Theory]
    [InlineData(HeroTourScenario.Tour, 47.5)]
    [InlineData(HeroTourScenario.Tour, 51.9)]
    [InlineData(HeroTourScenario.Startups, 5.6)]
    [InlineData(HeroTourScenario.Startups, 13.6)]
    [InlineData(HeroTourScenario.Startups, 30.6 + 0.45)]
    public void IdleStretchesAreZero(HeroTourScenario scenario, double at) => Assert.Equal(0, HeroTour.Value(scenario, at));

    [Theory]
    [InlineData(HeroTourScenario.Tour, 20)]
    [InlineData(HeroTourScenario.Tour, 35)]
    [InlineData(HeroTourScenario.Startups, 19)]
    public void ScalesWithTheRedline(HeroTourScenario scenario, double at)
    {
        var base400 = HeroTour.Value(scenario, at);
        Assert.Equal(base400, HeroTour.Value(scenario, at, 400));
        Assert.Equal(2 * base400, HeroTour.Value(scenario, at, 800), 9);
        Assert.Equal(base400 / 4, HeroTour.Value(scenario, at, 100), 9);
        Assert.Equal(base400, HeroTour.Value(scenario, at, double.NaN));
        Assert.Equal(base400, HeroTour.Value(scenario, at, -5));
    }

    [Theory]
    [InlineData(HeroTourScenario.Tour, 10)]
    [InlineData(HeroTourScenario.Tour, 33.3)]
    [InlineData(HeroTourScenario.Startups, 20)]
    public void LoopsAfterItsDuration(HeroTourScenario scenario, double at)
    {
        var duration = HeroTour.Duration(scenario);
        Assert.Equal(HeroTour.Value(scenario, at), HeroTour.Value(scenario, at + duration), 9);
        Assert.Equal(HeroTour.Value(scenario, at), HeroTour.Value(scenario, at + 3 * duration), 9);
    }

    [Fact]
    public void VisitsEveryTitleUpToOverdrive()
    {
        var max = 0.0;
        for (var t = 0.0; t < HeroTour.Duration(HeroTourScenario.Tour); t += 0.05)
        {
            max = Math.Max(max, HeroTour.Value(HeroTourScenario.Tour, t));
        }

        Assert.InRange(max, 700 * (1 - Noise), 700 * (1 + Noise));
    }

    [Theory]
    [InlineData(null, HeroTourScenario.Tour)]
    [InlineData("", HeroTourScenario.Tour)]
    [InlineData("  ", HeroTourScenario.Tour)]
    [InlineData("tour", HeroTourScenario.Tour)]
    [InlineData("TOUR", HeroTourScenario.Tour)]
    [InlineData("startups", HeroTourScenario.Startups)]
    [InlineData("Startups", HeroTourScenario.Startups)]
    public void ParsesScenarioNames(string? text, HeroTourScenario expected)
    {
        Assert.True(HeroTour.TryParse(text, out var scenario));
        Assert.Equal(expected, scenario);
    }

    [Theory]
    [InlineData("demo")]
    [InlineData("startup")]
    [InlineData("tour2")]
    public void RejectsUnknownScenarioNames(string text)
    {
        Assert.False(HeroTour.TryParse(text, out var scenario));
        Assert.Equal(HeroTourScenario.Tour, scenario);
    }
}
