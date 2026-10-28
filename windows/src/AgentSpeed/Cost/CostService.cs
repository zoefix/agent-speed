#if LINUX
using AgentSpeed.Linux.Platform;
#else
using System.Windows.Threading;
#endif
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Services;
#if !LINUX
using Microsoft.Win32;
#endif

namespace AgentSpeed.Cost;

/// <summary>
/// The cost feature's shared state (METRICS 9, NETWORK 3–4, DESIGN 9): the price book the live engine and the history
/// share, the display formatter every surface uses, and the two daily syncs under the network master switch.
/// UI thread only.
/// </summary>
internal sealed class CostService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsService _settings;
    private readonly HttpClientTransport _transport = new();
    private bool _started;
    private bool _disposed;

    public CostService(Dispatcher dispatcher, SettingsService settings, Localizer localizer, string version, string historyStorePath)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        var userAgent = HttpFetchRequest.UserAgentFor(version, OperatingSystem.IsLinux() ? "Linux" : "Windows");
        var tables = new List<PriceTable> { PriceBook.Bundled };
        if (PriceSyncController.LoadStoredTable(AppDataDirectory) is { } stored)
        {
            tables.Add(stored);
        }

        Book = new PriceBook(tables);
        Prices = new PriceSyncController(dispatcher, Book, AppDataDirectory, _transport, userAgent)
        {
            NetworkEnabled = settings.Current.NetworkEnabled,
        };
        var repaired = HistoryService.RestorePricing(Book, historyStorePath);
        if (repaired.Count > 0)
        {
            AppLog.Info($"Price bindings without a valid table rebound to the current table: {repaired.Count}.");
        }

        Rates = new ExchangeRateController(dispatcher, AppDataDirectory, _transport, userAgent, CurrencyTable.Bundled);
        Display = new CostDisplay(localizer, CurrencyTable.Bundled, Rates.Rates) { Settings = settings.Current.Cost };
        Rates.Changed += (_, _) =>
        {
            Display.Rates = Rates.Rates;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Prices.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Display.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Anything a cost surface shows changed: settings, rates, the price table or its sync status.</summary>
    public event EventHandler? Changed;

    /// <summary><c>%LOCALAPPDATA%\AgentSpeed</c> (NETWORK 3 storage).</summary>
    public static string AppDataDirectory =>
        #if LINUX
        AgentSpeed.Linux.Platform.LinuxPaths.Data;
#else
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.ProductFolder);
#endif

    /// <summary>Shared by the live engine and the daily history (METRICS 9.7).</summary>
    public PriceBook Book { get; }

    public CostDisplay Display { get; }

    public PriceSyncController Prices { get; }

    public ExchangeRateController Rates { get; }

    public bool NetworkEnabled => _settings.Current.NetworkEnabled;

    /// <summary>Starts the schedules (after the window and monitor are up; nothing here blocks the launch).</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;

#if !LINUX
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
#endif
        Apply(_settings.Current, asAfterLaunch: false);
    }

    public void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        Display.Settings = current.Cost;
        Prices.NetworkEnabled = current.NetworkEnabled;
        if (!_started)
        {
            return;
        }

        if (previous.NetworkEnabled != current.NetworkEnabled || previous.Cost.SyncPrices != current.Cost.SyncPrices
            || previous.Cost.Currency != current.Cost.Currency)
        {
            Apply(current, asAfterLaunch: !previous.NetworkEnabled && current.NetworkEnabled);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {

#if !LINUX
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
#endif
        }

        Prices.Dispose();
        Rates.Dispose();
        _transport.Dispose();
    }

    private void Apply(AppSettings settings, bool asAfterLaunch)
    {
        Prices.NetworkEnabled = settings.NetworkEnabled;
        Prices.SetEnabled(settings.NetworkEnabled && settings.Cost.SyncPrices, asAfterLaunch);
        Rates.Update(settings.Cost.Currency, settings.NetworkEnabled);
    }

#if !LINUX
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                Prices.OnResume();
                Rates.OnResume();
            }
        });
    }
#endif
}
