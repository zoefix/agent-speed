using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Quota;

namespace AgentSpeed.App;

/// <summary>
/// The developer capture's phase 2 and host quota surfaces (only with <c>--capture-dir</c>; the capture build never asks a
/// vendor, reads a browser or touches Credential Manager): <c>quota2-demo</c> (every phase 2 source with its switch on),
/// <c>quota2-demo-rest</c> (the rows under the first ones, with Factory Droid and WorkBuddy),
/// <c>quota2-hosts</c> (two demo hosts, shown as connected: Codex, Claude Code with its bridge connected and Kimi Code on
/// <c>devbox</c>, Gemini CLI on <c>gpu-01.lab</c>), <c>quota2-card-hosts</c> (the 額度 card with this computer's Kimi Code
/// and <c>devbox</c>'s sources: the same source on two hosts), <c>quota2-host-editor</c> / <c>quota2-host-editor-replaced</c> / <c>quota2-host-editor-off</c>
/// (the first host's editor with its bridge connected, replaced or off), <c>quota2-host-confirm</c> (the bridge's question
/// over the editor), <c>quota2-remove-confirm</c> (the removal confirmation of the bridged host) and <c>quota2-off</c>.
/// </summary>
internal sealed partial class AppController
{
    /// <summary>The demo hosts' destinations (named after them): never reached, and disabled so the sandbox starts no connection.</summary>
    private static readonly string[] CaptureHosts = ["devbox", "gpu-01.lab"];

