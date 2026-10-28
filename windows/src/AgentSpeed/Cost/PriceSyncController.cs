#if LINUX
using AgentSpeed.Linux.Platform;
#else
using System.Windows.Threading;
#endif
using AgentSpeed.App;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Cost;

/// <summary>What the last manual price check found, shown inline in Settings (DESIGN 9 Price table).</summary>
internal enum PriceCheckResult
{
    None,
    UpToDate,
    Updated,
    NewerSchema,
    Failed,
}

/// <summary>
/// The daily price-table sync of NETWORK 3: 60 s after launch, then whenever 24 h have passed since the last success
/// (checked hourly and on wake); a failed round retries once after 1 h. A table that becomes current is added to the
/// shared <see cref="PriceBook"/>; days already bound keep their table (METRICS 9.7). UI thread only.
/// </summary>
internal sealed class PriceSyncController : IDisposable
{
    private readonly PriceBook _book;
    private readonly PriceTableStore _store;
    private readonly PriceTableSync _sync;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _round;
    private bool _networkEnabled = true;
    private DateTimeOffset _launchedAt = DateTimeOffset.UtcNow;
    private bool _enabled;
    private bool _disposed;

    public PriceSyncController(Dispatcher dispatcher, PriceBook book, string appDataDirectory, IHttpTransport transport, string userAgent)
    {
        _book = book;
        _store = new PriceTableStore(appDataDirectory, AppLog.Info);
        _sync = new PriceTableSync(_store, transport, userAgent, log: AppLog.Info);
        State = _store.LoadState();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += (_, _) => OnTick();
    }

    public event EventHandler? Changed;

    /// <summary>A downloaded table became current; the history re-prices what no table could price (METRICS 9.7).</summary>
    public event EventHandler<PriceTable>? TableAccepted;

    public PriceSyncState State { get; private set; }

    public bool IsChecking { get; private set; }

    /// <summary>The outcome of the last manual check (shown until the next one).</summary>
    public PriceCheckResult LastCheck { get; private set; }

    public string? LastCheckReason { get; private set; }

    public int? LastCheckVersion { get; private set; }

    /// <summary>Only tables of a newer schema were offered by the last round (<c>prices.newerSchema</c>).</summary>
    public bool NewerSchemaOffered { get; private set; }

    /// <summary>The validated stored copy of an earlier sync, for the price book at launch; an invalid copy is deleted.</summary>
    public static PriceTable? LoadStoredTable(string appDataDirectory) => new PriceTableStore(appDataDirectory, AppLog.Info).LoadTable();

    /// <summary>Automatic rounds run while <paramref name="enabled"/> (the master switch and <c>syncPrices</c>).</summary>
    public void SetEnabled(bool enabled, bool asAfterLaunch)
    {
        if (asAfterLaunch)
        {
            _launchedAt = DateTimeOffset.UtcNow;
        }

        _enabled = enabled;
        Arm();
    }

    /// <summary>NETWORK 1 master switch: off, not even a manual check is made.</summary>
    public bool NetworkEnabled
    {
        get => _networkEnabled;
        set
        {
            _networkEnabled = value;
            if (!value)
            {
                // NETWORK 1: turning the switch off also stops a request in flight.
                _round?.Cancel();
            }
        }
    }

    /// <summary>A manual check can start now.</summary>
    public bool CanCheck => NetworkEnabled && !IsChecking && !_disposed;

    /// <summary>Manual <c>prices.check</c>: always tries now (with the network on) and reports the outcome inline.</summary>
    public void CheckNow()
    {
        if (CanCheck)
        {
            _ = RunAsync(manual: true);
        }
    }

    /// <summary>Re-checks the schedule after the computer wakes.</summary>
    public void OnResume() => Arm();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnTick()
    {
        _timer.Stop();
        if (_enabled && DateTimeOffset.UtcNow >= PriceTableSync.NextAttempt(_launchedAt, State, DateTimeOffset.UtcNow))
        {
            _ = RunAsync(manual: false);
            return;
        }

        Arm();
    }

    private void Arm()
    {
        _timer.Stop();
        if (_disposed || !_enabled)
        {
            return;
        }

        var wait = PriceTableSync.NextAttempt(_launchedAt, State, DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
        _timer.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
            : wait > PriceTableSync.CheckInterval ? PriceTableSync.CheckInterval
            : wait;
        _timer.Start();
    }

    private async Task RunAsync(bool manual)
    {
        if (_disposed || IsChecking)
        {
            return;
        }

        IsChecking = true;
        if (manual)
        {
            LastCheck = PriceCheckResult.None;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        PriceSyncResult? result = null;
        using var round = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _round = round;
        try
        {
            var current = _book.CurrentVersion;
            var token = round.Token;
            result = await Task.Run(() => _sync.RunAsync(current, token), token);
        }
        catch (OperationCanceledException)
        {
            IsChecking = false;
            if (!_disposed)
            {
                LastCheck = manual ? PriceCheckResult.None : LastCheck;
                Changed?.Invoke(this, EventArgs.Empty);
                Arm();
            }

            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The price table sync failed.", ex);
        }
        finally
        {
            IsChecking = false;
            _round = null;
        }

        if (_disposed)
        {
            return;
        }

        if (result is not null)
        {
            State = result.State;
            if (result.Outcome is not PriceSyncOutcome.Failed)
            {
                NewerSchemaOffered = result.Outcome is PriceSyncOutcome.NewerSchema;
            }
            if (result.Table is { } table && _book.Add(table))
            {
                AppLog.Info($"Price table v{table.Version} is current; days not bound yet use it.");
                TableAccepted?.Invoke(this, table);
            }

            if (manual)
            {
                (LastCheck, LastCheckVersion, LastCheckReason) = result.Outcome switch
                {
                    PriceSyncOutcome.Updated updated => (PriceCheckResult.Updated, (int?)updated.Version, (string?)null),
                    PriceSyncOutcome.NewerSchema => (PriceCheckResult.NewerSchema, (int?)null, (string?)null),
                    PriceSyncOutcome.Failed failed => (PriceCheckResult.Failed, (int?)null, failed.Reason),
                    _ => (PriceCheckResult.UpToDate, (int?)null, (string?)null),
                };
            }
        }
        else if (manual)
        {
            (LastCheck, LastCheckReason) = (PriceCheckResult.Failed, "error");
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Arm();
    }
}
