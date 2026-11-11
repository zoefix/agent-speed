using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Quota;

/// <summary>The developer capture's fixed quotas, around <c>now</c>.</summary>
internal static class QuotaDemo
{
    /// <summary>
    /// Claude Desktop 7d + Fable 7d and Codex 7d, with Claude Code 5h + 7d unless <paramref name="two"/>. Their estimates
    /// (QUOTAS 10): Claude Code's 5h with a high-confidence cap and an extrapolated percent, its 7d with a medium one, Claude
    /// Desktop's 7d only at least, Codex's 7d extrapolated with a medium cap; Fable 7d has none.
    /// </summary>
    public static QuotaState State(long now, bool two)
    {
        const long Hour = 3_600_000;
        var snapshots = ImmutableDictionary.CreateBuilder<SourceKind, QuotaSnapshot>();
        var desktop = QuotaWindow.Of(QuotaWindowKind.SevenDay, 86, now + 2 * 24 * Hour + 5 * Hour);
        snapshots[SourceKind.ClaudeDesktop] = new QuotaSnapshot(SourceKind.ClaudeDesktop, now - 6 * 60_000,
            [desktop, QuotaWindow.Of(QuotaWindowKind.ModelWeek, 27, now + 2 * 24 * Hour + 5 * Hour, "Fable")]);
        var codex = QuotaWindow.Of(QuotaWindowKind.SevenDay, 23, now + 5 * 24 * Hour + 9 * Hour);
        snapshots[SourceKind.Codex] = new QuotaSnapshot(SourceKind.Codex, now - 2 * 60_000, [codex]);
        var estimates = ImmutableDictionary.CreateBuilder<QuotaEstimateKey, QuotaWindowEstimate>();
        estimates[Key(null, SourceKind.ClaudeDesktop, desktop)] = Estimate(now, desktop, 86, 41_230, null, 47_942, null, EstimateConfidence.Low, null, 2);
        estimates[Key(null, SourceKind.Codex, codex)] = Estimate(now, codex, 25.6, 7_712, 31_800, 29_700, 34_100, EstimateConfidence.Medium, 0.92, 6, "pro");
        if (!two)
        {
            var five = QuotaWindow.Of(QuotaWindowKind.FiveHour, 41, now + 3 * Hour + 12 * 60_000);
            var week = QuotaWindow.Of(QuotaWindowKind.SevenDay, 96, now + 19 * Hour + 40 * 60_000);
            snapshots[SourceKind.ClaudeCode] = new QuotaSnapshot(SourceKind.ClaudeCode, now - 60_000, [five, week]);
            estimates[Key(null, SourceKind.ClaudeCode, five)] = Estimate(now, five, 44.2, 3_318, 7_540, 7_390, 7_690, EstimateConfidence.High, 0.97, 14,
                ClaudePlan, ClaudeTier);
            estimates[Key(null, SourceKind.ClaudeCode, week)] = Estimate(now, week, 96, 161_240, 170_200, 156_000, 185_000, EstimateConfidence.Medium, 0.99, 4,
                ClaudePlan, ClaudeTier);
        }

        return new QuotaState
        {
            Snapshots = snapshots.ToImmutable(),
            Active = [.. snapshots.Keys],
            CliStatus = two ? ClaudeCliBridgeStatus.Off : ClaudeCliBridgeStatus.Ok,
            CliFileTime = two ? null : DateTimeOffset.FromUnixTimeMilliseconds(now - 60_000),
            Estimates = estimates.ToImmutable(),
        };
    }

    private const string ClaudePlan = "claude_max";
    private const string ClaudeTier = "default_claude_max_20x";

    private static QuotaEstimateKey Key(string? host, SourceKind source, QuotaWindow window) => new(host, source, EstimateNames.WindowId(window));

    /// <summary>
    /// A window's estimate at <paramref name="now"/>: amounts in cents, the cap with its published range or only a lower bound,
    /// the whole account's use from the cap, extrapolated when <paramref name="live"/> is above the vendor's percent.
    /// </summary>
    private static QuotaWindowEstimate Estimate(long now, QuotaWindow window, double live, long usedCents, long? capCents, long? lowCents, long? highCents,
        EstimateConfidence confidence, double? coverage, int windows, string? plan = null, string? tier = null) =>
        new(now, live, live - window.UsedPercent >= 0.5, window.ResetsAtMs, false, usedCents * 10_000, 0, capCents * 10_000, lowCents * 10_000,
            highCents * 10_000, confidence, coverage, capCents is { } cap ? (long)(cap * 10_000 * live / 100) : null, windows, plan, tier);

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
    /// <paramref name="state"/> with the demo hosts' quotas (<see cref="Hosts"/>) and their estimates: the first host's Codex 7d
    /// with a cap and its Claude Code 7d extrapolated, at least.
    /// </summary>
    public static QuotaState WithHosts(QuotaState state, long now, IReadOnlyList<string> hostIds)
    {
        var hosts = Hosts(now, hostIds);
        var estimates = state.Estimates.ToBuilder();
        if (hostIds.Count > 0 && hosts.TryGetValue(hostIds[0], out var first))
        {
            var id = hostIds[0];
            var codex = first.Reports[SourceKind.Codex].Snapshot.Windows[0];
            var week = first.Reports[SourceKind.ClaudeCode].Snapshot.Windows[1];
            estimates[Key(id, SourceKind.Codex, codex)] = Estimate(now, codex, 40, 12_734, 31_800, 29_700, 34_100, EstimateConfidence.Medium, 0.91, 5, "pro");
            estimates[Key(id, SourceKind.ClaudeCode, week)] = Estimate(now, week, 88.4, 52_610, null, 59_800, null, EstimateConfidence.Low, null, 1, ClaudePlan,
                ClaudeTier);
        }

        return state with { Hosts = hosts, Estimates = estimates.ToImmutable() };
    }

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
