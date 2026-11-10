using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Quota;

/// <summary>The developer capture's fixed quotas, around <c>now</c>.</summary>
internal static class QuotaDemo
{
    public static QuotaState State(long now, bool two)
    {
        const long Hour = 3_600_000;
        var snapshots = ImmutableDictionary.CreateBuilder<SourceKind, QuotaSnapshot>();
        snapshots[SourceKind.ClaudeDesktop] = new QuotaSnapshot(SourceKind.ClaudeDesktop, now - 6 * 60_000,
        [
            QuotaWindow.Of(QuotaWindowKind.SevenDay, 86, now + 2 * 24 * Hour + 5 * Hour),
            QuotaWindow.Of(QuotaWindowKind.ModelWeek, 27, now + 2 * 24 * Hour + 5 * Hour, "Fable"),
        ]);
        snapshots[SourceKind.Codex] = new QuotaSnapshot(SourceKind.Codex, now - 2 * 60_000,
            [QuotaWindow.Of(QuotaWindowKind.SevenDay, 23, now + 5 * 24 * Hour + 9 * Hour)]);
        if (!two)
        {
            snapshots[SourceKind.ClaudeCode] = new QuotaSnapshot(SourceKind.ClaudeCode, now - 60_000,
            [
                QuotaWindow.Of(QuotaWindowKind.FiveHour, 41, now + 3 * Hour + 12 * 60_000),
                QuotaWindow.Of(QuotaWindowKind.SevenDay, 96, now + 19 * Hour + 40 * 60_000),
            ]);
        }

        return new QuotaState
        {
            Snapshots = snapshots.ToImmutable(),
            Active = [.. snapshots.Keys],
            CliStatus = two ? ClaudeCliBridgeStatus.Off : ClaudeCliBridgeStatus.Ok,
            CliFileTime = two ? null : DateTimeOffset.FromUnixTimeMilliseconds(now - 60_000),
        };
    }

    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Day = 24 * Hour;

    /// <summary>
    /// Phase 2 as the macOS review set shows it: Antigravity's named groups, ZCode's 5h and MCP month, Kimi's three windows,
    /// Grok's month, Devin's day and week, Qoder's credits on both Qoder rows and Gemini's daily families, all running and
    /// signed in, every route updated a few minutes ago.
    /// </summary>
    public static QuotaState Phase2(long now)
    {
        var state = State(now, two: false);
        var snapshots = state.Snapshots.ToBuilder();
        void Add(SourceKind source, long ageMs, params QuotaWindow[] windows) => snapshots[source] = new QuotaSnapshot(source, now - ageMs, windows);
        Add(SourceKind.Antigravity, 2 * Minute,
            QuotaWindow.Of(QuotaWindowKind.FiveHour, 25, now + 2 * Hour + 12 * Minute, "Gemini"),
            QuotaWindow.Of(QuotaWindowKind.SevenDay, 60, now + 3 * Day + 5 * Hour, "Gemini"),
            QuotaWindow.Of(QuotaWindowKind.SevenDay, 83, now + 5 * Day + 2 * Hour, "Claude/GPT"));
        Add(SourceKind.ZCode, 3 * Minute,
            QuotaWindow.Of(QuotaWindowKind.FiveHour, 30, now + 2 * Hour),
            QuotaWindow.Of(QuotaWindowKind.Month, 40, now + 6 * Day, "MCP"));
        Add(SourceKind.Kimi, 4 * Minute,
            QuotaWindow.Of(QuotaWindowKind.FiveHour, 23, now + 3 * Hour + 40 * Minute),
            QuotaWindow.Of(QuotaWindowKind.SevenDay, 41, now + 4 * Day + 5 * Hour),
            QuotaWindow.Of(QuotaWindowKind.Month, 13, now + 6 * Day));
        Add(SourceKind.GrokBuild, 3 * Hour, QuotaWindow.Of(QuotaWindowKind.Month, 64, now + 5 * Day));
        Add(SourceKind.Devin, 2 * Hour,
            QuotaWindow.Of(QuotaWindowKind.Other, 25, now + 9 * Hour, minutes: 1440),
            QuotaWindow.Of(QuotaWindowKind.SevenDay, 60, now + 5 * Day + 4 * Hour));
        Add(SourceKind.Qoder, 5 * Minute, QuotaWindow.Of(QuotaWindowKind.Credits, 37, now + 6 * Day));
        Add(SourceKind.QoderCli, 5 * Minute, QuotaWindow.Of(QuotaWindowKind.Credits, 37, now + 6 * Day));
        Add(SourceKind.Gemini, 6 * Minute,
            QuotaWindow.Of(QuotaWindowKind.Other, 40, now + 9 * Hour, "Pro", 1440),
            QuotaWindow.Of(QuotaWindowKind.Other, 3, now + 9 * Hour, "Flash", 1440));
        var statuses = ImmutableDictionary.CreateBuilder<QuotaRoute, QuotaRouteStatus>();
        foreach (var route in QuotaRoutes.All)
        {
            statuses[route] = QuotaRouteStatus.Ok(now - 4 * Minute);
        }

        return state with
        {
            Snapshots = snapshots.ToImmutable(),
            Active = [.. snapshots.Keys],
            RouteStatuses = statuses.ToImmutable(),
            ZcodeKeySaved = true,
        };
    }