    private partial bool RunQuotaSurfaceCapture(string op)
    {
        if (!op.StartsWith("quota2-", StringComparison.Ordinal) || _quota is not { } quota)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        switch (op)
        {
            case "quota2-demo":
                _settings.Update(s => s with { Quota = AllQuotaSwitches(s.Quota) });
                quota.SetDemo(QuotaDemo.Phase2(now));
                ShowCaptureConnected(null);
                return true;
            case "quota2-demo-rest":
                _settings.Update(s => s with { Quota = AllQuotaSwitches(s.Quota) });
                quota.SetDemo(RestOfPhase2(now));
                ShowCaptureConnected(null);
                return true;
            case "quota2-hosts":
            {
                var ids = EnsureCaptureHosts(bridge: ClaudeCliBridgeStatus.Ok);
                _settings.Update(s => s with { Quota = AllQuotaSwitches(s.Quota) });
                quota.SetDemo(QuotaDemo.State(now, two: true) with { Hosts = QuotaDemo.Hosts(now, ids) });
                ShowCaptureConnected(ids);
                return true;
            }

            case "quota2-card-hosts":
            {
                var ids = EnsureCaptureHosts(bridge: ClaudeCliBridgeStatus.Ok);
                if (ids.Count == 0)
                {
                    return true;
                }

                _settings.Update(s => s with { Quota = AllQuotaSwitches(s.Quota) });
                var phase2 = QuotaDemo.Phase2(now);
                quota.SetDemo(phase2 with
                {
                    Snapshots = phase2.Snapshots.Where(p => p.Key == SourceKind.Kimi).ToImmutableDictionary(),
                    Active = [SourceKind.Kimi],
                    Hosts = QuotaDemo.Hosts(now, ids),
                });
                ShowCaptureConnected([ids[0]]);
                return true;
            }

            case "quota2-host-editor" or "quota2-host-editor-replaced" or "quota2-host-editor-off":
            {
                var bridge = op switch
                {
                    "quota2-host-editor-replaced" => ClaudeCliBridgeStatus.Replaced,
                    "quota2-host-editor-off" => ClaudeCliBridgeStatus.Off,
                    _ => ClaudeCliBridgeStatus.Ok,
                };
                var ids = EnsureCaptureHosts(bridge);
                if (ids.Count == 0)
                {
                    return true;
                }

                var hosts = QuotaDemo.Hosts(now, ids);
                if (hosts.TryGetValue(ids[0], out var first))
                {
                    hosts = hosts.SetItem(ids[0], first with { Bridge = first.Bridge! with { State = bridge } });
                }

                quota.SetDemo(QuotaDemo.State(now, two: true) with { Hosts = hosts });
                ShowCaptureConnected(ids);
                if (_remoteHosts?.Document.Host(ids[0]) is { } edited)
                {
                    _ui.BeginInvoke(() => EditRemoteHost(edited));
                }

                return true;
            }

            case "quota2-host-confirm":
                if (_remoteHosts?.Document.Hosts.FirstOrDefault() is { } host)
                {
                    var name = _remoteHosts.Document.DisplayName(host.Id) ?? host.AutomaticName;
                    _ui.BeginInvoke(() => ConfirmHostBridge(name, Presentation.Remote.RemoteHostQuotaText.SettingsPath(Quota.HostOf(host.Id))));
                }

                return true;
            case "quota2-remove-confirm":
            {
                var ids = EnsureCaptureHosts(bridge: ClaudeCliBridgeStatus.Ok);
                if (ids.Count > 0 && _remoteHosts?.Document.Host(ids[0]) is { } removed)
                {
                    // The answer is ignored: the capture only looks at the confirmation and never removes the host.
                    var name = _remoteHosts.Document.DisplayName(removed.Id) ?? removed.AutomaticName;
                    _ui.BeginInvoke(() => ConfirmRemoveRemoteHost(name, removed));
                }

                return true;
            }

            case "quota2-off":
                quota.SetDemo(null);
                ShowCaptureConnected(null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The phase 2 rows a dashboard capture cannot reach under the first ones: Kimi Code, Grok Build, Devin, both Qoder rows, and
    /// Factory Droid (standard and core windows) and WorkBuddy (a month without a reset), which the shared demo leaves out.
    /// </summary>
    private static QuotaState RestOfPhase2(long now)
    {
        const long Hour = 3_600_000;
        var phase2 = QuotaDemo.Phase2(now);
        var snapshots = phase2.Snapshots
            .RemoveRange([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop, SourceKind.Gemini, SourceKind.Antigravity, SourceKind.ZCode])
            .SetItem(SourceKind.Droid, new QuotaSnapshot(SourceKind.Droid, now - 5 * 60_000,
            [
                QuotaWindow.Of(QuotaWindowKind.FiveHour, 12, now + 4 * Hour),
                QuotaWindow.Of(QuotaWindowKind.SevenDay, 35, now + 80 * Hour),
                QuotaWindow.Of(QuotaWindowKind.Month, 8, now + 400 * Hour),
                QuotaWindow.Of(QuotaWindowKind.FiveHour, 3, now + 4 * Hour, "Core"),
            ]))
            .SetItem(SourceKind.WorkBuddy, new QuotaSnapshot(SourceKind.WorkBuddy, now - 5 * 60_000, [QuotaWindow.Of(QuotaWindowKind.Month, 45, null)]));
        return phase2 with { Snapshots = snapshots, Active = [.. snapshots.Keys] };
    }

    /// <summary>Every quota switch on, as a user who turned each on (and agreed to the browser sessions) has them.</summary>
    private static QuotaSettings AllQuotaSwitches(QuotaSettings quota)
    {
        var all = quota with { Codex = true, ClaudeDesktop = true, ClaudeCli = true };
        foreach (var route in QuotaRoutes.All.Where(r => r != QuotaRoute.GrokBuildLive))
        {
            all = all.With(route, true);
        }

        return all;
    }

    /// <summary>The two demo hosts in the settings (added once, disabled), the first with its bridge switch as asked; their ids.</summary>
    private IReadOnlyList<string> EnsureCaptureHosts(ClaudeCliBridgeStatus bridge)
    {
        if (_remoteHosts is not { } hosts)
        {
            return [];
        }

        foreach (var destination in CaptureHosts)
        {
            if (!hosts.Document.Hosts.Any(h => h.Destination == destination))
            {
                hosts.Add(new RemoteHost(destination) { Enabled = false });
            }
        }

        var ids = CaptureHosts.Select(d => hosts.Document.Hosts.FirstOrDefault(h => h.Destination == d)?.Id).OfType<string>().ToList();
        if (ids.Count > 0 && hosts.Document.Host(ids[0]) is { } first && first.QuotaClaudeCli != (bridge != ClaudeCliBridgeStatus.Off))
        {
            hosts.Update(first with { QuotaClaudeCli = bridge != ClaudeCliBridgeStatus.Off });
        }

        return ids;
    }

    /// <summary>The Hosts section and the 額度 cards treat these hosts as connected (null: as they are).</summary>
    private void ShowCaptureConnected(IReadOnlyList<string>? ids)
    {
        var set = ids is null ? null : new HashSet<string>(ids, StringComparer.Ordinal);
        _dashboardModel.CaptureConnected = set;
        if (_orbs is { } orbs)
        {
            orbs.CaptureConnected = set;
        }

        _dashboardDirty = true;
        FlushDashboard();
    }
}
