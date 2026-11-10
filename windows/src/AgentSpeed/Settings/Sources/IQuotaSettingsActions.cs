using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;

namespace AgentSpeed.Settings.Sources;

/// <summary>The phase 1 quota switches of Settings → Data Sources (DESIGN 5, 額度); phase 2 switches are <see cref="QuotaRoute"/>s.</summary>
internal enum QuotaSwitch
{
    Codex,
    ClaudeDesktop,
    ClaudeCli,
}

/// <summary>What the quota switches do; the app implements it (the bridge and the browser routes ask first).</summary>
internal interface IQuotaSettingsActions
{
    void Set(QuotaSwitch quota, bool on);

    /// <summary>Asks, then turns the Claude CLI bridge on; true when it is on.</summary>
    bool EnableClaudeCli();

    void DisableClaudeCli();

    void ReconnectClaudeCli();

    /// <summary>A phase 2 switch (QUOTAS 9); turning a browser route on goes through <see cref="EnableBrowser"/>.</summary>
    void SetRoute(QuotaRoute route, bool on);

    /// <summary>Asks before the browser's session is read (QUOTAS 9.10), then turns the route on; true when it is on.</summary>
    bool EnableBrowser(QuotaRoute route);

    /// <summary>Saves a pasted ZCode key to Credential Manager (QUOTAS 9.8); false when it is not a key or could not be saved.</summary>
    bool SaveZcodeKey(string text);

    void RemoveZcodeKey();

    void SetZcodeRegion(ZaiRegion region);
}
