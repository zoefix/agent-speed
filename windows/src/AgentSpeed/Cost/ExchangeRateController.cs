#if LINUX
using AgentSpeed.Linux.Platform;
#else
using System.Windows.Threading;
#endif
using AgentSpeed.App;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Cost;

/// <summary>
/// The exchange rates in use (METRICS 9.8, NETWORK 4). Requests run only while a non-USD currency is selected and the
/// network master switch is on: 30 s after launch or at once on selection (when the stored rates are missing or older
/// than 24 h), then daily; a failed round retries after 1 h. Selecting USD stops every request. The UI never waits: it
/// converts with the last accepted or the bundled rates at once. UI thread only.
/// </summary>
internal sealed class ExchangeRateController : IDisposable
{
    /// <summary>How long a failed manual refresh replaces the status line (DESIGN 9).</summary>
    public static readonly TimeSpan FailureDisplay = TimeSpan.FromSeconds(6);

    private readonly CurrencyTable _currencies;
    private readonly ExchangeRateStore _store;
    private readonly ExchangeRateSync _sync;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _failureTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _round;
    private DateTimeOffset _launchedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastFailure;
    private string _currency = Currency.UsdCode;
    private bool _networkEnabled;
    private bool _hasSelection;
    private bool _disposed;

    public ExchangeRateController(Dispatcher dispatcher, string appDataDirectory, IHttpTransport transport, string userAgent, CurrencyTable currencies)
    {
        _currencies = currencies;
        _store = new ExchangeRateStore(appDataDirectory, AppLog.Info);
        _sync = new ExchangeRateSync(_store, transport, userAgent, currencies, log: AppLog.Info);
        Rates = new RatesInUse(_store.Load()?.ToRateSet(), currencies.FallbackRates);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += (_, _) => OnTick();
        _failureTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FailureDisplay };
        _failureTimer.Tick += (_, _) =>
        {
            _failureTimer.Stop();
            FailureReason = null;
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    public RatesInUse Rates { get; private set; }

    public bool IsRefreshing { get; private set; }

    /// <summary>The reason of a failed manual refresh, shown as <c>rates.failed</c> for 6 s.</summary>
    public string? FailureReason { get; private set; }

    public bool CanRefresh => _networkEnabled && _currency != Currency.UsdCode && !IsRefreshing;

    public bool IsStale => Rates.IsStale(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// The selected currency and the master switch. The first call restores the stored choice (scheduled as after a
    /// launch); a later change of currency is a selection and fetches at once when the rates are missing or old.
    /// </summary>
    public void Update(string currency, bool networkEnabled)
    {
        var selected = _hasSelection && currency != _currency;
        var networkTurnedOn = _hasSelection && networkEnabled && !_networkEnabled;
        _hasSelection = true;
        _currency = currency;
        _networkEnabled = networkEnabled;
        if (networkTurnedOn)
        {
            _launchedAt = DateTimeOffset.UtcNow;
        }

        if (!networkEnabled || currency == Currency.UsdCode)
        {
            // NETWORK 1, 4: no request of any kind, including one in flight.
            _round?.Cancel();
            _timer.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (selected && ExchangeRateSync.FetchesOnSelection(FetchedAt, _lastFailure, now))
        {
            _ = RunAsync(manual: false);
        }
        else
        {
            Arm();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Manual <c>rates.refresh</c>.</summary>
    public void Refresh()
    {
        if (CanRefresh)
        {
            _ = RunAsync(manual: true);
        }
    }

    public void OnResume() => Arm();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _failureTimer.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private DateTimeOffset? FetchedAt => Rates.FetchedMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private bool Active => !_disposed && _networkEnabled && _currency != Currency.UsdCode;

    private DateTimeOffset NextAttempt(DateTimeOffset now) => ExchangeRateSync.NextAttempt(_launchedAt, FetchedAt, _lastFailure, now);

    private void OnTick()
    {
        _timer.Stop();
        if (Active && DateTimeOffset.UtcNow >= NextAttempt(DateTimeOffset.UtcNow))
        {
            _ = RunAsync(manual: false);
            return;
        }

        Arm();
    }

    private void Arm()
    {
        _timer.Stop();
        if (!Active || IsRefreshing)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var wait = NextAttempt(now) - now;
        _timer.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
            : wait > ExchangeRateSync.CheckInterval ? ExchangeRateSync.CheckInterval
            : wait;
        _timer.Start();
    }

    private async Task RunAsync(bool manual)
    {
        if (!Active || IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        _timer.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
        ExchangeRateOutcome outcome;
        using var round = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _round = round;
        try
        {
            var currency = _currency;
            var token = round.Token;
            outcome = await Task.Run(() => _sync.RunAsync(currency, token), token);
        }
        catch (OperationCanceledException)
        {
            IsRefreshing = false;
            if (!_disposed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
                Arm();
            }

            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The exchange rate refresh failed.", ex);
            outcome = new ExchangeRateOutcome.Failed("error");
        }
        finally
        {
            IsRefreshing = false;
            _round = null;
        }

        if (_disposed)
        {
            return;
        }

        switch (outcome)
        {
            case ExchangeRateOutcome.Updated updated:
                Rates = new RatesInUse(updated.Set, _currencies.FallbackRates);
                _lastFailure = null;
                break;
            case ExchangeRateOutcome.Unchanged unchanged:
                Rates = new RatesInUse(unchanged.Set, _currencies.FallbackRates);
                _lastFailure = null;
                break;
            case ExchangeRateOutcome.Failed failed:
                _lastFailure = DateTimeOffset.UtcNow;
                if (manual)
                {
                    FailureReason = failed.Reason;
                    _failureTimer.Stop();
                    _failureTimer.Start();
                }

                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Arm();
    }
}
