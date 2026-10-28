using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.History;
using AgentSpeed.Linux.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AgentSpeed.Linux.UI;

internal sealed partial class MainWindow
{
    private SourceStatus? _showcaseCodexStatus;

    private void Capture(string name)
    {
        this.GetLayoutManager()?.ExecuteLayoutPass();
        using var target = new RenderTargetBitmap(new PixelSize((int)Width, (int)Height));
        target.Render(this);
        target.Save(Path.Combine(LinuxPaths.Cache, name + ".png"), new PngBitmapEncoderOptions());
    }

    /// <summary>Rich synthetic data for the design-review screenshots; never reads a transcript or the network.</summary>
    private void PublishShowcaseData()
    {
        var now = DateTimeOffset.Now;
        var history = Enumerable.Range(0, 120).Select(i =>
        {
            var measured = Math.Max(0, 63 + 28 * Math.Sin(i * .145) + 13 * Math.Sin(i * .47 + 1.1));
            var estimated = i >= 116 ? 5.5 : 0;
            return new HistoryPoint(now.AddSeconds(i - 119), new Rate(measured, measured * 5.2), new Rate(estimated, estimated * 5.2));
        }).ToArray();
        history[^1] = new HistoryPoint(now, new Rate(97.4, 97.4 * 5.2), default);
        var sources = new SourceStatus[]
        {
            new(SourceKind.Codex, true, "~/.codex", true, 8, new(42.5, 212), new TokenTotals(302, 520_000, 200_000, 0, 594_322, 0), 2)
                { TodayCost = new(14_780_000) },
            new(SourceKind.ClaudeCode, true, "~/.claude", true, 5, new(31.8, 158), new TokenTotals(418, 610_000, 210_000, 0, 330_569, 0), 1)
                { TodayCost = new(9_420_000) },
            new(SourceKind.ClaudeDesktop, true, "~/.claude", true, 2, new(16.2, 80), new TokenTotals(87, 140_000, 40_000, 0, 87_400, 0), 1)
                { TodayCost = new(3_180_000) },
            new(SourceKind.DeepSeek, true, "~/.dsh", true, 1, new(6.9, 34), new TokenTotals(144, 72_000, 12_000, 0, 98_210, 0), 1)
                { TodayCost = new(2_040_000) },
            new(SourceKind.Gemini, true, "~/.gemini", true, 1, default, new TokenTotals(28, 30_000, 4_000, 0, 25_000, 0), 0)
                { TodayCost = new(860_000) },
            new(SourceKind.OpenCode, true, "~/.opencode", true, 0, default, TokenTotals.Zero, 0),
            new(SourceKind.Omp, true, "~/.omp", true, 0, default, TokenTotals.Zero, 0),
            new(SourceKind.Antigravity, true, "~/.antigravity", false, 0, default, TokenTotals.Zero, 0),
            new(SourceKind.WorkBuddy, true, "~/.workbuddy", false, 0, default, TokenTotals.Zero, 0),
            new(SourceKind.WorkBuddyAi, true, "~/.workbuddy-ai", false, 0, default, TokenTotals.Zero, 0),
            new(SourceKind.ZCode, true, "~/.zcode", false, 0, default, TokenTotals.Zero, 0),
            new(SourceKind.Kimi, true, "~/.kimi", false, 0, default, TokenTotals.Zero, 0),
            new(SourceKind.GrokBuild, true, "~/.grok", false, 0, default, TokenTotals.Zero, 0),
        };
        _showcaseCodexStatus = sources[0];
        var agents = new AgentStatus[]
        {
            new("preview/atlas", SourceKind.Codex, "atlas-api", "gpt-6.1-sol", AgentPhase.Generating, false,
                new(42.5, 195), 42.5, now),
            new("preview/docs", SourceKind.Codex, "docs-site", "gpt-6.1-sol", AgentPhase.Tools, false,
                new(8.4, 41), 8.4, now.AddSeconds(-2)),
            new("preview/speed", SourceKind.ClaudeCode, "agent-speed", "claude-opus-5-5", AgentPhase.Generating, false,
                new(31.8, 150), 31.8, now.AddSeconds(-1)),
            new("preview/design", SourceKind.ClaudeDesktop, "design-review", "claude-opus-5-5", AgentPhase.Generating, false,
                new(16.2, 78), 16.2, now.AddSeconds(-4)),
        };
        _app.Speed.PublishPreview(SpeedSnapshot.Empty with
        {
            Timestamp = now,
            State = MonitorState.Running,
            Live = new Rate(97.4, 495.6),
            LiveEstimatedShare = .04,
            PeakToday = new Rate(184.3, 921.5),
            PeakTodayAt = now.AddHours(-4).AddMinutes(-57),
            AverageToday = 68.2,
            Today = new TokenTotals(1_220, 1_480_000, 1_220_000, 0, 954_307, 0),
            Sources = sources,
            Agents = agents,
            GeneratingCount = 3,
            ToolsCount = 1,
            History = history,
            Warnings = [new MonitorWarning.UnfinalizedUsage(2)],
        });
        RefreshLive();
    }

