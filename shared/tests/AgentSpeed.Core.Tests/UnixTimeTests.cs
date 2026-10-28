namespace AgentSpeed.Core.Tests;

public class UnixTimeTests
{
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(100_000_000_000_000_000)]
    [InlineData(long.MinValue)]
    public void TimesOutsideTheCalendarClampInsteadOfThrowing(long ms)
    {
        var time = UnixTime.FromMs(ms);
        Assert.Equal(ms > 0 ? DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() : DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
            time.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void OrdinaryTimesPassThrough() =>
        Assert.Equal(1_792_670_400_000, UnixTime.FromMs(1_792_670_400_000).ToUnixTimeMilliseconds());
}
