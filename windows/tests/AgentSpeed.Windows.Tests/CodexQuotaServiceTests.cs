using System.IO;
using System.Windows.Threading;
using AgentSpeed.Core.Quota;
using AgentSpeed.Services;

namespace AgentSpeed.Windows.Tests;

public sealed class CodexQuotaServiceTests
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DisabledQuotaNeverStartsAccountReads() => OnSta(() =>
    {
        var reads = 0;
        using var service = new CodexQuotaService(read: _ => { reads++; return Task.FromResult(Snapshot(Start, 0, null)); }, now: () => Start);
        service.Configure(false);
        service.Observe(Snapshot(Start, 1, Start.AddDays(1)));
        service.RefreshForViewAsync().GetAwaiter().GetResult();
        service.CheckExpiry();
        Assert.Equal(0, reads);
        Assert.Null(service.Snapshot);
    });

    [Fact]
    public void ViewingDuringAReadQueuesOnlyOneFollowUp() => OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var first = new TaskCompletionSource<CodexQuotaSnapshot>();
        var reads = 0;
        using var service = new CodexQuotaService(read: _ => ++reads == 1 ? first.Task
            : Task.FromResult(Snapshot(Start.AddSeconds(1), 0, null)), now: () => Start.AddSeconds(1));
        service.Configure(true);
        _ = service.RefreshForViewAsync();
        _ = service.RefreshForViewAsync();
        Assert.Equal(1, reads);
        first.SetResult(Snapshot(Start, 1, Start.AddDays(1)));
        Pump();
        Assert.Equal(2, reads);
        Assert.Equal(0, service.Snapshot!.AvailableResetCreditCount);
    });

    [Fact]
    public void ReenablingDuringACancelledReadStartsAFreshRead() => OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var first = new TaskCompletionSource<CodexQuotaSnapshot>();
        var reads = 0;
        CancellationToken firstToken = default;
        using var service = new CodexQuotaService(read: token =>
        {
            if (++reads == 1) { firstToken = token; return first.Task; }
            return Task.FromResult(Snapshot(Start.AddSeconds(1), 0, null));
        }, now: () => Start.AddSeconds(1));
        service.Configure(true);
        service.Configure(false);
        Assert.True(firstToken.IsCancellationRequested);
        service.Configure(true);
        first.SetResult(Snapshot(Start, 1, Start.AddDays(1)));
        Pump();
        Assert.Equal(2, reads);
        Assert.Equal(0, service.Snapshot!.AvailableResetCreditCount);
    });

    [Fact]
    public void ExpiryRemovesTheCardEvenWhenTheServerCannotBeReached() => OnSta(() =>
    {
        var now = Start;
        var reads = 0;
        using var service = new CodexQuotaService(read: _ => ++reads == 1
            ? Task.FromResult(Snapshot(now, 1, Start.AddMinutes(10)))
            : Task.FromException<CodexQuotaSnapshot>(new IOException("offline")), now: () => now);
        service.Configure(true);
        Assert.Equal(Start.AddMinutes(10), service.ExpiryDue);
        now = Start.AddMinutes(10);
        service.CheckExpiry();
        Assert.Equal(2, reads);
        Assert.Equal(0, service.Snapshot!.AvailableResetCreditCount);
        Assert.Empty(service.Snapshot.ResetCredits);
        Assert.Null(service.ExpiryDue);
    });

    [Fact]
    public void ViewingTheDetailsDetectsConsumptionWithoutQuotaRecovery() => OnSta(() =>
    {
        var now = Start;
        var reads = 0;
        using var service = new CodexQuotaService(read: _ => Task.FromResult(Snapshot(now, ++reads == 1 ? 1 : 0, Start.AddDays(2))), now: () => now);
        service.Configure(true);
        now = now.AddSeconds(2);
        service.RefreshForViewAsync().GetAwaiter().GetResult();
        Assert.Equal(2, reads);
        Assert.Equal(0, service.Snapshot!.AvailableResetCreditCount);
        Assert.Empty(service.Snapshot.ResetCredits);
    });

    [Fact]
    public void RecoverySmallerThanOneDisplayedPercentRefreshesTheCards() => OnSta(() =>
    {
        var now = Start;
        var reads = 0;
        using var service = new CodexQuotaService(read: _ => Task.FromResult(Snapshot(now, ++reads == 1 ? 1 : 0, Start.AddDays(2), reads == 1 ? 55.9 : 55.8)), now: () => now);
        service.Configure(true);
        now = now.AddSeconds(1);
        service.Observe(Snapshot(now, 0, null, 55.8) with { AvailableResetCreditCount = null });
        Assert.Equal(2, reads);
        Assert.Equal(0, service.Snapshot!.AvailableResetCreditCount);
    });

    [Fact]
    public void EarlyDeadlineAndIdleTimeDoNotQueryTheServer() => OnSta(() =>
    {
        var now = Start;
        var reads = 0;
        using var service = new CodexQuotaService(read: _ => { reads++; return Task.FromResult(Snapshot(now, 1, Start.AddDays(2))); }, now: () => now);
        service.Configure(true);
        now = now.AddHours(1);
        service.CheckExpiry();
        Assert.Equal(1, reads);
        Assert.Equal(Start.AddDays(2), service.ExpiryDue);
        service.Configure(false);
        Assert.Null(service.ExpiryDue);
    });

    [Fact]
    public void ReenablingAfterExpiryDoesNotRestoreAnExpiredCardWhileOffline() => OnSta(() =>
    {
        var now = Start;
        var reads = 0;
        using var service = new CodexQuotaService(read: _ => ++reads == 1
            ? Task.FromResult(Snapshot(now, 1, Start.AddMinutes(10)))
            : Task.FromException<CodexQuotaSnapshot>(new IOException("offline")), now: () => now);
        service.Configure(true);
        service.Configure(false);
        now = Start.AddMinutes(20);
        service.Configure(true);
        Assert.Equal(0, service.Snapshot!.AvailableResetCreditCount);
        Assert.Empty(service.Snapshot.ResetCredits);
        Assert.Null(service.ExpiryDue);
    });

    private static CodexQuotaSnapshot Snapshot(DateTimeOffset now, int count, DateTimeOffset? expiry, double used = 55.9) =>
        new(new(used, 10080, Start.AddDays(7)), null, now)
        {
            AvailableResetCreditCount = count,
            ResetCredits = count > 0 ? [new(expiry)] : [],
        };

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) { throw new InvalidOperationException("Quota service check failed.", failure); }
    }
}
