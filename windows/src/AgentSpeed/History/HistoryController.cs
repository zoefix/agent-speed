using System.Windows;
using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Modes;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Services;
using AgentSpeed.Theme;

namespace AgentSpeed.History;

/// <summary>
/// Owns the single Usage History window: opens or focuses it with a filter, remembers its frame and its own
/// include-subscriptions choice, follows the theme and language, and (developer option <c>--preview-history</c>) fills
/// the hub with generated days.
/// </summary>
internal sealed class HistoryController : IDisposable
{
    private const int PreviewSteps = 12;
    private const int PreviewFiles = 340;

    private readonly HistoryHub _hub;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly Localizer _loc;
    private readonly CostDisplay _cost;
    private readonly Dispatcher _dispatcher;
    private HistoryWindow? _window;
    private HistoryViewModel? _model;
    private DispatcherTimer? _preview;
    private bool _disposed;

    public HistoryController(HistoryHub hub, SettingsService settings, ThemeService theme, Localizer loc, CostDisplay cost)
    {
        _hub = hub;
        _settings = settings;
        _theme = theme;
        _loc = loc;
        _cost = cost;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsOpen => _window is not null;

    /// <summary>Observed network traffic by day (METRICS 10.9), shown with the traffic metric; set by the app.</summary>
    public Core.Traffic.TrafficHub? Traffic { get; set; }

    /// <summary>The price book shared with the engine and the history: its current table gives unpriced requests their reasons.</summary>
    public Core.Pricing.PriceBook? Prices { get; set; }

    /// <summary>The open window's content, for the developer capture.</summary>
    public FrameworkElement? CaptureContent => _window?.Content as FrameworkElement;

    /// <summary>Some traffic day is stored: "Clear history" says it goes too (DESIGN 10).</summary>
    public bool HasTrafficHistory => Traffic?.Days.Count > 0;

    /// <summary>Opens the window (or brings it forward) showing <paramref name="source"/>, or every source when null.</summary>
    /// <param name="traffic">Select the traffic metric (DESIGN 10).</param>
    public void Open(SourceKind? source, bool traffic = false)
    {
        if (_disposed)
        {
            return;
        }

        if (_window is null || _model is null)
        {
            var model = new HistoryViewModel(_hub, _loc, _cost, _dispatcher, SaveIncludeSubscriptions, Traffic,
                () => _settings.Current.Traffic.IncludeTools, () => _settings.Current.EnabledSourceSet(),
                () => Prices?.Current ?? Core.Pricing.PriceBook.Bundled, AppPaths.Version);
            model.SetSource(source);
            model.RememberModeKind(_settings.Current.HistoryModeKind, kind => _settings.Update(s => s with { HistoryModeKind = kind }));
            model.RememberPerfSort(_settings.Current.HistoryPerfSort, sort => _settings.Update(s => s with { HistoryPerfSort = sort }));
            var window = new HistoryWindow(model, Appearance)
            {
                Language = AppFonts.XmlLanguageFor(_loc.LanguageCode),
            };
            window.Closing += (_, _) => SaveFrame(window);
            window.Closed += (_, _) =>
            {
                model.Dispose();
                if (ReferenceEquals(_window, window))
                {
                    _window = null;
                    _model = null;
                }
            };
            window.RestoreFrame(_settings.Current.HistoryWindow);
            _model = model;
            _window = window;
            window.Show();
        }
        else
        {
            _model.SetSource(source);
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _model.RefreshLiveDay();
        }

        if (traffic)
        {
            _model.SetMetric(Presentation.History.HistoryChartMetric.Traffic);
        }

        _window.Activate();
    }

    /// <summary>The include-tools setting or the enabled sources changed: the window's traffic counts change with them.</summary>
    public void OnTrafficSettingsChanged() => _model?.RefreshTraffic();

    /// <summary>Developer option: publishes generated history as a short simulated analysis (about 3 s).</summary>
    public void StartPreview()
    {
        StopPreview();
        var days = PreviewHistory.Days(DateTime.Now);
        var newest = DateTime.Now.Date;
        var step = 0;
        _hub.Publish(HistoryState.Analyzing(0, 0, PreviewFiles), []);
        _preview = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            step++;
            if (step >= PreviewSteps)
            {
                StopPreview();
                _hub.Publish(HistoryState.Ready(DateTimeOffset.Now), days);
                return;
            }

            var progress = step / (double)PreviewSteps;
            var oldest = newest.AddDays(-Math.Ceiling(120 * progress));
            var known = days.Where(d => DateTime.TryParse(d.Day, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) && date >= oldest).ToArray();
            _hub.Publish(HistoryState.Analyzing(progress, (int)(PreviewFiles * progress), PreviewFiles), known);
        }, _dispatcher);
        _preview.Start();
    }

    public void OnThemeChanged() => _window?.ApplyTheme(Appearance);

    /// <summary>The window's motion and contrast, and how its effort and speed glyphs draw.</summary>
    private CapsuleAppearance Appearance =>
        new(_theme.IsDark, _theme.HighContrast, _theme.ReduceMotion, _settings.Current.Effects != EffectsIntensity.Off);

    public void OnLanguageChanged()
    {
        if (_window is null || _model is null)
        {
            return;
        }

        _window.Language = AppFonts.XmlLanguageFor(_loc.LanguageCode);
        _model.Relocalize(_loc);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPreview();
        _window?.Close();
    }

    private void StopPreview()
    {
        _preview?.Stop();
        _preview = null;
    }

    /// <summary>The window remembers its own <c>history.includeSubscriptions</c> choice (DESIGN 9).</summary>
    private void SaveIncludeSubscriptions(bool include) =>
        _settings.Update(s => s with { Cost = s.Cost with { HistoryIncludeSubscriptions = include } });

    private void SaveFrame(HistoryWindow window)
    {
        if (window.CaptureFrame() is { } frame)
        {
            _settings.Update(s => s with { HistoryWindow = frame });
        }
    }
}
