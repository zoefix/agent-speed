using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Tests;

public class SpeedPresenterTests
{
    private static SpeedSnapshot WithLive(double output, double total) =>
        SpeedSnapshot.Empty with { Live = new Rate(output, total) };

    [Fact]
    public void DefaultsToOutputMetricAndDefaultRedline()
    {
        var presenter = new SpeedPresenter();
        Assert.Equal(SpeedMetric.Output, presenter.Metric);
        Assert.Equal(HeatMath.DefaultRedline, presenter.Redline);
        Assert.Equal(0, presenter.Value);
        Assert.Equal(SpeedTier.Idle, presenter.Tier);
    }

    [Fact]
    public void ValueFollowsSelectedMetric()
    {
        var presenter = new SpeedPresenter();
        presenter.Update(WithLive(120, 900), 0);
        Assert.Equal(120, presenter.Value);

        presenter.Configure(SpeedMetric.Total, 400, 0.1);
        Assert.Equal(900, presenter.Value);
        Assert.Equal(HeatMath.MinRedlineTotal, presenter.Redline);
        Assert.Equal(900, presenter.ValueFor(new Rate(1, 900)));

        presenter.Update(WithLive(120, 900_000), 0.2);
        Assert.Equal(1, presenter.Heat);
        Assert.Equal(1, presenter.Overdrive);
    }

    [Fact]
    public void ConfigureClampsRedline()
    {
        var presenter = new SpeedPresenter();
        presenter.Configure(SpeedMetric.Output, 433, 0);
        Assert.Equal(450, presenter.Redline);
        presenter.Configure(SpeedMetric.Output, double.NaN, 0);
        Assert.Equal(HeatMath.DefaultRedline, presenter.Redline);
    }

    [Fact]
    public void InvalidLiveValuesAreSanitized()
    {
        var presenter = new SpeedPresenter();
        presenter.Update(WithLive(double.NaN, -5), 0);
        Assert.Equal(0, presenter.Value);
        Assert.Equal(0, presenter.ValueFor(new Rate(double.PositiveInfinity, 0)));
    }

    [Fact]
    public void TierAppearsAfterHoldViaTick()
    {
        var presenter = new SpeedPresenter();
        presenter.Configure(SpeedMetric.Output, HeatMath.ReferenceRedline, 0);
        Assert.False(presenter.Update(WithLive(100, 100), 0));
        Assert.Equal(0.8, presenter.NextDeadline!.Value, 9);
        Assert.True(presenter.Tick(0.8));
        Assert.Equal(SpeedTier.Boost, presenter.Tier);
    }

    [Fact]
    public void UpdateRejectsNullSnapshot()
    {
        Assert.Throws<ArgumentNullException>(() => new SpeedPresenter().Update(null!, 0));
    }

    [Theory]
    [InlineData(5_000, 20_000)]
    [InlineData(200_000, 200_000)]
    [InlineData(234_567, 230_000)]
    [InlineData(1_249_999, 1_200_000)]
    [InlineData(9_000_000, 2_000_000)]
    [InlineData(double.NaN, 2_000_000)]
    public void TotalMetricHasItsOwnRedlineRange(double input, double expected)
    {
        var presenter = new SpeedPresenter();
        presenter.Configure(SpeedMetric.Total, input, 0);
        Assert.Equal(expected, presenter.Redline, 6);
    }
}
