using AgentSpeed.Linux.Platform;

namespace AgentSpeed.Linux.Tests;

public sealed class LinuxAccessibilityTests
{
    [Fact]
    public async Task FailedReadsKeepTheLastKnownPreferences()
    {
        var reads = new Queue<LinuxAccessibilityRead>([
            new LinuxAccessibilityRead(false, true, true),
            new LinuxAccessibilityRead(null, false, null),
            LinuxAccessibilityRead.Unavailable,
        ]);
        var state = new LinuxAccessibilityState(_ => Task.FromResult(reads.Dequeue()));

        Assert.Equal(LinuxAccessibilitySnapshot.SafeFallback, state.Current);
        Assert.True(await state.RefreshAsync());
        Assert.Equal(new LinuxAccessibilitySnapshot(false, true, true), state.Current);
        Assert.True(await state.RefreshAsync());
        Assert.Equal(new LinuxAccessibilitySnapshot(false, false, true), state.Current);
        Assert.False(await state.RefreshAsync());
        Assert.Equal(new LinuxAccessibilitySnapshot(false, false, true), state.Current);
    }

    [Fact]
    public async Task ReaderExceptionKeepsTheLastKnownPreferences()
    {
        var attempts = 0;
        var state = new LinuxAccessibilityState(_ =>
        {
            if (attempts++ == 0) return Task.FromResult(new LinuxAccessibilityRead(false, true, true));
            return Task.FromException<LinuxAccessibilityRead>(new IOException("GNOME settings are unavailable."));
        });

        Assert.True(await state.RefreshAsync());
        var known = state.Current;
        Assert.False(await state.RefreshAsync());
        Assert.Equal(known, state.Current);
    }

    [Fact]
    public async Task SlowReaderIsBoundedAndMaintenanceCannotStartAnOverlappingRead()
    {
        var pending = new TaskCompletionSource<LinuxAccessibilityRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var state = new LinuxAccessibilityState(_ =>
        {
            attempts++;
            return attempts == 1 ? pending.Task : Task.FromResult(new LinuxAccessibilityRead(true, true, false));
        }, timeout: TimeSpan.FromMilliseconds(20));

        var first = state.RefreshAsync();
        Assert.False(first.IsCompleted);
        Assert.False(await first);
        Assert.False(await state.RefreshAsync());
        Assert.Equal(1, attempts);
        Assert.Equal(LinuxAccessibilitySnapshot.SafeFallback, state.Current);

        pending.SetResult(new LinuxAccessibilityRead(false, false, true));
        Assert.True(await state.RefreshAsync());
        Assert.Equal(2, attempts);
        Assert.Equal(new LinuxAccessibilitySnapshot(true, true, false), state.Current);
    }

    [Fact]
    public async Task PreviewStateNeverInvokesTheSystemReader()
    {
        var calls = 0;
        var state = LinuxAccessibilityState.Create(preview: true, _ =>
        {
            calls++;
            throw new InvalidOperationException("Preview must not read GNOME settings.");
        });

        Assert.False(await state.RefreshAsync());
        Assert.Equal(0, calls);
        Assert.Equal(LinuxAccessibilitySnapshot.SafeFallback, state.Current);
    }
}
