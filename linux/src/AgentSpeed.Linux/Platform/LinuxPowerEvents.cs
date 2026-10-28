using Tmds.DBus;

namespace AgentSpeed.Linux.Platform;

[DBusInterface("org.freedesktop.login1.Manager")]
public interface ILoginManager : IDBusObject
{
    Task<IDisposable> WatchPrepareForSleepAsync(Action<bool> handler);
}

internal sealed class LinuxPowerEvents(LinuxApp app) : IDisposable
{
    private Connection? _connection;
    private IDisposable? _watch;
    private volatile bool _disposed;
    public async Task StartAsync()
    {
        try
        {
            var connection = new Connection(Address.System!);
            _connection = connection;
            await connection.ConnectAsync();
            if (_disposed) return;
            var proxy = connection.CreateProxy<ILoginManager>("org.freedesktop.login1", new ObjectPath("/org/freedesktop/login1"));
            var watch = await proxy.WatchPrepareForSleepAsync(sleeping => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || app.Quitting) return;
                if (sleeping) LinuxOrbPositionLifecycle.Suspend(app.FlushOrbPositions, app.Traffic.Monitor.Flush, app.Settings.Flush);
                else { app.Cost.Prices.OnResume(); app.Cost.Rates.OnResume(); app.SyncOrbs(); }
            }));
            if (_disposed) watch.Dispose();
            else _watch = watch;
        }
        catch (Exception ex) { if (!_disposed) AgentSpeed.App.AppLog.Warn("Suspend notifications are unavailable.", ex); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watch?.Dispose(); _watch = null;
        _connection?.Dispose(); _connection = null;
    }
}
