using System.Net.NetworkInformation;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Settings;
using Microsoft.Win32;

namespace AgentSpeed.Services;

/// <summary>
/// The remote hosts of the app (REMOTE 2, 4.4): keeps the coordinator in step with the settings (<c>remoteHosts</c>, the
/// network master switch), retries at once after the computer wakes or its network comes back, and passes time zone
/// changes on. The settings page and pickers of a later version read <see cref="Coordinator"/>.
/// </summary>
internal sealed class RemoteHostsService : IDisposable
{
    private readonly RemoteCoordinator _coordinator;
    private bool _disposed;

    public RemoteHostsService(LiveMonitor monitor, string appVersion, PriceBook pricing)
    {
        RemoteLog.SetSink(message => AppLog.Info("Remote: " + message));
        _coordinator = new RemoteCoordinator(monitor, appVersion, pricing, typeof(RemoteHostsService).Assembly);
        _coordinator.AttachHistory(History);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public RemoteCoordinator Coordinator => _coordinator;

    /// <summary>The settings' hosts and master switch; applied only when they changed.</summary>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed)
        {
            return;
        }

        var names = settings.RemoteHosts.DisplayNames();
        History.SetHosts([.. settings.RemoteHosts.Hosts.Select(h => (h.Id, names[h.Id]))]);
        _coordinator.Update(settings.RemoteHosts, settings.NetworkEnabled, settings.RemoteQuotaSwitches);
    }

    /// <summary>
    /// The hosts' daily history (REMOTE 8): created with the service, so what the hosts send before the history service starts
    /// waits there; hand it to the <see cref="HistoryService"/>.
    /// </summary>
    public RemoteHistorySync History { get; } = new();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _coordinator.Dispose();
        RemoteLog.SetSink(null);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !_disposed)
        {
            _coordinator.NoteWake();
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            TimeZoneInfo.ClearCachedData();
            _coordinator.NoteTimeZoneChange();
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable && !_disposed)
        {
            _coordinator.NoteNetworkAvailable();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (!_disposed && NetworkInterface.GetIsNetworkAvailable())
        {
            _coordinator.NoteNetworkAvailable();
        }
    }
}
