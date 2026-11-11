using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Quota;

namespace AgentSpeed.App;

/// <summary>
/// Settings → Data Sources' phase 2 captures (only with <c>--capture-dir</c>, where the quota service is sandboxed: no
/// browser is read, the ZCode key stays in memory and no vendor is asked). Every state comes from <see cref="QuotaDemo"/>.
/// </summary>
internal sealed partial class AppController
{
    private static readonly SourceKind[] SettingsCaptureSources =
    [
        SourceKind.GrokBuild, SourceKind.Devin, SourceKind.Antigravity, SourceKind.Kimi, SourceKind.Gemini, SourceKind.Droid, SourceKind.ZCode,
        SourceKind.Qoder, SourceKind.QoderCli, SourceKind.WorkBuddy,
    ];

    /// <summary>The sources <c>quota2-settings-source-off</c> disables: a dimmed group of each kind of row, and the bridge that stays switchable.</summary>
    private static readonly SourceKind[] SettingsCaptureOffSources = [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.GrokBuild, SourceKind.ZCode, SourceKind.WorkBuddy];

    /// <summary>
    /// <c>quota2-settings-on</c> (every phase 2 switch on, all updated, key saved), <c>-statuses</c> (one status per route),
    /// <c>-statuses-win</c> (the Windows browser statuses and the rest), <c>-empty</c> (no key yet), <c>-network-off</c> /
    /// <c>-network-on</c>, <c>-source-off</c> / <c>-source-on</c>, <c>-live-off</c> (Grok's log switch off),
    /// <c>-region-cn</c> / <c>-region-global</c>, <c>-default</c> (the stored defaults, real readers), <c>-key-enter</c> /
    /// <c>-key-remove</c> (the key field driven as a user would, with a made-up key in the sandbox's memory store), and
    /// <c>quota2-consent-qoder</c> / <c>-workbuddy</c> / <c>-close</c> (the browser session's dialog, then Cancel).
    /// </summary>
    private partial bool RunQuotaSettingsCapture(string op)
    {
        if (_quota is not { } quota || !op.StartsWith("quota2-", StringComparison.Ordinal))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        switch (op)
        {
            case "quota2-settings-on":
                ShowSettingsCapture(QuotaDemo.Phase2(now));
                break;
            case "quota2-settings-statuses":
                ShowSettingsCapture(QuotaDemo.Statuses(now));
                break;
            case "quota2-settings-statuses-win":
                ShowSettingsCapture(QuotaDemo.Phase2(now) with { RouteStatuses = SettingsCaptureStatuses(now) });
                break;
            case "quota2-settings-empty":
                ShowSettingsCapture(QuotaDemo.Phase2(now) with { ZcodeKeySaved = false });
                break;
            case "quota2-settings-network-off" or "quota2-settings-network-on":
                _settings.Update(s => s with { NetworkEnabled = op.EndsWith("-on", StringComparison.Ordinal) });
                break;
            case "quota2-settings-source-off" or "quota2-settings-source-on":
                var enable = op.EndsWith("-on", StringComparison.Ordinal);
                _settings.Update(s => SettingsCaptureOffSources.Aggregate(s, (next, source) => next.WithSourceEnabled(source, enable)));
                break;
            case "quota2-settings-live-off":
                _settings.Update(s => s with { Quota = s.Quota with { GrokBuild = false } });
                break;
            case "quota2-settings-region-cn" or "quota2-settings-region-global":
                var region = op.EndsWith("-cn", StringComparison.Ordinal) ? ZaiRegion.BigmodelCn : ZaiRegion.Global;
                _settings.Update(s => s with { Quota = s.Quota with { ZcodeRegion = region } });
                break;
            case "quota2-settings-default":
                _settings.Update(s => s with { Quota = SettingsCaptureDefaults(s.Quota), NetworkEnabled = true });
                quota.SetDemo(null);
                break;
            case "quota2-settings-key-enter":
                if (SettingsCaptureElement<PasswordBox>("ZcodeKeyBox") is { IsVisible: true } box && PresentationSource.FromVisual(box) is { } source)
                {
                    box.Focus();
                    box.Password = "capture-made-up-key";
                    box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                    AppLog.Info($"Quota capture: the key field is {(box.Password.Length == 0 ? "empty" : "NOT empty")} after Enter.");
                }

                break;
            case "quota2-settings-key-remove":
                if (SettingsCaptureElement<Button>("ZcodeRemove") is { IsVisible: true } remove
                    && new ButtonAutomationPeer(remove).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                {
                    remove.Focus();
                    invoke.Invoke();
                }

                break;
            case "quota2-consent-qoder" or "quota2-consent-workbuddy":
                var route = op.EndsWith("-qoder", StringComparison.Ordinal) ? QuotaRoute.Qoder : QuotaRoute.WorkBuddy;
                _settings.Update(s => s with { Quota = s.Quota.With(route, false) });
                _ui.BeginInvoke(() => EnableBrowserQuota(route));
                break;
            case "quota2-consent-close":
                foreach (var dialog in System.Windows.Application.Current.Windows.OfType<ConfirmDialog>().ToList())
                {
                    dialog.Close();
                }

                break;
            default:
                return false;
        }

        return true;

        void ShowSettingsCapture(Presentation.Quota.QuotaState demo)
        {
            _settings.Update(s => SettingsCaptureSources.Aggregate(s, (next, source) => next.WithSourceEnabled(source, true)) with
            {
                Quota = QuotaRoutes.All.Aggregate(s.Quota, (next, r) => next.With(r, true)),
                NetworkEnabled = true,
            });
            quota.SetDemo(demo);
        }
    }

    /// <summary>A named control of the open Settings window's page, for the captures that drive it.</summary>
    private T? SettingsCaptureElement<T>(string name)
        where T : FrameworkElement
    {
        static T? Find(DependencyObject root, string name)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T element && element.Name == name)
                {
                    return element;
                }

                if (Find(child, name) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        return _settingsWindow is { } window ? Find(window, name) : null;
    }

    /// <summary>The Windows browser statuses on Qoder and WorkBuddy, with the kinds <see cref="QuotaDemo.Statuses"/> leaves out.</summary>
    private static ImmutableDictionary<QuotaRoute, QuotaRouteStatus> SettingsCaptureStatuses(long now) =>
        ImmutableDictionary.CreateRange(new Dictionary<QuotaRoute, QuotaRouteStatus>
        {
            [QuotaRoute.GrokBuild] = QuotaRouteStatus.Ok(now - 26 * 3_600_000L),
            [QuotaRoute.GrokBuildLive] = QuotaRouteStatus.Of(QuotaStatusKind.NoCredential),
            [QuotaRoute.Devin] = QuotaRouteStatus.Ok(now - 5 * 60_000),
            [QuotaRoute.Antigravity] = QuotaRouteStatus.Of(QuotaStatusKind.Failed),
            [QuotaRoute.Kimi] = QuotaRouteStatus.Of(QuotaStatusKind.NotRunning),
            [QuotaRoute.Gemini] = QuotaRouteStatus.Of(QuotaStatusKind.Expired),
            [QuotaRoute.Droid] = QuotaRouteStatus.Denied(QuotaHosts.Factory),
            [QuotaRoute.Zcode] = QuotaRouteStatus.Denied(QuotaHosts.Zai),
            [QuotaRoute.Qoder] = QuotaRouteStatus.Of(QuotaStatusKind.BrowserProtected),
            [QuotaRoute.WorkBuddy] = QuotaRouteStatus.Of(QuotaStatusKind.BrowserLocked),
        });

    private static QuotaSettings SettingsCaptureDefaults(QuotaSettings quota) => quota with
    {
        GrokBuild = QuotaSettings.Default.GrokBuild,
        GrokBuildLive = QuotaSettings.Default.GrokBuildLive,
        Devin = QuotaSettings.Default.Devin,
        Antigravity = QuotaSettings.Default.Antigravity,
        Kimi = QuotaSettings.Default.Kimi,
        Gemini = QuotaSettings.Default.Gemini,
        Droid = QuotaSettings.Default.Droid,
        Zcode = QuotaSettings.Default.Zcode,
        ZcodeRegion = QuotaSettings.Default.ZcodeRegion,
        Qoder = QuotaSettings.Default.Qoder,
        WorkBuddy = QuotaSettings.Default.WorkBuddy,
    };
}
