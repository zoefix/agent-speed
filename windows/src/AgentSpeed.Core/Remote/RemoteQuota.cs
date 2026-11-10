using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// What the app asks a host's relay to read (REMOTE 11.1): the sources whose quota switch is on, the master switch, Grok's
/// live request and the host's Claude Code bridge.
/// </summary>
public sealed record RemoteQuotaConfig
{
    private readonly SourceSet _sources;

    /// <param name="sources">Sources a host cannot read are left out.</param>
    public RemoteQuotaConfig(SourceSet sources, bool network, bool grokLive, bool claudeBridge)
    {
        Sources = sources;
        Network = network;
        GrokLive = grokLive;
        ClaudeBridge = claudeBridge;
    }

    /// <summary>The sources a host can read (REMOTE 11); every other key is left out.</summary>
    public static SourceSet HostSources { get; } =
        [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.GrokBuild, SourceKind.Kimi, SourceKind.Gemini, SourceKind.Droid, SourceKind.Antigravity];

    public static RemoteQuotaConfig Off { get; } = new(SourceSet.Empty, false, false, false);

    public SourceSet Sources
    {
        get => _sources;
        init => _sources = value.Intersect(HostSources);
    }

    public bool Network { get; init; }

    public bool GrokLive { get; init; }

    public bool ClaudeBridge { get; init; }

    /// <summary>
    /// The app-wide switches for one host: Claude Code is that host's own bridge switch, and only while Claude Code is
    /// enabled. A source the app does not read (<paramref name="enabledSources"/>) reads no quota on the host either
    /// (QUOTAS 9.1); the bridge itself stays as the host's switch so it can always be restored.
    /// </summary>
    public static RemoteQuotaConfig ForHost(RemoteHost host, RemoteQuotaSwitches switches, bool networkEnabled, SourceSet? enabledSources = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(switches);
        var sources = switches.Sources.Without(SourceKind.ClaudeCode);
        if (host.QuotaClaudeCli && switches.ClaudeCode)
        {
            sources = sources.With(SourceKind.ClaudeCode);
        }

        if (enabledSources is { } enabled)
        {
            sources = sources.Intersect(enabled);
        }

        sources = sources.Intersect(HostSources);
        return new RemoteQuotaConfig(sources, networkEnabled, switches.GrokLive && sources.Contains(SourceKind.GrokBuild), host.QuotaClaudeCli);
    }

    /// <summary>The config with the Claude Code bridge off and its quota no longer read (restoring the host's status line).</summary>
    internal RemoteQuotaConfig WithoutBridge() => this with { ClaudeBridge = false, Sources = Sources.Without(SourceKind.ClaudeCode) };
}

/// <summary>The app's quota switches as they apply to every host.</summary>
/// <param name="Sources">The sources whose quota switch is on; Claude Code's comes from each host's own bridge switch.</param>
/// <param name="GrokLive">The app's <c>quotaGrokBuildLive</c>.</param>
/// <param name="ClaudeCode">Claude Code is enabled: the hosts' bridges may report its quota.</param>
public sealed record RemoteQuotaSwitches(SourceSet Sources = default, bool GrokLive = false, bool ClaudeCode = true);

/// <summary>A <c>quota</c> line (REMOTE 11.1): one source's snapshot on a host, with whether its agent runs and is signed in there.</summary>
/// <param name="Status"><see cref="QuotaStatusKind.Ok"/> carries the snapshot's time; <see cref="QuotaStatusKind.Denied"/> the vendor's host.</param>
public sealed record RemoteQuotaReport(string HostId, QuotaSnapshot Snapshot, bool Running, bool SignedIn, QuotaRouteStatus Status)
{
    public const int MaxWindows = 16;
    public const int MaxNameCharacters = 64;
    public const int MaxMinutes = 527_040;
    public const double MaxUsedPercent = 1000;

    public SourceKind Source => Snapshot.Source;
}

/// <summary>A <c>quotaBridge</c> line: the host's Claude Code status line bridge (QUOTAS 5.4 on the host).</summary>
/// <param name="SettingsPath">Abbreviated with <c>~</c>; displayed as plain text only, never used as a path.</param>
public sealed record RemoteQuotaBridge(string HostId, ClaudeCliBridgeStatus State, string SettingsPath);

/// <summary>
/// A host's quota line (REMOTE 11.1), times already local; raised on a background thread, in the order the host sent them.
/// Exactly one of <see cref="Report"/>, <see cref="RemovedSource"/> and <see cref="Bridge"/> is set.
/// </summary>
public sealed class RemoteQuotaEventArgs(string hostId, RemoteQuotaReport? report = null, SourceKind? removedSource = null, RemoteQuotaBridge? bridge = null)
    : EventArgs
{
    public string HostId { get; } = hostId;

    /// <summary>A <c>quota</c> line: the source's snapshot replaces the previous one.</summary>
    public RemoteQuotaReport? Report { get; } = report;

    /// <summary>A <c>quotaRemoved</c> line: the source's snapshot went.</summary>
    public SourceKind? RemovedSource { get; } = removedSource;

    /// <summary>A <c>quotaBridge</c> line: the state of the host's Claude Code bridge.</summary>
    public RemoteQuotaBridge? Bridge { get; } = bridge;
}
