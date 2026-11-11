using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Presentation.Remote;

/// <summary>What leads the host bridge's status line: nothing (waiting), the check, or the caution triangle.</summary>
public enum RemoteBridgeMark
{
    None,
    Ok,
    Warning,
}

/// <summary>The host bridge's status line in the host editor (DESIGN 13 <i>Host quotas</i>), in the words of QUOTAS 5.4.</summary>
/// <param name="Reconnect">The status line was replaced: <c>settings.quota.cli.reconnect</c> follows the text.</param>
public sealed record RemoteBridgeLine(string Text, RemoteBridgeMark Mark, bool Reconnect)
{
    public static RemoteBridgeLine None { get; } = new(string.Empty, RemoteBridgeMark.None, false);

    public bool IsEmpty => Text.Length == 0;
}

/// <summary>The texts of a host's quotas (DESIGN 13 <i>Host quotas</i>, REMOTE 2.4 and 11.3).</summary>
public static class RemoteHostQuotaText
{
    /// <summary>Claude Code's own settings file, while the host's relay has not reported another one.</summary>
    public const string DefaultSettingsPath = "~/.claude/settings.json";

    /// <summary>The settings path on the host the confirmation names: the one the bridge reported, else Claude Code's default.</summary>
    public static string SettingsPath(HostQuotaState host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Bridge is { SettingsPath.Length: > 0 } bridge ? bridge.SettingsPath : DefaultSettingsPath;
    }

    /// <summary>
    /// The bridge's state under the editor's switch, while both the draft and the saved host have it on (<paramref name="shown"/>):
    /// waiting, connected with the quota's time, replaced (with Reconnect), unreadable or failed with the host's settings path.
    /// Nothing while off; connected without a quota yet reads as waiting.
    /// </summary>
    public static RemoteBridgeLine Bridge(HostQuotaState host, bool shown, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(loc);
        if (!shown || host.Bridge is not { State: not ClaudeCliBridgeStatus.Off } bridge)
        {
            return RemoteBridgeLine.None;
        }

        var path = SettingsPath(host);
        return bridge.State switch
        {
            ClaudeCliBridgeStatus.Ok when host.Reports.GetValueOrDefault(SourceKind.ClaudeCode) is { } report =>
                new RemoteBridgeLine(loc.Format("settings.quota.cli.ok", QuotaText.When(report.Snapshot.ObservedMs, nowMs, loc, zone)), RemoteBridgeMark.Ok, false),
            ClaudeCliBridgeStatus.Replaced => new RemoteBridgeLine(loc["settings.quota.cli.replaced"], RemoteBridgeMark.Warning, true),
            ClaudeCliBridgeStatus.Unreadable => new RemoteBridgeLine(loc.Format("settings.quota.cli.unreadable", path), RemoteBridgeMark.Warning, false),
            ClaudeCliBridgeStatus.Failed => new RemoteBridgeLine(loc.Format("settings.quota.cli.failed", path), RemoteBridgeMark.Warning, false),
            _ => new RemoteBridgeLine(loc["settings.quota.cli.waiting"], RemoteBridgeMark.None, false),
        };
    }

    /// <summary>
    /// The removal confirmation's body (REMOTE 2.4): where the relay files stay and, for a host whose bridge is on, what stays
    /// on the host if it cannot be reached (<c>remote.quota.removeNote</c>).
    /// </summary>
    public static string RemovalBody(RemoteHost host, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(loc);
        var folder = Unbroken(host.EffectiveRelayFolder);
        var files = loc.Format("remote.remove.files", folder);
        return host.QuotaClaudeCli ? files + "\n\n" + loc.Format("remote.quota.removeNote", folder) : files;
    }

    /// <summary>
    /// A path that reads as one piece in a wrapped sentence: its hyphens are non-breaking (U+2011) and a word joiner (U+2060)
    /// follows each slash; a path longer than the line still breaks where it must.
    /// </summary>
    public static string Unbroken(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('-', '\u2011').Replace("/", "/\u2060", StringComparison.Ordinal);
    }
}

/// <summary>One source of a host row in Dashboard → Hosts (DESIGN 13 <i>Host quotas</i>): its name and its quota lines.</summary>
public sealed record HostQuotaGroup(SourceKind Source, string Name, IReadOnlyList<QuotaLine> Lines)
{
    /// <summary>The source's name followed by its windows, as the host row's label speaks it.</summary>
    public string Spoken(Localizer loc) => Name + " " + QuotaLines.Spoken(Lines, loc);
}

/// <summary>The quota sub-lines of a host row: each source with windows observed within a day, in source order.</summary>
public static class HostQuotaGroups
{
    public static IReadOnlyList<HostQuotaGroup> Of(QuotaState state, string hostId, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(loc);
        if (hostId == RemoteHost.LocalId)
        {
            return [];
        }

        var snapshots = state.HostSnapshots(hostId, nowMs);
        var groups = new List<HostQuotaGroup>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var lines = QuotaLines.Of(snapshot, nowMs, loc, zone);
            if (lines.Count > 0)
            {
                groups.Add(new HostQuotaGroup(snapshot.Source, loc[DisplayText.SourceKey(snapshot.Source)], lines));
            }
        }

        return groups;
    }

    /// <summary><c>, Codex 7d 40% used, …</c> after the row's cost; empty without groups.</summary>
    public static string Spoken(IReadOnlyList<HostQuotaGroup> groups, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(loc);
        return string.Join(loc["list.separator"], groups.Select(g => g.Spoken(loc)));
    }
}
