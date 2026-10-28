using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Linux.Platform;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Titles;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace AgentSpeed.Linux.UI;

internal sealed partial class MainWindow : Window
{
    private readonly LinuxApp _app;
    private readonly ThemeBinder _theme;
    private readonly ContentControl _body = new();
    private readonly TextBlock _notice = Text("", 12);
    private readonly ScrollViewer _pageScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, DesktopButton> _navButtons = [];
    private readonly List<(DesktopButton Button, string Key)> _toolButtons = [];
    private readonly Dictionary<SourceKind, SourceRowView> _sourceRows = [];
    private readonly StackPanel _sources = Stack();
    private readonly StackPanel _otherSources = Stack();
    private readonly StackPanel _agents = Stack();
    private Border _root = null!;
    private Border _sidebar = null!;
    private Border _content = null!;
    private TextBlock _activeBadgeText = null!;
    private Border _activeBadge = null!;
    private TextBlock _warningSummary = null!;
    private TextBlock _warningDetails = null!;
    private TextBlock _estimateTag = null!;
    private Border _estimatePill = null!;
    private DesktopButton _warningToggle = null!;
    private Avalonia.Controls.Shapes.Path _warningChevron = null!;
    private bool _warningExpanded;
    private TextBlock _speed = null!;
    private TextBlock _speedUnit = null!;
    private TextBlock _title = null!;
    private TextBlock _context = null!;
    private TextBlock _peakText = null!;
    private TextBlock _trafficText = null!;
    private TextBlock _chartLatest = null!;
    private TextBlock _chartRange = null!;
    private TextBlock _sourcesHeading = null!;
    private TextBlock _agentsHeading = null!;
    private TextBlock _sourcesHint = null!;
    private TextBlock _dashboardTitle = null!;
    private TextBlock _dashboardSubtitle = null!;
    private TextBlock _outputMetric = null!;
    private TextBlock _inputMetric = null!;
    private TextBlock _requestsMetric = null!;
    private TextBlock _costMetric = null!;
    private TextBlock _costTag = null!;
    private TextBlock _sourcesEmpty = null!;
    private TextBlock _agentsEmpty = null!;
    private TextBlock _otherSourcesHeader = null!;
    private TextBlock _historyTitle = null!;
    private TextBlock _historySubtitle = null!;
    private TextBlock _historyChartTitle = null!;
    private TextBlock _historyChartUnit = null!;
    private TextBlock _historyStatus = null!;
    private TextBlock? _networkStatus;
    private TextBlock _settingsSubtitle = null!;
    private TextBlock _brandSubtitle = null!;
    private HeroControl _hero = null!;
    private LineChart _chart = null!;
    private Grid _metricGrid = null!;
    private Grid _sectionsGrid = null!;
    private Grid _dashboardHeader = null!;
    private Border _sourcesCard = null!;
    private Border _agentsCard = null!;
    private Expander _otherSourcesExpander = null!;
    private StackPanel _dashboard = null!;
    private StackPanel _historyPage = null!;
    private TabControl? _settingsTabs;
    private ThemeScope? _settingsThemeScope;
    private readonly ComboBox _historySource = new() { Width = 155 };
    private ComboBox? _historyMetricPicker;
    private readonly TextBox _from = new() { Width = 125, PlaceholderText = "YYYY-MM-DD" };
    private readonly TextBox _to = new() { Width = 125, PlaceholderText = "YYYY-MM-DD" };
    private readonly HistoryChart _historyChart = new() { Height = 240 };
    private readonly StackPanel _historyRows = Stack();
    private readonly List<(Button Button, string English)> _historyButtons = [];
    private readonly Dictionary<DateOnly, Expander> _dayRows = [];
    private readonly Avalonia.Threading.DispatcherTimer _historyRefresh = new() { Interval = LiveDay.RefreshInterval };
    private readonly Avalonia.Threading.DispatcherTimer _liveRefresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private IReadOnlyList<DaySummary> _days = [];
    private SourceKind? _selectedSource;
    private HistoryChartMetric _historyMetric;
    private bool _historyRangeValid;
    private bool _updatingHistorySources;
    private string _page = "dashboard";

    private bool IsDarkTheme => _app.RequestedThemeVariant == ThemeVariant.Light
        ? false
        : _app.RequestedThemeVariant == ThemeVariant.Dark || _app.Accessibility.Current.PrefersDark;

    internal static TextBlock Text(string text, double size = 14) => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    internal static StackPanel Stack(params Control[] children)
    {
        var stack = new StackPanel { Spacing = 12 };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    internal static WrapPanel Row(params Control[] children)
    {
        var row = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private TextBlock ThemedText(string text, double size, TextRole role = TextRole.Primary)
    {
        var block = Text(text, size);
        _theme.On(() => block.Foreground = _theme.Palette.Brush(_theme.Palette.TextFor(role)));
        return block;
    }

    private static void Set(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal)) block.Text = text;
    }

