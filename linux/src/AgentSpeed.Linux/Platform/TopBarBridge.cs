using System.Text.Json;
using Tmds.DBus;

namespace AgentSpeed.Linux.Platform;

[DBusInterface("io.github.zoefix.AgentSpeed1")]
public interface IAgentSpeedBridge : IDBusObject
{
    Task<string> GetSnapshotAsync();
    Task ActivateAsync(string action);
}

internal sealed class TopBarBridge(LinuxApp app) : IAgentSpeedBridge, IDisposable
{
    internal const string BusName = "io.github.zoefix.AgentSpeed";
    internal static readonly ObjectPath Path = new("/io/github/zoefix/AgentSpeed");
    private Connection? _connection;
    private volatile bool _disposed;
    public ObjectPath ObjectPath => Path;
    public async Task StartAsync()
    {
        var connection = new Connection(Address.Session!);
        _connection = connection;
        await connection.ConnectAsync();
        if (_disposed) return;
        await connection.RegisterObjectAsync(this);
        if (_disposed) return;
        await connection.RegisterServiceAsync(BusName);
    }
    public async Task<string> GetSnapshotAsync() => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
    {
        var s = app.Speed.Snapshot;
        var traffic = app.Settings.Current.Traffic.Bar switch
        { AgentSpeed.Presentation.Settings.BarTraffic.Machine => app.Traffic.Hub.Snapshot.MachineLive,
          AgentSpeed.Presentation.Settings.BarTraffic.Agents => app.Traffic.Hub.Snapshot.Live(null, app.Settings.Current.Traffic.IncludeTools), _ => null };
        var label = app.Settings.Current.Cost.LiveUnit == AgentSpeed.Presentation.Settings.LiveUnit.Cost
            ? app.Cost.Display.Live(s).InlineText : $"{s.Live.ValueFor(app.Settings.Current.Metric):0.0} tok/s";
        var todayCost = AgentSpeed.Presentation.Cost.TodayCostSummary.From(s, app.Cost.Display).AmountText;
        if (app.Settings.Current.BarSecondary && app.Settings.Current.Cost.LiveUnit == AgentSpeed.Presentation.Settings.LiveUnit.Tokens)
            label += " · " + app.Cost.Display.Live(s).InlineText + " · " + todayCost;
        return JsonSerializer.Serialize(new { schema = 1, speed = s.Live.ValueFor(app.Settings.Current.Metric),
            label,
            agents = s.GeneratingCount, cost = app.Cost.Display.Live(s).InlineText, todayCost,
            rx = traffic?.Rx, tx = traffic?.Tx, bits = app.Settings.Current.Traffic.Bits, enabled = app.Settings.Current.ShowOrbs });
    });
    public async Task ActivateAsync(string action)
    {
        if (action == "quit")
        {
            // Let D-Bus send its reply before shutdown disposes the connection.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                Avalonia.Threading.DispatcherTimer.RunOnce(() => app.Activate(action), TimeSpan.FromMilliseconds(100)));
            return;
        }
        if (action is "dashboard" or "history" or "settings" or "toggle-orbs" or "restore-input")
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => app.Activate(action));
    }
    public static async Task<bool> ActivateExistingAsync(string action)
    {
        var failure = await ExistingInstanceActivation.RetryAsync(async () =>
        {
            using var connection = new Connection(Address.Session!); await connection.ConnectAsync();
            await connection.CreateProxy<IAgentSpeedBridge>(BusName, Path).ActivateAsync(action);
        });
        if (failure is null) return true;
        AgentSpeed.App.AppLog.Warn("Could not activate the running instance.", failure);
        return false;
    }
    internal static async Task CheckAsync()
    {
        using var connection = new Connection(Address.Session!); await connection.ConnectAsync();
        var proxy = connection.CreateProxy<IAgentSpeedBridge>(BusName, Path);
        var snapshot = await proxy.GetSnapshotAsync();
        using var document = JsonDocument.Parse(snapshot);
        if (document.RootElement.GetProperty("schema").GetInt32() != 1 || document.RootElement.GetProperty("speed").GetDouble() <= 0)
            throw new InvalidOperationException("The top bar bridge did not deliver preview data.");
        await proxy.ActivateAsync("history");
        await proxy.ActivateAsync("dashboard");
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose(); _connection = null;
    }
}
