using System.Runtime.ExceptionServices;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AgentSpeed.Core;
using AgentSpeed.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Services;
using AgentSpeed.Settings;
using AgentSpeed.Settings.Pages;
using AgentSpeed.Updates;

namespace AgentSpeed.Windows.Tests;

public sealed class ApiPageTests
{
    [Fact]
    public void BindingInitializationAndGroupUncheckingDoNotWriteBackAndActivationSelectsThePane()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001 // Match the application's supported Fluent theme setup.
                app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/AgentSpeed;component/Theme/Styles.xaml", UriKind.Relative),
                });
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var settings = new SettingsService(dispatcher);
                settings.MakeEphemeral();
                using var preview = new PreviewService(new SpeedHub(), dispatcher);
                using var updates = new UpdateService(dispatcher, settings, "0.0.0");
                var loc = new Localizer(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
                using var cost = new CostService(dispatcher, settings, loc, "0.0.0",
                    Path.Combine(Path.GetTempPath(), "agent-speed-api-test-" + Guid.NewGuid().ToString("N")));
                var model = new SettingsViewModel(settings, preview, loc, _ => null, updates,
                    () => { }, () => { }, () => false, () => { }, cost);
                model.ReduceMotion = true;
                for (var reopen = 0; reopen < 4; reopen++)
                {
                    model.ApiShowsLan = reopen % 2 == 1;
                    var settingsWindow = new SettingsWindow(model) { ShowInTaskbar = false };
                    try
                    {
                        settingsWindow.Show();
                        Drain();
                        settingsWindow.ShowPage("report");
                        Drain();
                        var api = (ApiPage)settingsWindow.FindName("ReportPage");
                        AssertSelection(api, model, (RadioButton)api.FindName("ReportSegmentButton"),
                            (RadioButton)api.FindName("LanSegmentButton"));
                    }
                    finally { settingsWindow.Close(); }
                    var page = new ApiPage { DataContext = model };
                    var window = new Window { Content = page, Width = 700, Height = 500, ShowInTaskbar = false };
                    try
                    {
                        window.Show();
                        Drain();
                        var report = (RadioButton)page.FindName("ReportSegmentButton");
                        var lan = (RadioButton)page.FindName("LanSegmentButton");
                        AssertSelection(page, model, report, lan);
                        // Unchecking is a side effect of the group, not an instruction to select the other pane.
                        var before = model.ApiShowsLan;
                        var selected = before ? lan : report;
                        selected.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                        Assert.Equal(before, model.ApiShowsLan);
                        report.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateTarget();
                        lan.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateTarget();
                        AssertSelection(page, model, report, lan);
                        for (var i = 0; i < 20; i++)
                        {
                            var next = !model.ApiShowsLan;
                            var button = next ? lan : report;
                            if (i % 2 == 0)
                            {
                                // UI Automation selects without raising Click.
                                var peer = new RadioButtonAutomationPeer(button);
                                ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).Select();
                            }
                            else
                            {
                                button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            }
                            Drain();
                            Assert.Equal(next, model.ApiShowsLan);
                            AssertSelection(page, model, report, lan);
                            model.ApiShowsLan = !next;
                            Drain();
                            AssertSelection(page, model, report, lan);
                        }
                    }
                    finally
                    {
                        window.Close();
                        page.DataContext = null;
                    }
                }
                app.Shutdown();
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "API pane test did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void AssertSelection(ApiPage page, SettingsViewModel model, RadioButton report, RadioButton lan)
    {
        Assert.Equal(model.ApiShowsLan, lan.IsChecked);
        Assert.Equal(!model.ApiShowsLan, report.IsChecked);
        Assert.Equal(model.ApiShowsLan ? Visibility.Visible : Visibility.Collapsed,
            ((FrameworkElement)page.FindName("LanSegment")).Visibility);
        Assert.Equal(model.ApiShowsLan ? Visibility.Collapsed : Visibility.Visible,
            ((FrameworkElement)page.FindName("ReportSegment")).Visibility);
    }
}