    private string T(string english) => LinuxText.Get(_app.Loc.LanguageCode, english);

    private string Tr(string en, string zh, string ja = "") => LinuxText.Get(_app.Loc.LanguageCode, en);

    private string L(string key) => _app.Loc[key];

    private Button Button(string text, Action action) => DesktopButton(text, action, DesktopButtonKind.Secondary);

    private DesktopButton DesktopButton(string text, Action action, DesktopButtonKind kind)
    {
        var button = new DesktopButton(_theme.Palette, kind, text);
        button.Click += (_, _) => action();
        _theme.On(() => button.SetPalette(_theme.Palette));
        return button;
    }

    private Border Card(Control content) => Card(content, new Thickness(15));

    private Border Card(Control content, Thickness padding)
    {
        var border = new Border
        {
            Child = content,
            Padding = padding,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
        };
        _theme.On(() =>
        {
            border.Background = DesktopStyles.CardBrush(_theme.Palette);
            border.BorderBrush = _theme.Palette.Brush(_theme.Palette.Border);
        });
        return border;
    }

    private Border Inset(Control content, Thickness padding)
    {
        var border = new Border
        {
            Child = content,
            Padding = padding,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
        };
        _theme.On(() =>
        {
            border.Background = _theme.Palette.Brush(_theme.Palette.CardInset);
            border.BorderBrush = _theme.Palette.Brush(_theme.Palette.Border);
        });
        return border;
    }

    private TextBox StyledInput(TextBox field)
    {
        field.CornerRadius = new CornerRadius(8);
        field.Padding = new Thickness(10, 7);
        void Apply()
        {
            field.Background = _theme.Palette.Brush(_theme.Palette.CardInset);
            field.BorderBrush = _theme.Palette.Brush(_theme.Palette.Border);
            field.Foreground = _theme.Palette.PrimaryBrush;
        }
        _theme.On(Apply);
        field.GotFocus += (_, _) => field.BorderBrush = _theme.Palette.AccentBrush;
        field.LostFocus += (_, _) => field.BorderBrush = _theme.Palette.BorderBrush;
        return field;
    }

    public MainWindow(LinuxApp app)
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_TRACE_INPUT") == "1")
            AddHandler(PointerPressedEvent, (_, e) => Console.Error.WriteLine($"PRESS {e.GetPosition(this)} source={e.Source}"),
                Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _app = app;
        _theme = new ThemeBinder(DesktopStyles.PaletteFor(IsDarkTheme, _app.Accessibility.Current.HighContrast));
        Title = "Agent Speed";
        Width = 1120; Height = 820; MinWidth = 780; MinHeight = 560;
        BuildShell();
        BuildDashboard();
        BuildHistory();
        SelectPage("dashboard");
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        _liveRefresh.Tick += (_, _) => { _liveRefresh.Stop(); if (IsVisible) RefreshLive(); };
        _app.Changed += () =>
        {
            if (IsVisible && !_liveRefresh.IsEnabled) _liveRefresh.Start();
        };
        _app.History.Changed += HistoryChanged;
        _app.Traffic.Hub.DaysChanged += HistoryChanged;
        _app.Loc.LanguageChanged += (_, _) => ApplyLanguage();
        _historyRefresh.Tick += (_, _) =>
        {
            if (IsVisible && _page == "history")
            {
                RefreshHistory(); // Resolve relative ranges again after midnight and on resume.
                if (LiveDay.CanRefreshQuietly(_app.History.State)) _app.History.Refresh();
            }
        };
        _historyRefresh.Start();
        Closed += (_, _) => { _historyRefresh.Stop(); _liveRefresh.Stop(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                _app.Traffic.Monitor.SetDetailDemand(IsVisible);
                UpdatePairingTimer();
            }
        };
        Closing += (_, e) => { if (!_app.Quitting) { e.Cancel = true; Hide(); } };
        RefreshLive();
        RefreshHistory();
    }

