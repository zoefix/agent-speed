using AgentSpeed.App;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Quota;
using System.Windows.Threading;

namespace AgentSpeed.Services;

/// <summary>Response-driven quota; card details refresh on view, recovery and a single scheduled expiry deadline.</summary>
internal sealed class CodexQuotaService : IDisposable
{
    private readonly Func<CancellationToken, Task<CodexQuotaSnapshot>> _read;
    private readonly Func<DateTimeOffset> _now;
    private readonly DispatcherTimer _expiryTimer;
    private DateTimeOffset? _expiryDue;
    private CancellationTokenSource? _request;
    private bool _enabled;
    private bool _busy;
    private bool _disposed;
    private DateTimeOffset _lastDetailsAttempt;
    private bool _refreshAfterCurrent;

    public CodexQuotaService(Dispatcher? dispatcher = null,
        Func<CancellationToken, Task<CodexQuotaSnapshot>>? read = null, Func<DateTimeOffset>? now = null)
    {
        var client = new CodexQuotaClient();
        _read = read ?? (token => client.ReadAsync(cancellationToken: token));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _expiryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher ?? Dispatcher.CurrentDispatcher);
        _expiryTimer.Tick += (_, _) => CheckExpiry();
    }

    public event EventHandler? Changed;
    public CodexQuotaSnapshot? Snapshot { get; private set; }
    public CodexQuotaStatus Status { get; private set; } = CodexQuotaStatus.Loading;

    public void Configure(bool enabled)
    {
        if (_disposed || _enabled == enabled) { return; }
        _enabled = enabled;
        if (enabled)
        {
            Snapshot = Snapshot?.WithoutExpiredResetCredits(_now());
            ScheduleExpiry();
            Status = Snapshot is null ? CodexQuotaStatus.Loading : CodexQuotaStatus.Ready;
            _ = RefreshDetailsAsync(afterCurrent: true);
        }
        else
        {
            _request?.Cancel();
            _expiryTimer.Stop();
            _expiryDue = null;
            Status = CodexQuotaStatus.Paused;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Observe(CodexQuotaSnapshot? quota)
    {
        if (_disposed || !_enabled || quota is null || quota.FetchedAt > _now().AddMinutes(1)
            || Snapshot is { } previous && quota.FetchedAt <= previous.FetchedAt)
        {
            return;
        }

        var reset = Snapshot is { } old && quota.RecoveredSince(old);
        Snapshot = quota.WithResetCreditsFrom(Snapshot).WithoutExpiredResetCredits(_now());
        ScheduleExpiry();
        Status = CodexQuotaStatus.Ready;
        Changed?.Invoke(this, EventArgs.Empty);
        // A replenished quota can mean a reset card was consumed; refresh its metadata once on that event.
        if (reset || Snapshot.AvailableResetCreditCount is null && _now() - _lastDetailsAttempt > TimeSpan.FromMinutes(1))
        {
            _ = RefreshDetailsAsync(afterCurrent: reset);
        }
    }

    public Task RefreshForViewAsync() => RefreshDetailsAsync(afterCurrent: true);

    internal DateTimeOffset? ExpiryDue => _expiryDue;

    private void ScheduleExpiry()
    {
        _expiryTimer.Stop();
        _expiryDue = _enabled ? Snapshot?.NextResetCreditExpiry(_now()) : null;
        if (_expiryDue is not { } at) { return; }
        // Long deadlines can be rescheduled locally; no request is made before the actual expiry.
        _expiryTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp((at - _now()).TotalMilliseconds, 10, TimeSpan.FromDays(20).TotalMilliseconds));
        _expiryTimer.Start();
    }

    internal void CheckExpiry()
    {
        _expiryTimer.Stop();
        if (_disposed || !_enabled || _expiryDue is not { } due) { return; }
        if (_now() < due) { ScheduleExpiry(); return; }
        Snapshot = Snapshot?.WithoutExpiredResetCredits(_now());
        ScheduleExpiry();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RefreshDetailsAsync(afterCurrent: true);
    }

    private async Task RefreshDetailsAsync(bool afterCurrent = false)
    {
        if (_disposed || !_enabled) { return; }
        if (_busy) { _refreshAfterCurrent |= afterCurrent; return; }
        _busy = true;
        _lastDetailsAttempt = _now();
        using var request = new CancellationTokenSource();
        _request = request;
        try
        {
            var result = await _read(request.Token);
            if (_disposed || !_enabled || request.IsCancellationRequested) { return; }
            Snapshot = Snapshot is { } newer && newer.FetchedAt > result.FetchedAt
                ? newer with { AvailableResetCreditCount = result.AvailableResetCreditCount, ResetCredits = result.ResetCredits }
                : result;
            Snapshot = Snapshot.WithoutExpiredResetCredits(_now());
            ScheduleExpiry();
            Status = CodexQuotaStatus.Ready;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            if (_disposed || !_enabled || request.IsCancellationRequested) { return; }
            AppLog.Warn("Codex account details unavailable; waiting for response activity.");
            Status = Snapshot is null ? CodexQuotaStatus.Unavailable : CodexQuotaStatus.Ready;
        }
        finally
        {
            _busy = false;
            if (ReferenceEquals(_request, request)) { _request = null; }
            if (!_disposed) { Changed?.Invoke(this, EventArgs.Empty); }
            if (_refreshAfterCurrent)
            {
                _refreshAfterCurrent = false;
                if (!_disposed && _enabled) { _ = RefreshDetailsAsync(); }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _expiryTimer.Stop();
        _request?.Cancel();
    }
}
