using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Settings;

/// <summary>
/// The quota switches of Settings → Data Sources (DESIGN 5, QUOTAS): Codex and Claude Desktop on, the Claude CLI
/// status line bridge off, and whether the dashboard's Claude Code invitation was dismissed. Stored as the top-level
/// values <c>quotaCodex</c>, <c>quotaClaudeDesktop</c>, <c>quotaClaudeCli</c> and <c>quotaCliInvite</c>.
/// </summary>
public sealed record QuotaSettings
{
    public static QuotaSettings Default { get; } = new();

    public bool Codex { get; init; } = true;

    public bool ClaudeDesktop { get; init; } = true;

    public bool ClaudeCli { get; init; }

    /// <summary><c>quotaCliInvite == "dismissed"</c>: the invitation never shows again.</summary>
    public bool CliInviteDismissed { get; init; }

    /// <summary>The source's quota shows (sources without a quota reader are off).</summary>
    public bool IsOn(SourceKind source) => source switch
    {
        SourceKind.Codex => Codex,
        SourceKind.ClaudeDesktop => ClaudeDesktop,
        SourceKind.ClaudeCode => ClaudeCli,
        _ => false,
    };
}