    private void BuildShell()
    {
        DesktopStyles.ApplyWindow(this, _theme.Palette);
        _root = new Border();
        _theme.On(() => _root.Background = _theme.Palette.Brush(_theme.Palette.Window));
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("188,*") };
        _sidebar = BuildSidebar();
        shell.Children.Add(_sidebar);
        _content = new Border { Child = BuildContent() };
        Grid.SetColumn(_content, 1);
        shell.Children.Add(_content);
        _root.Child = shell;
        Content = _root;
    }

    private Border BuildSidebar()
    {
        var sidebar = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        var sidebarGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(16, 20, 16, 16),
        };
        sidebar.Child = sidebarGrid;
        _theme.On(() =>
        {
            sidebar.Background = _theme.Palette.Brush(_theme.Palette.Sidebar);
            sidebar.BorderBrush = _theme.Palette.Brush(_theme.Palette.SidebarBorder);
        });

        var brand = new Grid { ColumnDefinitions = new ColumnDefinitions("38,*"), ColumnSpacing = 9, Margin = new Thickness(2, 0, 0, 23) };
        var icon = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://agent-speed/Assets/icon.png"))),
            Width = 34,
            Height = 34,
            Stretch = Stretch.UniformToFill,
        };
        brand.Children.Add(icon);
        var brandText = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var name = ThemedText("Agent Speed", 15, TextRole.Primary);
        name.FontWeight = FontWeight.SemiBold;
        brandText.Children.Add(name);
        _brandSubtitle = ThemedText(T("Live agent telemetry"), 9.5, TextRole.Muted);
        brandText.Children.Add(_brandSubtitle);
        Grid.SetColumn(brandText, 1);
        brand.Children.Add(brandText);
        sidebarGrid.Children.Add(brand);

        _navButtons.Clear();
        var navigation = new StackPanel { Spacing = 3 };
        AddNav(navigation, "dashboard", T("Dashboard"));
        AddNav(navigation, "history", L("history.title"));
        AddNav(navigation, "settings", L("settings.title"));
        Grid.SetRow(navigation, 1);
        sidebarGrid.Children.Add(navigation);

        var footer = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        var divider = new Border { Height = 1, Margin = new Thickness(2, 0, 2, 2) };
        _theme.On(() => divider.Background = _theme.Palette.Brush(_theme.Palette.Border));
        footer.Children.Add(divider);
        AddTool(footer, T("Show / hide orbs"), () => _app.Activate("toggle-orbs"));
        AddTool(footer, T("Restore mouse input"), () => _app.Activate("restore-input"));
        var version = ThemedText($"v{_app.Version}", 10, TextRole.Muted);
        version.Margin = new Thickness(2, 9, 0, 0);
        footer.Children.Add(version);
        Grid.SetRow(footer, 3);
        sidebarGrid.Children.Add(footer);
        return sidebar;
    }

    private void AddNav(StackPanel panel, string page, string label)
    {
        var button = new DesktopButton(_theme.Palette, DesktopButtonKind.Nav, label)
        {
            Height = 44,
            Padding = new Thickness(13, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => SelectPage(page);
        _theme.On(() => { button.SetPalette(_theme.Palette); button.Selected = _page == page; });
        _navButtons[page] = button;
        panel.Children.Add(button);
    }

    private void AddTool(StackPanel panel, string label, Action action)
    {
        var button = new DesktopButton(_theme.Palette, DesktopButtonKind.Tool, label)
        {
            Height = 32,
            FontSize = 11.5,
            Padding = new Thickness(7, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => action();
        _theme.On(() => button.SetPalette(_theme.Palette));
        _toolButtons.Add((button, label));
        panel.Children.Add(button);
    }

    private Grid BuildContent()
    {
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(26, 14, 26, 0),
        };
        _notice.FontSize = 12;
        _notice.Margin = new Thickness(0, 0, 0, 10);
        _notice.IsVisible = false;
        _theme.On(() => _notice.Foreground = _theme.Palette.WarningBrush);
        _notice.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) _notice.IsVisible = !string.IsNullOrEmpty(_notice.Text);
        };
        content.Children.Add(_notice);
        Grid.SetRow(_body, 1);
        content.Children.Add(_body);
        return content;
    }

    public void SelectPage(string page)
    {
        _page = page;
        if (page == "settings")
        {
            _body.Content = BuildSettings();
        }
        else
        {
            _pageScroll.Content = page == "history" ? _historyPage : _dashboard;
            _body.Content = _pageScroll;
            _pageScroll.Offset = default;
        }
        UpdatePairingTimer();
        UpdateNavigation();
        _app.Traffic.Monitor.SetDetailDemand(IsVisible);
        RefreshLive();
        if (page == "history") { _app.History.Refresh(); RefreshHistory(); }
    }

    private void UpdateNavigation()
    {
        foreach (var (page, button) in _navButtons)
        {
            button.Selected = page == _page;
            button.SetPalette(_theme.Palette);
        }
    }

    internal int ThemeBindingCount => _theme.BindingCount;

    internal int ThemeScopeCount => _theme.ScopeCount;

    internal DesktopPalette CurrentPalette => _theme.Palette;

    internal void RefreshAccessibilityPalette() => ApplyTheme();

    private void ApplyTheme()
    {
        _theme.Apply(DesktopStyles.PaletteFor(IsDarkTheme, _app.Accessibility.Current.HighContrast));
        DesktopStyles.ApplyWindow(this, _theme.Palette);
        _root.Background = _theme.Palette.Brush(_theme.Palette.Window);
        _sidebar.Background = _theme.Palette.Brush(_theme.Palette.Sidebar);
        _sidebar.BorderBrush = _theme.Palette.Brush(_theme.Palette.SidebarBorder);
        UpdateNavigation();
        ApplyChartTheme();
        foreach (var row in _sourceRows.Values) ApplySourceTheme(row);
        foreach (var row in _agentRows.Values) ApplyAgentTheme(row);
        if (_page == "settings") _body.Content = BuildSettings();
        RefreshLive();
        RefreshHistory();
    }

    private void ApplyLanguage()
    {
        if (_navButtons.TryGetValue("dashboard", out var dash)) dash.Content = T("Dashboard");
        if (_navButtons.TryGetValue("history", out var history)) history.Content = L("history.title");
        if (_navButtons.TryGetValue("settings", out var settings)) settings.Content = L("settings.title");
        _brandSubtitle.Text = T("Live agent telemetry");
        foreach (var (button, key) in _toolButtons) button.Content = T(key);
        RefreshDashboardText();
        _historyTitle.Text = L("history.title");
        _historySubtitle.Text = T("Daily output, cost and traffic from local records.");
        if (_settingsSubtitle is not null) _settingsSubtitle.Text = T("Appearance, sources, titles and privacy.");
        foreach (var (button, english) in _historyButtons) button.Content = T(english);
        var selectedSource = _selectedSource;
        _updatingHistorySources = true;
        try
        {
            _historySource.ItemsSource = new[] { T("All sources") }
                .Concat(Enum.GetValues<SourceKind>().Select(k => L(DisplayText.SourceKey(k)))).ToArray();
            _historySource.SelectedIndex = selectedSource is { } selected ? (int)selected + 1 : 0;
        }
        finally
        {
            _updatingHistorySources = false;
            _selectedSource = selectedSource;
        }
        if (_historyMetricPicker is not null)
        {
            var metric = _historyMetric;
            _historyMetricPicker.ItemsSource = Enum.GetValues<HistoryChartMetric>();
            _historyMetricPicker.SelectedItem = metric;
        }
        _otherSourcesHeader.Text = T("Other sources");
        RefreshLive();
        RefreshHistory();
    }

    private void ApplyChartTheme()
    {
        var p = _theme.Palette;
        _chart.GridBrush = p.Brush(p.ChartGrid);
        _chart.MutedBrush = p.SecondaryBrush;
        _chart.FillAlpha = p.HighContrast ? .35 : p.Dark ? .24 : .18;
        _historyChart.GridBrush = p.Brush(p.ChartGrid);
        _historyChart.MutedBrush = p.SecondaryBrush;
        _historyChart.LineBrush = p.PrimaryBrush;
        _historyChart.InvalidateVisual();
    }

    private Control BuildSettings()
    {
        var selected = _settingsTabs?.SelectedIndex ?? 0;
        _settingsThemeScope?.Dispose();
        _settingsThemeScope = _theme.BeginScope();
        using var themeScope = _theme.UseScope(_settingsThemeScope);
        if (_settingsTabs is not null) _settingsTabs.ItemsSource = null;
        var tabs = new TabControl { TabStripPlacement = Dock.Left, FontSize = 13.5, Padding = new Thickness(0) };
        tabs.ItemsSource = new[]
        {
            Tab(L("settings.general"), General()),
            Tab(L("settings.sources"), Sources()),
            Tab(T("Orbs"), Orbs()),
            Tab(L("settings.cost"), Costs()),
            Tab(T("Titles"), Titles()),
            Tab(T("Network"), Network()),
            Tab(T("Reporting"), Reporting()),
            Tab(T("About"), About()),
        };
        tabs.SelectionChanged += (_, _) => UpdatePairingTimer();
        tabs.SelectedIndex = selected;
        _settingsTabs = tabs;
        UpdatePairingTimer();
        var title = ThemedText(L("settings.title"), 26, TextRole.Primary);
        title.FontWeight = FontWeight.SemiBold;
        _settingsSubtitle = ThemedText(T("Appearance, sources, titles and privacy."), 12, TextRole.Secondary);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 14 };
        layout.Children.Add(Stack(title, _settingsSubtitle));
        Grid.SetRow(tabs, 1);
        layout.Children.Add(tabs);
        return layout;
    }

    private TabItem Tab(string title, Control content) => new()
    {
        Header = Text(title, 13.5),
        Padding = new Thickness(10, 9),
        Content = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(16, 0, 0, 0),
        },
    };

    private CheckBox Toggle(string label, bool value, Action<bool> change)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        box.IsCheckedChanged += (_, _) => change(box.IsChecked == true);
        return box;
    }

    private Control Pick<T>(string label, IEnumerable<T> values, T selected, Action<T> change) where T : notnull
    {
        var options = values.ToArray();
        var combo = new ComboBox { ItemsSource = options, SelectedItem = selected, MinWidth = 180 };
        combo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((value, _) => Text(Choice(value), 14));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is T value) change(value); };
        return Stack(Text(label, 12), combo);
    }

    private string Choice(object? value)
    {
        var key = value switch
        {
            AppearanceMode appearance => "appearance." + appearance.ToString().ToLowerInvariant(),
            SpeedMetric metric => metric == SpeedMetric.Output ? "metric.output" : "metric.total",
            OrbStyle style => "style." + OrbStyles.Key(style),
            LiveUnit unit => "settings.liveUnit." + unit.ToString().ToLowerInvariant(),
            OrbUnit unit => "orbs.unit." + unit.ToString().ToLowerInvariant(),
            HistoryChartMetric metric => HistoryChartMetrics.TitleKey(metric),
            _ => "",
        };
        if (key.Length > 0 && _app.Loc.Table.TryGet(key, _app.Loc.LanguageCode, out var text)) return text;
        if (value is AppLanguage language) return language switch
        {
            AppLanguage.ZhHans => "简体中文",
            AppLanguage.ZhHant => "繁體中文",
            AppLanguage.En => "English",
            AppLanguage.Ja => "日本語",
            _ => LinuxText.Get(_app.Loc.LanguageCode, "System"),
        };
        var english = value?.ToString() ?? "";
        if (_app.Settings.Current.TitleThemes.Theme(english) is { } theme) return theme.Name(_app.Loc);
        if (english == "general") return L("settings.general");
        foreach (var candidate in _app.Loc.Table.Keys)
            if (_app.Loc.Table.TryGet(candidate, "en", out var original) && string.Equals(original, english, StringComparison.OrdinalIgnoreCase))
                return L(candidate);
        return LinuxText.Get(_app.Loc.LanguageCode, english);
    }

    private Control Input(string label, string? value, Action<string> commit, Func<string, string?>? validate = null,
        Func<string, string>? normalize = null)
    {
        var field = StyledInput(new TextBox { Text = value ?? "", MinWidth = 140 });
        var saved = (value ?? "").Trim();
        void Save()
        {
            var next = field.Text?.Trim() ?? "";
            if (validate?.Invoke(next) is { } error) { DataValidationErrors.SetError(field, new ArgumentException(error)); return; }
            DataValidationErrors.ClearErrors(field);
            if (normalize is not null)
            {
                next = normalize(next);
                field.Text = next;
            }
            if (next == saved) return;
            saved = next;
            commit(next);
        }
        field.LostFocus += (_, _) => Save();
        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };
        return Stack(Text(label, 12), field);
    }

    private Control NumberInput(string label, double? value, Action<double?> commit, double min = 0,
        double max = double.MaxValue, bool allowEmpty = false, Func<double, double>? normalize = null)
        => Input(label, value?.ToString(CultureInfo.InvariantCulture),
            v => commit(v.Length == 0 ? null : double.Parse(v, CultureInfo.InvariantCulture)),
            v => allowEmpty && v.Length == 0 ? null
                : !double.TryParse(v, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n)
                    ? T("Enter a valid number.")
                    : n < min || n > max ? T("The value is outside the allowed range.") : null,
            normalize is null ? null : v => v.Length == 0 ? v
                : normalize(double.Parse(v, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));

    private Control General()
    {
        var s = _app.Settings.Current;
        return Stack(
            Pick(L("settings.appearance"), Enum.GetValues<AppearanceMode>(), s.Appearance,
                v => _app.Settings.Update(x => x with { Appearance = v })),
            Pick(T("Language"), Enum.GetValues<AppLanguage>(), s.Language,
                v => { _app.Settings.Update(x => x with { Language = v }); SelectPage("settings"); }),
            Pick(T("Metric"), Enum.GetValues<SpeedMetric>(), s.Metric, v =>
            {
                _app.Settings.Update(x => x with { Metric = v });
                Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectPage("settings"));
            }),
            NumberInput(T("Redline (tok/s)"), s.Redline, v => _app.Settings.Update(x => x.WithRedline(s.Metric, v!.Value)),
                min: HeatMath.MinRedlineFor(s.Metric), max: HeatMath.MaxRedlineFor(s.Metric),
                normalize: v => HeatMath.ClampRedline(s.Metric, v)),
            Pick(L("settings.effects.intensity"), Enum.GetValues<EffectsIntensity>(), s.Effects,
                v => _app.Settings.Update(x => x with { Effects = v })),
            Toggle(L("settings.effects.peaks"), s.CelebratePeaks, v => _app.Settings.Update(x => x with { CelebratePeaks = v })),
            Toggle(L("settings.effects.pulses"), s.ResponsePulses, v => _app.Settings.Update(x => x with { ResponsePulses = v })),
            Pick(T("Top bar network display"), Enum.GetValues<BarTraffic>(), s.Traffic.Bar,
                v => _app.Settings.Update(x => x with { Traffic = x.Traffic with { Bar = v } })),
            StartupToggle(),
            Button(T("Restore all mouse input"), () => _app.Activate("restore-input")),
            Button(T("Enable GNOME top bar"), async () =>
            {
                try
                {
                    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("gnome-extensions")
                    { ArgumentList = { "enable", "agent-speed@zoefix.github.io" }, RedirectStandardError = true });
                    if (process is not null)
                    {
                        await process.WaitForExitAsync();
                        _notice.Text = process.ExitCode == 0 ? "" : T("Log out and back in after installing the package, then enable the extension.");
                    }
                }
                catch (System.ComponentModel.Win32Exception) { _notice.Text = T("GNOME Extensions is unavailable."); }
            }));
    }

    private CheckBox StartupToggle(Action<bool>? apply = null)
    {
        apply ??= LinuxStartup.Set;
        var box = new CheckBox { Content = T("Start at login"), IsChecked = LinuxStartup.Enabled };
        var restoring = false;
        box.IsCheckedChanged += (_, _) =>
        {
            if (restoring) return;
            var enabled = box.IsChecked == true;
            try { apply(enabled); _notice.Text = ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                restoring = true;
                try { box.IsChecked = !enabled; }
                finally { restoring = false; }
                _notice.Text = T("Unable to change start at login. Check your configuration folder permissions.");
            }
        };
        return box;
    }

    private Control Sources()
    {
        var panel = Stack(Text(T("Only local usage records are read. Empty folders use automatic discovery."), 12));
        foreach (var group in Enum.GetValues<SourceKind>().GroupBy(RootProperty))
        {
            var kind = group.First();
            var value = (string?)typeof(AppSettings).GetProperty(group.Key)?.GetValue(_app.Settings.Current);
            var body = Stack();
            foreach (var source in group)
                body.Children.Add(Toggle(L(DisplayText.SourceKey(source)), _app.Settings.Current.IsSourceEnabled(source),
                    v => _app.Settings.Update(s => s.WithSourceEnabled(source, v))));
            body.Children.Add(Input(T("Folder"), value, v =>
                {
                    _app.Settings.Update(s => WithRoot(s, kind, v.Length == 0 ? null : v));
                }, v => v.Length > 0 && !Path.IsPathFullyQualified(v) ? T("Choose an absolute folder path.") : null));
            panel.Children.Add(Card(body));
        }
        return panel;
    }

    private static string RootProperty(SourceKind kind) => kind switch
    {
        SourceKind.ClaudeCode or SourceKind.ClaudeDesktop => "ClaudeRoot",
        SourceKind.DeepSeek => "DshRoot",
        SourceKind.WorkBuddyAi => "WorkBuddyAiRoot",
        _ => kind + "Root",
    };

    private static AppSettings WithRoot(AppSettings s, SourceKind k, string? root) => k switch
    {
        SourceKind.Codex => s with { CodexRoot = root },
        SourceKind.ClaudeCode or SourceKind.ClaudeDesktop => s with { ClaudeRoot = root },
        SourceKind.DeepSeek => s with { DshRoot = root },
        SourceKind.OpenCode => s with { OpenCodeRoot = root },
        SourceKind.Gemini => s with { GeminiRoot = root },
        SourceKind.Omp => s with { OmpRoot = root },
        SourceKind.Antigravity => s with { AntigravityRoot = root },
        SourceKind.WorkBuddy => s with { WorkBuddyRoot = root },
        SourceKind.WorkBuddyAi => s with { WorkBuddyAiRoot = root },
        SourceKind.ZCode => s with { ZCodeRoot = root },
        SourceKind.Kimi => s with { KimiRoot = root },
        SourceKind.GrokBuild => s with { GrokBuildRoot = root },
        _ => s,
    };

    private Control Orbs()
    {
        var s = _app.Settings.Current;
        var panel = Stack(
            Toggle(T("Show orbs"), s.ShowOrbs, v => _app.Settings.Update(x => x.WithShowOrbs(v))),
            Pick(T("Name plates"), Enum.GetValues<OrbNamesMode>(), s.OrbNames, v => _app.Settings.Update(x => x with { OrbNames = v })),
            Button(T("Add orb"), () =>
            {
                var result = OrbEditing.Add(_app.Settings.Current.Orbs, null, SourceSet.Everything);
                if (result is not null)
                {
                    var id = result.Created.FirstOrDefault();
                    var document = id is null ? result.Document : _app.PlaceNewOrb(result.Document, id, null).Document;
                    _app.Settings.Update(x => x with { Orbs = document });
                }
                SelectPage("settings");
            }));
        foreach (var o in s.Orbs.Orbs)
        {
            void Change(Func<OrbDefinition, OrbDefinition> change) => _app.Settings.Update(x => x.Orbs.Find(o.Id) is { } current
                ? x with { Orbs = x.Orbs.Replace(change(current)) } : x);
            var sources = Stack(Text(T("Tracked sources (none selected = all)"), 12));
            foreach (var kind in Enum.GetValues<SourceKind>())
                sources.Children.Add(Toggle(kind.ToString(), o.TrackedSources?.Contains(kind) == true,
                    v => Change(d => d with { Sources = OrbSources.Of(v
                        ? (d.TrackedSources ?? SourceSet.Empty).With(kind)
                        : (d.TrackedSources ?? SourceSet.Empty).Without(kind)) })));
            panel.Children.Add(Card(Stack(
                Input(T("Name"), o.Name, v => Change(d => d with { Name = v })),
                Pick(T("Style"), OrbStyles.All, o.Style, v =>
                {
                    Change(d => d.WithStyle(v));
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectPage("settings"));
                }),
                Pick(T("Title theme"), new[] { "general" }.Concat(s.TitleThemes.AllThemes.Select(t => t.Id)), o.TitleTheme ?? "general",
                    v => Change(d => d with { TitleTheme = v == "general" ? null : v })),
                NumberInput(T("Redline (blank follows general)"),
                    s.Metric == SpeedMetric.Output ? o.RedlineOutput : o.RedlineTotal,
                    v => Change(d => d.WithRedline(s.Metric, v)),
                    min: HeatMath.MinRedlineFor(s.Metric), max: HeatMath.MaxRedlineFor(s.Metric), allowEmpty: true,
                    normalize: v => HeatMath.ClampRedline(s.Metric, v)),
                NumberInput(T("Size"), o.Size, v => Change(d => d.WithSize(v!.Value)), min: OrbStyles.MinSizeFor(o.Style), max: OrbStyles.MaxSize),
                NumberInput(T("Opacity (0.4–1)"), o.Opacity, v => Change(d => d with { Opacity = v!.Value }), min: .4, max: 1),
                Pick(T("Unit"), Enum.GetValues<OrbUnit>(), o.Unit, v => Change(d => d with { Unit = v })),
                Pick(T("Traffic"), Enum.GetValues<OrbTraffic>(), o.Traffic, v => Change(d => d with { Traffic = v })),
                Toggle(T("Visible"), o.Visible, v => Change(d => d with { Visible = v })),
                Toggle(T("Always on top"), o.AlwaysOnTop, v => Change(d => d with { AlwaysOnTop = v })),
                Toggle(T("Click through"), o.ClickThrough, v => Change(d => d with { ClickThrough = v })),
                Toggle(T("Fade while idle"), o.FadeWhenIdle, v => Change(d => d with { FadeWhenIdle = v })),
                new Expander { Header = T("Sources"), Content = sources },
                Row(
                    Button(T("Duplicate"), () =>
                    {
                        var result = OrbEditing.Duplicate(_app.Settings.Current.Orbs, o.Id);
                        if (result is not null)
                        {
                            var id = result.Created.FirstOrDefault();
                            var document = id is null ? result.Document : _app.PlaceNewOrb(result.Document, id, o.Id).Document;
                            _app.Settings.Update(x => x with { Orbs = document });
                        }
                        SelectPage("settings");
                    }),
                    Button(T("Remove"), () =>
                    {
                        var result = OrbEditing.Remove(_app.Settings.Current.Orbs, o.Id);
                        if (result is not null) _app.Settings.Update(x => x with { Orbs = result });
                        SelectPage("settings");
                    })))));
        }
        return panel;
    }

    private Control Costs()
    {
        var cost = _app.Settings.Current.Cost;
        var panel = Stack(
            Pick(T("Currency"), CurrencyTable.Bundled.Currencies.Select(c => c.Code), cost.Currency,
                v => _app.Settings.Update(s => s with { Cost = s.Cost with { Currency = v } })),
            Pick(T("Live display"), Enum.GetValues<LiveUnit>(), cost.LiveUnit,
                v => _app.Settings.Update(s => s with { Cost = s.Cost with { LiveUnit = v } })),
            Toggle(T("Update model prices daily"), cost.SyncPrices,
                v => _app.Settings.Update(s => s with { Cost = s.Cost with { SyncPrices = v } })),
            Toggle(T("Show speed and cost in the top bar"), _app.Settings.Current.BarSecondary,
                v => _app.Settings.Update(s => s with { BarSecondary = v })),
            Toggle(T("Include subscriptions in costs"), cost.IncludeSubscriptions,
                v => _app.Settings.Update(s => s with { Cost = s.Cost with { IncludeSubscriptions = v } })),
            Text(L("settings.cost.subscriptions"), 18));
        foreach (var source in Enum.GetValues<SourceKind>())
            panel.Children.Add(Toggle(source.ToString(), cost.IsSubscription(source), v =>
                _app.Settings.Update(s => s with
                {
                    Cost = s.Cost with
                    {
                        Subscriptions = v ? s.Cost.Subscriptions.With(source) : s.Cost.Subscriptions.Without(source),
                    },
                })));
        return panel;
    }

    private Control Titles()
    {
        var doc = _app.Settings.Current.TitleThemes;
        var panel = Stack(Pick(T("Title theme"), doc.AllThemes.Select(t => t.Id), doc.Selected,
            v => _app.Settings.Update(s => s with { TitleThemes = s.TitleThemes with { Selected = v } })));
        foreach (var theme in doc.AllThemes)
            panel.Children.Add(Text($"{theme.Name(_app.Loc)}: {string.Join(" · ", theme.Labels(_app.Loc))}", 12));
        panel.Children.Add(Button(T("Add custom theme"), () =>
        {
            if (!doc.CanAddCustom) return;
            var custom = new CustomTitleSet(Guid.NewGuid().ToString("D"), T("My titles"), TitlePreset.Vehicles);
            _app.Settings.Update(s => s with
            {
                TitleThemes = s.TitleThemes with
                {
                    Custom = s.TitleThemes.Custom.Add(custom),
                    Selected = custom.Id,
                },
            });
            SelectPage("settings");
        }));
        foreach (var custom in doc.Custom)
        {
            void Change(Func<CustomTitleSet, CustomTitleSet> change) => _app.Settings.Update(s => s with
            {
                TitleThemes = s.TitleThemes with
                {
                    Custom = s.TitleThemes.Custom.Select(c => c.Id == custom.Id ? change(c) : c).ToImmutableArray(),
                },
            });
            var fields = Stack(Input(T("Theme name"), custom.Name, v => Change(c => c with { Name = v })));
            for (var level = 0; level <= TitlePresets.LevelCount; level++)
            {
                var n = level;
                fields.Children.Add(Input($"{level}", custom.Field(level), v => Change(c => c.WithField(n, v))));
            }
            panel.Children.Add(new Expander { Header = custom.Name, Content = fields });
        }
        return panel;
    }

    private Control Network()
    {
        var s = _app.Settings.Current;
        _networkStatus = Text("", 12);
        RefreshNetworkStatus();
        return Stack(
            Toggle(T("Allow network connections"), s.NetworkEnabled,
                v => _app.Settings.Update(x => x with { NetworkEnabled = v })),
            Text(T("This switch controls updates, public prices, exchange rates, reporting and profile requests. Local measurements keep running."), 12),
            Toggle(T("Measure network traffic"), s.Traffic.Enabled,
                v => _app.Settings.Update(x => x with { Traffic = x.Traffic with { Enabled = v } })),
            Toggle(T("Include tool subprocesses"), s.Traffic.IncludeTools,
                v => _app.Settings.Update(x => x with { Traffic = x.Traffic with { IncludeTools = v } })),
            Pick(T("Traffic units"), Enum.GetValues<TrafficUnit>(), s.Traffic.Unit,
                v => _app.Settings.Update(x => x with { Traffic = x.Traffic with { Unit = v } })),
            _networkStatus,
            Button(T("Install traffic helper…"), () => _ = RunHelperInstaller()),
            Button(T("Retry sampler"), _app.Traffic.Monitor.Retry));
    }

    private void RefreshNetworkStatus()
    {
        if (_networkStatus is not { } status) return;
        var snapshot = _app.Traffic.Hub.Snapshot;
        status.Text = $"{T("Process traffic")}: {snapshot.ProcessState.Kind}\n{snapshot.ProcessError}";
    }

    private async Task RunHelperInstaller()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pkexec")
            { ArgumentList = { "/usr/lib/agent-speed/install-traffic-helper" }, UseShellExecute = false });
            if (process is not null)
            {
                await process.WaitForExitAsync();
                _notice.Text = process.ExitCode == 0
                    ? T("Traffic helper installed.")
                    : T("Helper installation did not complete.");
            }
            _app.Traffic.Monitor.Retry();
        }
        catch (System.ComponentModel.Win32Exception) { _notice.Text = T("Install the .deb package to enable the helper installer."); }
    }
}