    /// <summary>The same with one status line per route kind, for the status-line review (DESIGN 5).</summary>
    public static QuotaState Statuses(long now) => Phase2(now) with
    {
        RouteStatuses = ImmutableDictionary.CreateRange(new Dictionary<QuotaRoute, QuotaRouteStatus>
        {
            [QuotaRoute.GrokBuild] = QuotaRouteStatus.Ok(now - 4 * Minute),
            [QuotaRoute.GrokBuildLive] = QuotaRouteStatus.Of(QuotaStatusKind.Failed),
            [QuotaRoute.Devin] = QuotaRouteStatus.Ok(now - 2 * Hour),
            [QuotaRoute.Antigravity] = QuotaRouteStatus.Of(QuotaStatusKind.NotRunning),
            [QuotaRoute.Kimi] = QuotaRouteStatus.Of(QuotaStatusKind.Expired),
            [QuotaRoute.Gemini] = QuotaRouteStatus.Denied(QuotaHosts.Gemini),
            [QuotaRoute.Droid] = QuotaRouteStatus.Of(QuotaStatusKind.NoCredential),
            [QuotaRoute.Zcode] = QuotaRouteStatus.Ok(now - 3 * Minute),
            [QuotaRoute.Qoder] = QuotaRouteStatus.Of(QuotaStatusKind.BrowserLocked),
            [QuotaRoute.WorkBuddy] = QuotaRouteStatus.Of(QuotaStatusKind.NoBrowser),
        }),
    };

    /// <summary>
    /// Host quotas for the given hosts in order: the first runs Codex, Claude Code (its bridge connected) and Kimi Code, the
    /// second Gemini CLI.
    /// </summary>
    public static ImmutableDictionary<string, HostQuotaState> Hosts(long now, IReadOnlyList<string> hostIds)
    {
        var hosts = ImmutableDictionary.CreateBuilder<string, HostQuotaState>();
        RemoteQuotaReport Report(string host, SourceKind source, params QuotaWindow[] windows) =>
            new(host, new QuotaSnapshot(source, now - 3 * Minute, windows), true, true, QuotaRouteStatus.Ok(now - 3 * Minute));
        if (hostIds.Count > 0)
        {
            var id = hostIds[0];
            hosts[id] = new HostQuotaState
            {
                Reports = ImmutableDictionary.CreateRange(new Dictionary<SourceKind, RemoteQuotaReport>
                {
                    [SourceKind.Codex] = Report(id, SourceKind.Codex, QuotaWindow.Of(QuotaWindowKind.SevenDay, 40, now + 4 * Day + 8 * Hour)),
                    [SourceKind.ClaudeCode] = Report(id, SourceKind.ClaudeCode,
                        QuotaWindow.Of(QuotaWindowKind.FiveHour, 6, now + 4 * Hour + 10 * Minute),
                        QuotaWindow.Of(QuotaWindowKind.SevenDay, 86, now + 3 * Day + 2 * Hour)),
                    [SourceKind.Kimi] = Report(id, SourceKind.Kimi, QuotaWindow.Of(QuotaWindowKind.FiveHour, 52, now + Hour + 10 * Minute)),
                }),
                Bridge = new RemoteQuotaBridge(id, ClaudeCliBridgeStatus.Ok, "~/.claude/settings.json"),
            };
        }

        if (hostIds.Count > 1)
        {
            var id = hostIds[1];
            hosts[id] = new HostQuotaState
            {
                Reports = ImmutableDictionary.CreateRange(new Dictionary<SourceKind, RemoteQuotaReport>
                {
                    [SourceKind.Gemini] = Report(id, SourceKind.Gemini,
                        QuotaWindow.Of(QuotaWindowKind.Other, 40, now + 9 * Hour, "Pro", 1440),
                        QuotaWindow.Of(QuotaWindowKind.Other, 3, now + 9 * Hour, "Flash", 1440)),
                }),
            };
        }

        return hosts.ToImmutable();
    }
}
