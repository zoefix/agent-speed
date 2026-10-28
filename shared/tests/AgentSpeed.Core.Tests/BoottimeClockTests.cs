using AgentSpeed.Core.Traffic.Linux;

namespace AgentSpeed.Core.Tests;

public sealed class BoottimeClockTests
{
    [Fact]
    public void MillisecondConversionKeepsSubsecondPrecision()
    {
        Assert.Equal(12_345, BoottimeClock.ToMilliseconds(12, 345_999_999));
    }

    [Fact]
    public void LinuxClockUsesSuspendAwareBoottimeClockId()
    {
        Assert.Equal(7, BoottimeClock.ClockId);
    }

    [Fact]
    public void NativeBoottimeClockReadsNondecreasingMilliseconds()
    {
        if (!OperatingSystem.IsLinux()) return;

        var first = BoottimeClock.Milliseconds();
        var second = BoottimeClock.Milliseconds();
        Assert.True(first >= 0);
        Assert.True(second >= first);
    }
}
