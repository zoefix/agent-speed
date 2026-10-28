using AgentSpeed.Linux.Platform;
using AvaloniaPriority = Avalonia.Threading.DispatcherPriority;

namespace AgentSpeed.Linux.Tests;

public sealed class DispatcherTests
{
    [Fact]
    public void MapsSharedTimerPrioritiesToAvalonia()
    {
        Assert.Equal(AvaloniaPriority.Normal, DispatcherTimer.MapPriority(DispatcherPriority.Normal));
        Assert.Equal(AvaloniaPriority.Background, DispatcherTimer.MapPriority(DispatcherPriority.Background));
    }

    [Fact]
    public void TimersUseThePassedLinuxDispatcher()
    {
        var dispatcher = new Dispatcher();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);

        Assert.Same(dispatcher.NativeDispatcher, timer.NativeDispatcher);
        Assert.False(timer.IsEnabled);
    }

    [Fact]
    public void IntervalConstructorStillStartsTheTimerImmediately()
    {
        var dispatcher = new Dispatcher();
        var timer = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => { }, dispatcher);
        try { Assert.True(timer.IsEnabled); }
        finally { timer.Stop(); }
    }
}