    private async Task PublishShowcaseAsync()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            PublishShowcaseData();
            await Task.Delay(80);
            RefreshLive();
            if (_app.Speed.Snapshot.Sources.Count > 5) return;
        }
        throw new InvalidOperationException("The synthetic dashboard snapshot did not reach the UI hub.");
    }

    private async Task AssertNetworkStatusRefreshAsync()
    {
        SelectPage("settings");
        _settingsTabs!.SelectedIndex = 5;
        var status = _networkStatus ?? throw new InvalidOperationException("The network status control was not built.");
        var original = _app.Traffic.Hub.Snapshot;
        try
        {
            _app.Traffic.Hub.Publish(original with
            {
                Enabled = true,
                ProcessState = ProcessTrafficState.Running(1),
                ProcessError = null,
            });
            await Task.Delay(140);
            if (!ReferenceEquals(status, _networkStatus) || !status.Text!.Contains("Running"))
                throw new InvalidOperationException("The network process status did not refresh in place while settings was open.");

            const string error = "Synthetic sampler failure";
            _app.Traffic.Hub.Publish(original with
            {
                Enabled = true,
                ProcessState = ProcessTrafficState.Unavailable(TrafficUnavailableReason.Sampler),
                ProcessError = error,
            });
            await Task.Delay(140);
            if (!status.Text!.Contains("Unavailable") || !status.Text.Contains(error))
                throw new InvalidOperationException("The network process error did not refresh while settings was open.");
        }
        finally
        {
            _app.Traffic.Hub.Publish(original);
            await Task.Delay(140);
        }
    }

    private async Task AssertHeroTimerStopsWhenHiddenAsync()
    {
        if (!_hero.AnimationTimerEnabled)
            throw new InvalidOperationException($"The visible dashboard hero animation timer is not running ({_hero.AnimationTimerStatus}).");
        SelectPage("history");
        await Task.Delay(80);
        if (_hero.AnimationTimerEnabled)
            throw new InvalidOperationException($"Leaving the dashboard kept its detached hero animation timer running ({_hero.AnimationTimerStatus}).");
        SelectPage("dashboard");
        await Task.Delay(80);
        if (!_hero.AnimationTimerEnabled)
            throw new InvalidOperationException($"Returning to the dashboard did not restart its hero animation timer ({_hero.AnimationTimerStatus}).");
        Hide();
        await Task.Delay(80);
        if (_hero.AnimationTimerEnabled)
            throw new InvalidOperationException($"The hidden window kept the dashboard hero animation timer running ({_hero.AnimationTimerStatus}).");
        Show();
        await Task.Delay(80);
        if (!_hero.AnimationTimerEnabled)
            throw new InvalidOperationException($"The dashboard hero animation timer did not resume when the window became visible ({_hero.AnimationTimerStatus}).");
    }

    private void PublishSourceGroupingData(bool produced)
    {
        var snapshot = _app.Speed.Snapshot;
        var showcase = _showcaseCodexStatus ?? throw new InvalidOperationException("The showcase source is not ready.");
        var codex = showcase with
        {
            Live = produced ? showcase.Live : default,
            Today = produced ? showcase.Today : showcase.Today with { Output = 0, Requests = 0 },
            ActiveAgents = produced ? showcase.ActiveAgents : 0,
        };
        var sources = snapshot.Sources.Select(source => source.Kind == SourceKind.Codex ? codex : source).ToArray();
        _app.Speed.PublishPreview(snapshot with
        {
            Timestamp = DateTimeOffset.Now,
            Sources = sources,
            GeneratingCount = produced ? snapshot.GeneratingCount : Math.Max(0, snapshot.GeneratingCount - 1),
        });
        RefreshLive();
    }

    private async Task PublishSourceGroupingAsync(bool produced)
    {
        PublishSourceGroupingData(produced);
        await Task.Delay(50);
        RefreshLive();
    }

    private void ForceLayout() => this.GetLayoutManager()?.ExecuteLayoutPass();

    private void AssertSourceParent(SourceKind kind, Panel parent, string label)
    {
        if (!_sourceRows.TryGetValue(kind, out var row) || !ReferenceEquals(row.Button.Parent, parent))
            throw new InvalidOperationException($"{label}: source {kind} is not parented in the expected panel.");
    }

    private static void AssertBrush(IBrush? brush, Color color, string label)
    {
        if (brush is not ISolidColorBrush solid || solid.Color != color)
            throw new InvalidOperationException($"{label} does not use the current palette color.");
    }

    private void AssertThemeApplied()
    {
        var palette = CurrentPalette;
        AssertBrush(_dashboardTitle.Foreground, palette.TextPrimary, "dashboard title");
        AssertBrush(_speedEyebrow.Foreground, palette.TextMuted, "live-speed label");
        AssertBrush(_sourcesHint.Foreground, palette.TextMuted, "sources hint");
        if (_page == "settings" && _settingsSubtitle is not null)
            AssertBrush(_settingsSubtitle.Foreground, palette.TextSecondary, "settings subtitle");
    }

    private void AssertVisibleInViewport(Control control, string label)
    {
        ForceLayout();
        var origin = control.TranslatePoint(new Point(0, 0), _pageScroll);
        if (origin is null)
            throw new InvalidOperationException($"{label} is not in the dashboard viewport tree.");
        var bottom = origin.Value.Y + control.Bounds.Height;
        if (origin.Value.Y < 0 || bottom > _pageScroll.Viewport.Height + 1)
            throw new InvalidOperationException($"{label} is below the first screen (bottom {bottom:0.0}, viewport {_pageScroll.Viewport.Height:0.0}).");
    }

    private void AssertFirstScreenDensity()
    {
        ForceLayout();
        if (_sources.Children.Count < 2 || _agents.Children.Count < 1)
            throw new InvalidOperationException("The dashboard has fewer than two sources or one agent.");
        AssertVisibleInViewport(_sources.Children[1], "second source row");
        AssertVisibleInViewport(_agents.Children[0], "first agent row");
        if (_warningCard.Bounds.Height is < 32 or > 36)
            throw new InvalidOperationException($"The usage notice header is {_warningCard.Bounds.Height:0.0} high, expected 32–36.");
    }

    private void AssertDashboardLayout(bool twoColumn, bool requireDensity)
    {
        ForceLayout();
        if (DashboardIsTwoColumn != twoColumn)
            throw new InvalidOperationException($"Dashboard column layout was {DashboardIsTwoColumn}, expected {twoColumn}.");
        if (DashboardHasHorizontalOverflow)
            throw new InvalidOperationException($"Dashboard overflows horizontally at {Width:0}x{Height:0}.");
        if (_sourcesCard.Bounds.Width <= 0 || _agentsCard.Bounds.Width <= 0)
            throw new InvalidOperationException("A dashboard section did not receive layout.");
        if (_sourceRows.Count < 2) throw new InvalidOperationException("The dashboard did not list the synthetic sources.");
        if (_sources.Children.Count < 4 || _agents.Children.Count < 3)
            throw new InvalidOperationException("The dashboard source or agent list is incomplete.");
        if (!_otherSourcesExpander.IsVisible || _otherSources.Children.Count == 0)
            throw new InvalidOperationException("The expandable other-sources section is empty.");
        if (requireDensity) AssertFirstScreenDensity();
    }

    private void AssertHistoryMetric(HistoryChartMetric metric, string expectedTitle, string expectedUnit,
        Func<DaySummary, double> expectedValue)
    {
        _historyMetricPicker!.SelectedItem = metric;
        RefreshHistory();
        if (_historyMetric != metric)
            throw new InvalidOperationException($"History metric selection did not switch to {metric}.");
        if (_historyChartTitle.Text != expectedTitle)
            throw new InvalidOperationException($"History {metric} title was '{_historyChartTitle.Text}', expected '{expectedTitle}'.");
        if (_historyChartUnit.Text != expectedUnit)
            throw new InvalidOperationException($"History {metric} unit was '{_historyChartUnit.Text}', expected '{expectedUnit}'.");
        if (metric == HistoryChartMetric.Output && _historyChartUnit.Text == L("unit.tokS"))
            throw new InvalidOperationException("The daily output chart still uses a speed unit.");
        var bars = _historyChart.Bars;
        foreach (var day in _days)
        {
            var index = bars.Range.IndexOf(day.Day);
            if (!bars.HasData(index)) throw new InvalidOperationException($"History {metric} lost a recorded day.");
            var actual = bars.Total(index) + bars.TotalBelow(index);
            if (Math.Abs(actual - expectedValue(day)) > .0001)
                throw new InvalidOperationException($"History {metric} no longer matches daily semantics: {actual}.");
        }
    }

    private void AssertHistoryChartMetrics()
    {
        AssertHistoryMetric(HistoryChartMetric.Output, L(HistoryChartMetrics.TitleKey(HistoryChartMetric.Output)),
            L("history.summary.output"), day => day.Output);
        AssertHistoryMetric(HistoryChartMetric.Cost, L(HistoryChartMetrics.TitleKey(HistoryChartMetric.Cost)),
            _app.Cost.Display.Symbol,
            day => new HistoryMoney(_app.Cost.Display).Value(day.Cost.MicroUsd));
        AssertHistoryMetric(HistoryChartMetric.Traffic, L(HistoryChartMetrics.TitleKey(HistoryChartMetric.Traffic)),
            "↓ / ↑", day => (double)(day.Traffic?.Shown.Sum ?? 0));
        AssertHistoryMetric(HistoryChartMetric.Output, L(HistoryChartMetrics.TitleKey(HistoryChartMetric.Output)),
            L("history.summary.output"), day => day.Output);
    }

    private async Task AssertHistoryRecoveryAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        SelectHistoryRange(HistoryRangeKind.Today);
        if (!_historyChart.HasData || _historyChart.Bars.Count != 1)
            throw new InvalidOperationException("Today has usage but the single-day chart is empty.");
        var layout = HistoryChart.CalculateLayout(_historyChart.Bars, 760, 240);
        if (layout.Segments.Count == 0) throw new InvalidOperationException("The single-day chart has no visible bar.");
        Capture("smoke-history-today");
        RefreshHistoryForDay(today.AddDays(1));
        if (_to.Text != HistoryRanges.FormatDay(today.AddDays(1)))
            throw new InvalidOperationException("Today stopped following the local day after midnight.");
        _from.Text = _to.Text = HistoryRanges.FormatDay(today.AddDays(-3));
        RefreshHistoryForDay(today.AddDays(2));
        if (_to.Text != HistoryRanges.FormatDay(today.AddDays(-3)))
            throw new InvalidOperationException("A custom range was changed at midnight.");
        SelectHistoryRange(HistoryRangeKind.Days30);
        var beforeScopes = ThemeScopeCount;
        for (var i = 0; i < 4; i++) RefreshHistory();
        if (ThemeScopeCount != beforeScopes) throw new InvalidOperationException("History refresh retained old theme scopes.");
        var preservedDays = _app.History.Days;
        _app.History.Publish(HistoryState.Analyzing(.4, 4, 10));
        await Task.Delay(80);
        if (!_historyProgress.IsVisible || !_historyChart.HasData)
            throw new InvalidOperationException("Analysis hid already available history or lost its progress state.");
        _app.History.Publish(HistoryState.Failed("Synthetic retryable error"));
        await Task.Delay(80);
        if (!_historyChart.HasData || !_historyStatus.Text!.Contains(T("History could not be refreshed. Showing the last available data; use Refresh to retry.")))
            throw new InvalidOperationException("A failed refresh discarded cached history or gave no retry guidance.");
        _app.History.Publish(HistoryState.Failed("Synthetic error without cached history"), []);
        await Task.Delay(80);
        var retry = T("History could not be loaded. Use Refresh to retry.");
        if (_historyChart.HasData || _historyChart.EmptyText != retry || !_historyStatus.Text!.Contains(retry))
            throw new InvalidOperationException("A load failure was presented as genuinely empty history.");
        _from.Text = _to.Text = "0001-01-01";
        RefreshHistory();
        if (!_historyRangeValid || _historyChart.Bars.Range != DayRange.Single(DateOnly.MinValue))
            throw new InvalidOperationException("A valid boundary date could not be displayed safely.");
        SelectHistoryRange(HistoryRangeKind.Days30);
        _app.History.Publish(HistoryState.Ready(DateTimeOffset.Now), preservedDays);
        await Task.Delay(80);
        RefreshHistory();
        _app.Settings.Update(settings => settings with { Appearance = AgentSpeed.Presentation.Settings.AppearanceMode.Light });
        await Task.Delay(60);
        this.GetLayoutManager()?.ExecuteLayoutPass();
        if (_summaryViews.Any(view => view.Value.TextWrapping != TextWrapping.NoWrap))
            throw new InvalidOperationException("History summary values can wrap into multiple lines.");
        Capture("smoke-history-light");
        Width = 780; Height = 560;
        await Task.Delay(60);
        Capture("smoke-history-compact");
        Width = 1120; Height = 820;
        _app.Settings.Update(settings => settings with { Appearance = AgentSpeed.Presentation.Settings.AppearanceMode.Dark });
        await Task.Delay(60);
    }

    private void AssertSharedSourceFolderEditor()
    {
        var original = _app.Settings.Current;
        try
        {
            var panel = (StackPanel)Sources();
            var bodies = panel.Children.OfType<Border>().Select(c => (StackPanel)c.Child!).ToArray();
            if (bodies.Length != Enum.GetValues<SourceKind>().Select(RootProperty).Distinct().Count())
                throw new InvalidOperationException("Sources contain duplicate editors for a shared folder.");
            var claude = bodies.Single(b => b.Children.Count == 3);
            if (claude.Children[0] is not CheckBox code || claude.Children[1] is not CheckBox desktop)
                throw new InvalidOperationException("The Claude folder does not retain separate source toggles.");
            code.IsChecked = false; desktop.IsChecked = true;
            if (_app.Settings.Current.IsSourceEnabled(SourceKind.ClaudeCode)
                || !_app.Settings.Current.IsSourceEnabled(SourceKind.ClaudeDesktop))
                throw new InvalidOperationException("Claude source toggles are not independent.");
            var input = (TextBox)((StackPanel)claude.Children[2]).Children[1];
            var path = Path.Combine(LinuxPaths.Cache, "synthetic-claude-records");
            input.Text = path;
            input.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
            if (_app.Settings.Current.ClaudeRoot != path)
                throw new InvalidOperationException("The shared Claude source folder was not saved.");
        }
        finally { _app.Settings.Update(_ => original); }
    }

    private void AssertSourceFolderValidation()
    {
        var original = _app.Settings.Current.CodexRoot;
        var panel = (StackPanel)Sources();
        var card = (Border)panel.Children[1];
        var body = (StackPanel)card.Child!;
        var input = (TextBox)((StackPanel)body.Children[1]).Children[1];
        void Submit(string text)
        {
            input.Text = text;
            input.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        }
        Submit("relative/records");
        if (!DataValidationErrors.GetHasErrors(input) || _app.Settings.Current.CodexRoot != original)
            throw new InvalidOperationException("A relative source folder was not rejected inline.");
        Submit(original ?? "");
        if (DataValidationErrors.GetHasErrors(input))
            throw new InvalidOperationException("Restoring a source folder retained its validation error.");
        var absolute = Path.Combine(LinuxPaths.Cache, "synthetic-records");
        Submit(absolute);
        if (_app.Settings.Current.CodexRoot != absolute || DataValidationErrors.GetHasErrors(input))
            throw new InvalidOperationException("A valid absolute source folder was not accepted.");
        Submit(original ?? "");
    }

    private async Task AssertHistoryExportAsync()
    {
        var originalSource = _historySource.SelectedIndex;
        _historySource.SelectedIndex = 1;
        RefreshHistory();
        var expected = HistoryCsv.Build(_days, _selectedSource, new HistoryMoney(_app.Cost.Display),
            _app.Traffic.Hub.Since is not null);
        var output = new MemoryStream();
        await ExportToAsync(false, async () =>
        {
            // A filter can change while the native save picker is open.
            _historySource.SelectedIndex = 0;
            await Task.Yield();
            return output;
        });
        if (System.Text.Encoding.UTF8.GetString(output.ToArray()) != expected)
            throw new InvalidOperationException("CSV export did not retain the filter selected when saving began.");
        await ExportToAsync(false, () => Task.FromException<Stream?>(new UnauthorizedAccessException("Synthetic file denial")));
        if (_notice.Text != T("Unable to export CSV. Choose a writable file and try again."))
            throw new InvalidOperationException("A CSV file error was not reported.");
        await ExportToAsync(false, () => Task.FromResult<Stream?>(new MemoryStream([], writable: false)));
        if (string.IsNullOrEmpty(_notice.Text)) throw new InvalidOperationException("A read-only CSV target was accepted.");
        await ExportToAsync(false, () => Task.FromResult<Stream?>(new MemoryStream()));
        if (!string.IsNullOrEmpty(_notice.Text)) throw new InvalidOperationException("A successful CSV retry retained its error.");
        _historySource.SelectedIndex = originalSource;
    }

    private async Task AssertRedlineSettingsAsync()
    {
        var original = _app.Settings.Current;
        try
        {
            foreach (var metric in new[] { SpeedMetric.Output, SpeedMetric.Total })
            {
                _app.Settings.Update(s => s with { Metric = metric });
                var previous = _app.Settings.Current.Redline;
                var general = (StackPanel)General();
                await Task.Delay(40);
                var input = (TextBox)((StackPanel)general.Children[3]).Children[1];
                input.Text = "1";
                input.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
                if (!DataValidationErrors.GetHasErrors(input) || _app.Settings.Current.Redline != previous)
                    throw new InvalidOperationException("A redline outside the shared metric bounds was accepted.");
                input.Text = metric == SpeedMetric.Output ? "125" : "21555";
                input.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
                var expected = metric == SpeedMetric.Output ? 150d : 22000d;
                if (_app.Settings.Current.Redline != expected || input.Text != expected.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("The redline input disagrees with the normalized saved setting.");
            }
        }
        finally { _app.Settings.Update(_ => original); }
        SelectPage("history");
    }

    private async Task AssertLanguageKeepsHistoryFilterAsync()
    {
        SelectPage("history");
        await Task.Delay(80);
        _historySource.SelectedIndex = (int)SourceKind.DeepSeek + 1;
        RefreshHistory();
        var selected = _selectedSource;
        if (selected != SourceKind.DeepSeek) throw new InvalidOperationException("The history source filter did not apply.");
        _app.Settings.Update(settings => settings with { Language = AgentSpeed.Presentation.Localization.AppLanguage.ZhHant });
        await Task.Delay(120);
        if (_page != "history" || _selectedSource != selected || _historySource.SelectedIndex != (int)SourceKind.DeepSeek + 1)
            throw new InvalidOperationException("Changing the language reset the history source filter.");
        _app.Settings.Update(settings => settings with { Language = AgentSpeed.Presentation.Localization.AppLanguage.ZhHans });
        await Task.Delay(120);
        if (_selectedSource != selected) throw new InvalidOperationException("Restoring the language reset the history source filter.");
    }

    private async Task AssertThemeScopesBoundedAsync()
    {
        // Rebuilding the settings page disposes its previous scope instead of retaining every old control.
        SelectPage("settings");
        await Task.Delay(60);
        var baselineScopes = ThemeScopeCount;
        var baselineBindings = ThemeBindingCount;
        for (var i = 0; i < 5; i++)
        {
            SelectPage("settings");
            _settingsTabs!.SelectedIndex = i % 8;
            await Task.Delay(40);
        }
        if (ThemeScopeCount > baselineScopes)
            throw new InvalidOperationException("Rebuilding settings leaked theme scopes.");
        if (ThemeBindingCount > baselineBindings + 8)
            throw new InvalidOperationException("Rebuilding settings retained stale theme bindings.");
        AssertThemeApplied();

        // A synthetic agent gains one scope and removes it again; the same is checked for a source row.
        SelectPage("dashboard");
        await PublishShowcaseAsync();
        var beforeAgentScopes = ThemeScopeCount;
        var snapshot = _app.Speed.Snapshot;
        var temp = new AgentStatus("preview/temporary", SourceKind.Codex, "temporary-project", "gpt-6.1-sol",
            AgentPhase.Generating, false, new Rate(9, 45), 9, DateTimeOffset.Now);
        _app.Speed.PublishPreview(snapshot with { Timestamp = DateTimeOffset.Now, Agents = [.. snapshot.Agents, temp] });
        await Task.Delay(50);
        RefreshLive();
        if (ThemeScopeCount != beforeAgentScopes + 1)
            throw new InvalidOperationException("Adding an agent did not register exactly one disposable theme scope.");
        await PublishShowcaseAsync();
        if (ThemeScopeCount != beforeAgentScopes)
            throw new InvalidOperationException("Removing an agent did not release its theme scope.");

        var beforeSourceScopes = ThemeScopeCount;
        _app.Speed.PublishPreview(_app.Speed.Snapshot with
        {
            Timestamp = DateTimeOffset.Now,
            Sources = _app.Speed.Snapshot.Sources.Where(source => source.Kind != SourceKind.Kimi).ToArray(),
        });
        await Task.Delay(50);
        RefreshLive();
        if (_sourceRows.ContainsKey(SourceKind.Kimi) || ThemeScopeCount != beforeSourceScopes - 1)
            throw new InvalidOperationException("Removing a source did not release its theme scope.");
        await PublishShowcaseAsync();
        if (!_sourceRows.ContainsKey(SourceKind.Kimi) || ThemeScopeCount != beforeSourceScopes)
            throw new InvalidOperationException("Restoring a source did not restore its theme scope.");
    }

    private void AssertKeyboardFocus()
    {
        var nav = _navButtons["history"];
        if (!nav.Focus(NavigationMethod.Tab) || !nav.IsFocused)
            throw new InvalidOperationException("An unselected navigation item cannot receive keyboard focus.");
        AssertBrush(nav.BorderBrush, CurrentPalette.Accent, "focused navigation border");

        var source = _sourceRows[SourceKind.Codex].Button;
        if (!source.Focus(NavigationMethod.Tab) || !source.IsFocused)
            throw new InvalidOperationException("A source row cannot receive keyboard focus.");
        AssertBrush(source.BorderBrush, CurrentPalette.Accent, "focused source border");
        _navButtons["dashboard"].Focus(NavigationMethod.Tab);
    }

    internal async Task CheckSurfacesAsync()
    {
        _app.PausePreviewForSmoke();
        _app.Settings.Update(settings => settings with
        {
            Language = AgentSpeed.Presentation.Localization.AppLanguage.ZhHans,
            Appearance = AgentSpeed.Presentation.Settings.AppearanceMode.Dark,
        });
        Width = 1120; Height = 820;
        SelectPage("dashboard");
        await Task.Delay(160);
        await AssertHeroTimerStopsWhenHiddenAsync();
        await PublishShowcaseAsync();
        await Task.Delay(2200); // Let the original vehicle transition settle; no stale cross-fade in the public screenshot.
        RefreshLive();
        if (_app.Speed.Snapshot.Live.Output <= 0) throw new InvalidOperationException("Preview never reached the UI hub.");
        AssertDashboardLayout(twoColumn: true, requireDensity: true);
        AssertThemeApplied();
        if (!_warningCard.IsVisible || !_warningToggle.IsVisible)
            throw new InvalidOperationException("The usage notice is not visible on the dashboard.");
        if (!_estimatePill.IsVisible) throw new InvalidOperationException("The estimate marker is missing from the usage notice.");
        Capture("smoke-dashboard-zh-dark");
        Capture("smoke-dashboard");

        await PublishShowcaseAsync();
        _pageScroll.Offset = new Vector(0, 2000);
        await Task.Delay(100);
        Capture("smoke-dashboard-sources");
        _pageScroll.Offset = default;
        await Task.Delay(60);

        // The compact disclosure keeps its details available.
        _warningToggle.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(30);
        if (!_warningDetails.IsVisible) throw new InvalidOperationException("The usage notice details did not expand.");
        _warningToggle.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(30);
        if (_warningDetails.IsVisible) throw new InvalidOperationException("The usage notice details did not collapse.");

        // A source moving other → main → other keeps its control instance and remains clickable.
        await PublishSourceGroupingAsync(produced: false);
        AssertSourceParent(SourceKind.Codex, _otherSources, "before production");
        var codexRow = _sourceRows[SourceKind.Codex];
        var codexButton = codexRow.Button;
        await PublishSourceGroupingAsync(produced: true);
        AssertSourceParent(SourceKind.Codex, _sources, "after production");
        if (!ReferenceEquals(codexButton, _sourceRows[SourceKind.Codex].Button))
            throw new InvalidOperationException("Moving a source between panels recreated its button.");
        codexButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        if (_page != "history" || _selectedSource != SourceKind.Codex)
            throw new InvalidOperationException("A moved source did not filter history when clicked.");
        SelectPage("dashboard");
        await Task.Delay(80);
        await PublishSourceGroupingAsync(produced: false);
        AssertSourceParent(SourceKind.Codex, _otherSources, "after losing today's output");
        if (!ReferenceEquals(codexButton, _sourceRows[SourceKind.Codex].Button))
            throw new InvalidOperationException("Moving a source back to other recreated its button.");
        await PublishShowcaseAsync();

        var stable = _sourceRows.First();
        var stableButton = stable.Value.Button;
        for (var i = 0; i < 5; i++) { PublishShowcaseData(); await Task.Delay(30); RefreshLive(); }
        if (!ReferenceEquals(stableButton, _sourceRows[stable.Key].Button))
            throw new InvalidOperationException("A live refresh replaced a clickable source row.");

        await AssertThemeScopesBoundedAsync();

        SelectPage("history");
        await Task.Delay(100);
        _historySource.SelectedIndex = 0;
        await PublishShowcaseAsync();
        var historyBars = _historyChart.Bars;
        if (historyBars.Count != historyBars.Range.DayCount)
            throw new InvalidOperationException("The history chart lost its calendar date slots.");
        for (var index = 0; index < historyBars.Count; index++)
            if (!double.IsFinite(historyBars.Total(index)))
                throw new InvalidOperationException("The history chart contains a non-finite value.");
        if (_days.Any(day => !historyBars.HasData(historyBars.Range.IndexOf(day.Day))))
            throw new InvalidOperationException("The history chart and the day list disagree.");
        AssertHistoryChartMetrics();
        await AssertHistoryRecoveryAsync();
        await AssertHistoryExportAsync();
        var day = _dayRows.First();
        day.Value.IsExpanded = true;
        RefreshHistory();
        if (!ReferenceEquals(day.Value, _dayRows[day.Key]) || !day.Value.IsExpanded)
            throw new InvalidOperationException("History refresh lost its expanded row.");
        var date = _from.Text;
        _from.Text = "2099-01-01";
        RefreshHistory();
        if (_historyRangeValid) throw new InvalidOperationException("An inverted date range was accepted.");
        _from.Text = date;
        RefreshHistory();

        var commits = 0;
        var field = (TextBox)((StackPanel)Input("test", "initial", _ => commits++)).Children[1];
        field.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        if (commits != 0) throw new InvalidOperationException("An untouched input was submitted.");
        field.Text = "changed";
        field.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        field.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        if (commits != 1) throw new InvalidOperationException("Enter and blur did not commit exactly once.");
        var numeric = (TextBox)((StackPanel)NumberInput("number", 1, _ => commits++)).Children[1];
        numeric.Text = "NaN";
        numeric.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        if (!DataValidationErrors.GetHasErrors(numeric) || commits != 1)
            throw new InvalidOperationException("An invalid numeric setting was accepted.");
        numeric.Text = "1";
        numeric.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        if (DataValidationErrors.GetHasErrors(numeric) || commits != 1)
            throw new InvalidOperationException("Restoring the saved numeric value retained a stale validation error.");
        await AssertRedlineSettingsAsync();
        var startupAttempts = 0;
        var startup = StartupToggle(_ => { startupAttempts++; throw new UnauthorizedAccessException("Synthetic autostart denial"); });
        var initialStartup = startup.IsChecked;
        startup.IsChecked = initialStartup != true;
        if (startup.IsChecked != initialStartup || startupAttempts != 1 || string.IsNullOrEmpty(_notice.Text))
            throw new InvalidOperationException("An autostart write failure did not restore its toggle and show an error.");
        _notice.Text = "";
        AssertSourceFolderValidation();
        AssertSharedSourceFolderEditor();

        await AssertLanguageKeepsHistoryFilterAsync();
        _historySource.SelectedIndex = 0;
        await PublishShowcaseAsync();
        Capture("smoke-history");

        SelectPage("dashboard");
        Width = 780; Height = 560;
        await Task.Delay(220);
        await PublishShowcaseAsync();
        AssertDashboardLayout(twoColumn: false, requireDensity: false);
        Capture("smoke-dashboard-compact");

        _app.Settings.Update(settings => settings with { Appearance = AgentSpeed.Presentation.Settings.AppearanceMode.Light });
        Width = 1120; Height = 820;
        await Task.Delay(260);
        if (_app.RequestedThemeVariant != Avalonia.Styling.ThemeVariant.Light)
            throw new InvalidOperationException("The light theme did not reach the window.");
        await PublishShowcaseAsync();
        AssertDashboardLayout(twoColumn: true, requireDensity: true);
        AssertThemeApplied();
        Capture("smoke-dashboard-light");

        _app.Settings.Update(settings => settings with { Appearance = AgentSpeed.Presentation.Settings.AppearanceMode.Dark });
        await Task.Delay(160);
        SelectPage("history");
        await Task.Delay(160);
        _historySource.SelectedIndex = 0;
        await PublishShowcaseAsync();
        Capture("smoke-history");

        SelectPage("settings");
        Width = 780; Height = 560;
        for (var index = 0; index < 8; index++)
        {
            _settingsTabs!.SelectedIndex = index;
            await Task.Delay(80);
            foreach (var scroll in _settingsTabs.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.Viewport.Width > 0))
                if (scroll.Extent.Width > scroll.Viewport.Width + 1)
                    throw new InvalidOperationException($"Settings tab {index} overflows horizontally.");
            Capture($"smoke-settings-{index}-compact");
        }
        await AssertNetworkStatusRefreshAsync();

        Width = 1120; Height = 820;
        SelectPage("dashboard");
        SelectPage("settings");
        _settingsTabs!.SelectedIndex = 0;
        await Task.Delay(160);
        Capture("smoke-settings");
        await AssertThemeScopesBoundedAsync();

        _app.Settings.Update(settings => settings with
        {
            Language = AgentSpeed.Presentation.Localization.AppLanguage.ZhHans,
            Appearance = AgentSpeed.Presentation.Settings.AppearanceMode.Dark,
        });
        SelectPage("settings");
        _settingsTabs!.SelectedIndex = 0;
        await Task.Delay(160);
        AssertThemeApplied();
        Capture("smoke-settings-zh");

        SelectPage("dashboard");
        await Task.Delay(120);
        AssertKeyboardFocus();
        _app.Activate("restore-input");
    }
}
