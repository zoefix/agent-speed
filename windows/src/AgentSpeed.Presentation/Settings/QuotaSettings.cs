using AgentSpeed.Core;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;

namespace AgentSpeed.Presentation.Settings;

/// <summary>
/// The quota switches of Settings → Data Sources (DESIGN 5, QUOTAS): Codex and Claude Desktop on, the Claude CLI status
/// line bridge off, whether the dashboard's Claude Code invitation was dismissed, and the phase 2 switches of QUOTAS 9
/// (the local and agent-sign-in routes on, Grok's live request, ZCode and the browser sessions off). Stored as the
/// top-level values <c>quotaCodex</c> … <c>quotaWorkBuddy</c>; the ZCode key only in the secret store.
/// </summary>
public sealed record QuotaSettings
{
    public static QuotaSettings Default { get; } = new();

    public bool Codex { get; init; } = true;

    public bool ClaudeDesktop { get; init; } = true;

    public bool ClaudeCli { get; init; }

    /// <summary><c>quotaCliInvite == "dismissed"</c>: the invitation never shows again.</summary>
    public bool CliInviteDismissed { get; init; }

    public bool GrokBuild { get; init; } = true;

    /// <summary>Grok's live request; only with <see cref="GrokBuild"/>.</summary>
    public bool GrokBuildLive { get; init; }

    public bool Devin { get; init; } = true;

    public bool Antigravity { get; init; } = true;

    public bool Kimi { get; init; } = true;

    public bool Gemini { get; init; } = true;

    public bool Droid { get; init; } = true;

    public bool Zcode { get; init; }

    public ZaiRegion ZcodeRegion { get; init; } = ZaiRegion.Global;

    /// <summary>One switch for both Qoder sources (one account).</summary>
    public bool Qoder { get; init; }

    public bool WorkBuddy { get; init; }

    /// <summary>The source's quota switch alone, whatever its source toggle; sources without a quota reader are off.</summary>
    public bool IsOn(SourceKind source) => source switch
    {
        SourceKind.Codex => Codex,
        SourceKind.ClaudeDesktop => ClaudeDesktop,
        SourceKind.ClaudeCode => ClaudeCli,
        SourceKind.GrokBuild => GrokBuild,
        SourceKind.Devin => Devin,
        SourceKind.Antigravity => Antigravity,
        SourceKind.Kimi => Kimi,
        SourceKind.Gemini => Gemini,
        SourceKind.Droid => Droid,
        SourceKind.ZCode => Zcode,
        SourceKind.Qoder or SourceKind.QoderCli => Qoder,
        SourceKind.WorkBuddy => WorkBuddy,
        _ => false,
    };

    /// <summary>A phase 2 route's own switch (Grok's live request also needs the log's).</summary>
    public bool IsOn(QuotaRoute route) => route switch
    {
        QuotaRoute.GrokBuildLive => GrokBuild && GrokBuildLive,
        _ => IsOn(route.Source()),
    };

    /// <summary>The same settings with one route's switch set.</summary>
    public QuotaSettings With(QuotaRoute route, bool on) => route switch
    {
        QuotaRoute.GrokBuild => this with { GrokBuild = on },
        QuotaRoute.GrokBuildLive => this with { GrokBuildLive = on },
        QuotaRoute.Devin => this with { Devin = on },
        QuotaRoute.Antigravity => this with { Antigravity = on },
        QuotaRoute.Kimi => this with { Kimi = on },
        QuotaRoute.Gemini => this with { Gemini = on },
        QuotaRoute.Droid => this with { Droid = on },
        QuotaRoute.Zcode => this with { Zcode = on },
        QuotaRoute.Qoder => this with { Qoder = on },
        _ => this with { WorkBuddy = on },
    };

    /// <summary>Every source with a quota, in source order (QUOTAS 9).</summary>
    public static IReadOnlyList<SourceKind> Sources { get; } =
    [
        .. Enum.GetValues<SourceKind>().Where(s => s is SourceKind.Codex or SourceKind.ClaudeCode or SourceKind.ClaudeDesktop or SourceKind.GrokBuild
            or SourceKind.Devin or SourceKind.Antigravity or SourceKind.Kimi or SourceKind.Gemini or SourceKind.Droid or SourceKind.ZCode
            or SourceKind.Qoder or SourceKind.QoderCli or SourceKind.WorkBuddy),
    ];
}
