using AgentSpeed.Linux.Platform;

namespace AgentSpeed.Linux.Tests;

public sealed class OrbLifecycleTests
{
    [Fact]
    public async Task ExistingInstanceActivationRetriesUntilTheBridgeRegisters()
    {
        var attempts = 0;
        var waits = 0;
        var failure = await ExistingInstanceActivation.RetryAsync(() =>
        {
            if (++attempts < 3) throw new IOException("Bridge is not registered yet.");
            return Task.CompletedTask;
        }, attempts: 5, retryDelay: TimeSpan.Zero, delayAsync: _ => { waits++; return Task.CompletedTask; });

        Assert.Null(failure);
        Assert.Equal(3, attempts);
        Assert.Equal(2, waits);
    }

    [Fact]
    public async Task ExistingInstanceActivationStopsAtItsConfiguredRetryLimit()
    {
        var attempts = 0;
        var waits = 0;
        var failure = await ExistingInstanceActivation.RetryAsync(() =>
        {
            attempts++;
            throw new IOException("Bridge is not registered.");
        }, attempts: 3, retryDelay: TimeSpan.Zero, delayAsync: _ => { waits++; return Task.CompletedTask; });

        Assert.IsType<IOException>(failure);
        Assert.Equal(3, attempts);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void PositionFlushReentryCannotMutateTheClosingWindowCollection()
    {
        var gate = new LinuxOrbSyncGate();
        var windows = new Dictionary<string, int>(StringComparer.Ordinal) { ["orb"] = 1 };
        var operations = new List<string>();
        var syncPasses = 0;

        var entered = gate.Run(repositionExisting: false, () =>
        {
            syncPasses++;
            LinuxOrbPositionLifecycle.Close(
                retainPosition: true,
                flushPosition: () =>
                {
                    Assert.DoesNotContain("orb", windows.Keys);
                    operations.Add("flush");
                    // Settings.Update raises Changed synchronously; its SyncOrbs call must not re-enter.
                    gate.Run(repositionExisting: false, () => { syncPasses++; windows["orb"] = 2; });
                },
                close: () => operations.Add("close"),
                removeFromLiveWindows: () => { windows.Remove("orb"); operations.Add("remove"); });
        });

        Assert.True(entered);
        Assert.Equal(["remove", "flush", "close"], operations);
        Assert.Equal(1, syncPasses);
        Assert.Empty(windows);
    }
}
