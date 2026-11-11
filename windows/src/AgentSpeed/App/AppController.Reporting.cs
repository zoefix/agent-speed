using System.Windows;
using AgentSpeed.Controls;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Dashboard;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Reporting;
using AgentSpeed.Settings;
using AgentSpeed.Theme;

namespace AgentSpeed.App;

/// <summary>
/// Live reporting's surfaces (DESIGN 11): the settings pane, the consent dialog, the preview window, the dashboard's
/// invitation and footer indicator, the quick menu item and the About and Network Traffic texts.
/// </summary>
internal sealed partial class AppController
{
    private ReportController? _report;
    private ReportConsentDialog? _consentDialog;
    private ReportPreviewWindow? _previewWindow;
    private bool _invitationShowing;

    /// <summary>The UI thread (the e2e harness's feed runs on it).</summary>
    internal System.Windows.Threading.Dispatcher Dispatcher => _ui;

    /// <summary>A synthetic engine snapshot for the e2e harness, published like a live one.</summary>
    internal void PublishSynthetic(Core.SpeedSnapshot snapshot) => _hub.PublishLive(snapshot);

    private void CreateReporting()
    {
        ReportLog.Sink = line => AppLog.Info(line);
        _report = new ReportController(_ui, _settings, _hub, _traffic, _cost.Book, AppPaths.Version)
        {
            QuotaEntries = (nowMs, origin) => _quota?.ReportEntries(nowMs, origin) ?? [],
        };
        _report.Changed += (_, _) => RefreshReportSurfaces();
        _report.ConsentChanged += (_, _) => OnConsentChanged();
    }

    private void StartReporting()
    {
        if (_report is null)
        {
            return;
        }

        _report.Activate();
        RefreshReportSurfaces();
        if (_launch.ReportE2E is { } url)
        {
            _ = ReportHarness.RunAsync(this, _report, url);
        }
    }

    private ReportPaneViewModel? CreateReportPane() => _report is null
        ? null
        : new ReportPaneViewModel(_report, _settings, _loc, _ui, ConfirmForget, OpenReportPreview);

    private bool ConfirmForget(string heading, string body) =>
        ConfirmDialog.Show(_settingsWindow, _loc["app.name"], heading, body, _loc["report.forget.action"], _loc["history.cancel"]);

    private void AttachReporting(DashboardWindow dashboard)
    {
        dashboard.ReportSettingsRequested += (_, _) => OpenSettings("report");
        var card = dashboard.ReportInvitation;
        card.Accepted += (_, _) => _report?.AcceptInvitation();
        card.Declined += (_, _) => _report?.DeclineInvitation();
        card.PreviewRequested += (_, _) => OpenReportPreview();
        card.Collapsed += (_, _) =>
        {
            _invitationShowing = false;
            RefreshReportSurfaces();
        };
    }

    /// <summary>The invitation shows while it is offered, one card at a time: it waits while <c>update.done</c> shows.</summary>
    private void ShowReportInvitation()
    {
        if (_dashboard is not { } dashboard || _report is not { } report)
        {
            return;
        }

        var card = dashboard.ReportInvitation;
        card.SetAppearance(_theme.IsDark, _theme.HighContrast, _theme.ReduceMotion);
        if (report.InvitationOffered && _updateDoneText is null)
        {
            var (name, domain, username) = report.InvitationText;
            card.SetText(_loc, name, domain, username);
            if (!_invitationShowing)
            {
                _invitationShowing = true;
                card.Enter();
            }
        }
        else if (_invitationShowing && !report.InviteJustAccepted && card.IsVisible && !report.InvitationOffered
                 && report.Preferences.Invite is null)
        {
            _invitationShowing = false;
            card.HideNow();
        }
    }

    /// <summary>Everything that names or follows live reporting outside its pane.</summary>
    private void RefreshReportSurfaces()
    {
        if (_report is not { } report)
        {
            return;
        }

        var status = report.Status;
        if (_dashboard is { } dashboard)
        {
            var paused = report.IsPaused;
            var label = status.NeedsUser
                ? _loc["report.indicator.attention"]
                : paused
                    ? _loc["report.menu.paused"]
                    : _loc.Format("report.indicator", ReportPaneViewModel.Isolated(report.FieldUsername), report.DestinationHost ?? string.Empty);
            dashboard.SetReportIndicator(report.IndicatorShown, paused, status.NeedsUser, label);
            if (_invitationShowing && report.Preferences.Invite is null && !report.InvitationOffered)
            {
                _invitationShowing = false;
                dashboard.ReportInvitation.HideNow();
            }
        }

        var host = report.IsOn ? report.DestinationHost : null;
        _settingsModel?.SetReporting(host);
        _settingsModel?.Traffic?.SetReporting(report.ReportsTraffic ? report.DestinationHost : null);
    }

    /// <summary>The quick menu's item while reporting is on and registered (DESIGN 11, Menu).</summary>
    private ReportMenuState? ReportMenuState()
    {
        if (_report is not { IsOn: true } report)
        {
            return null;
        }

        return new ReportMenuState(report.ServiceName ?? report.DestinationHost ?? string.Empty, report.IsPaused,
            report.Discovery?.Leaderboard is not null, report.Status.MenuTone);
    }

    public void PauseReporting(bool indefinitely) => _report?.Pause(indefinitely ? null : TimeSpan.FromHours(1));

    public void ResumeReporting() => _report?.Resume();

    public void OpenReportLeaderboard() => ReportLinks.Open(_report?.Discovery?.Leaderboard);

    private void OpenReportPreview()
    {
        if (_report is null)
        {
            return;
        }

        if (_previewWindow is { } open)
        {
            open.Activate();
            return;
        }

        _dashboard?.Dismiss();
        _previewWindow = new ReportPreviewWindow(_report) { Language = AppFonts.XmlLanguageFor(_loc.LanguageCode) };
        if (_settingsWindow is { IsVisible: true } owner)
        {
            _previewWindow.Owner = owner;
        }
        else
        {
            _previewWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _previewWindow.Closed += (_, _) => _previewWindow = null;
        _previewWindow.Show();
    }

    /// <summary>The controller opened or closed the consent sheet.</summary>
    private void OnConsentChanged()
    {
        if (_report is not { } report)
        {
            return;
        }

        if (report.Consent is null)
        {
            _consentDialog?.CloseFromController();
            _consentDialog = null;
            return;
        }

        if (_consentDialog is not null)
        {
            _consentDialog.Activate();
            return;
        }

        _dashboard?.Dismiss();
        var model = new ReportConsentViewModel(report, _loc, Settings.Traffic.Enabled);
        var dialog = new ReportConsentDialog(report, model) { Language = AppFonts.XmlLanguageFor(_loc.LanguageCode) };
        if (_settingsWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_consentDialog, dialog))
            {
                _consentDialog = null;
            }
        };
        _consentDialog = dialog;
        // Shown modally but started from the dispatcher, so the controller's call that opened it returns first.
        _ui.BeginInvoke(() =>
        {
            if (ReferenceEquals(_consentDialog, dialog) && !dialog.IsLoaded)
            {
                dialog.ShowDialog();
            }
        });
    }

    /// <summary>The elements the developer capture renders (<c>report-consent</c>, <c>report-preview</c>, <c>report-invite</c>).</summary>
    private FrameworkElement? ReportCaptureTarget(string target) => target switch
    {
        "report-consent" => _consentDialog?.Content as FrameworkElement,
        "report-preview" => _previewWindow?.Content as FrameworkElement,
        "report-invite" => _dashboard?.ReportInvitation,
        _ => null,
    };
}
