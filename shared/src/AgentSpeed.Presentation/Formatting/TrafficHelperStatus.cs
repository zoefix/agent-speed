using AgentSpeed.Core.Traffic;
using AgentSpeed.Core.Traffic.Helper;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Formatting;

/// <summary>The traffic helper's states in Settings → 網路流量 (DESIGN 10; WINDOWS-SERVICE 6).</summary>
public enum TrafficHelperStatusKind
{
    NotInstalled,

    /// <summary>Installed, stopped (it starts on demand).</summary>
    Idle,

    Running,

    Outdated,

    /// <summary>Verification failed or access was denied, or another error (with its text).</summary>
    Error,
}

/// <summary>The color of the status dot; the text always says the same.</summary>
public enum TrafficHelperDot
{
    Neutral,
    Success,
    Caution,
    Critical,
}

/// <summary>What the helper section shows: the status line, its dot and which buttons apply.</summary>
/// <param name="ShowsInstall">The primary button installs (<c>traffic.helper.install</c>).</param>
/// <param name="ShowsUpdate">The primary button updates (<c>traffic.helper.update</c>).</param>
/// <param name="ShowsRemove">The destructive text button removes the helper.</param>
public sealed record TrafficHelperStatus(
    TrafficHelperStatusKind Kind,
    string Text,
    TrafficHelperDot Dot,
    bool ShowsInstall,
    bool ShowsUpdate,
    bool ShowsRemove)
{
    /// <param name="installed">The service and its registry key exist.</param>
    /// <param name="allowed">The current user is among the helper's allowed users.</param>
    /// <param name="installedVersion">The installed helper's version, when readable.</param>
    /// <param name="running">The service runs now.</param>
    /// <param name="state">The traffic sampler's state (denied and failures show as errors).</param>
    /// <param name="error">The last failure's text (the Win32 message), if any.</param>
    public static TrafficHelperStatus From(bool installed, bool allowed, HelperVersion? installedVersion, bool running,
        ProcessTrafficState state, string? error, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var bundled = HelperProtocol.BundledVersion;
        if (!installed || !allowed)
        {
            return new TrafficHelperStatus(TrafficHelperStatusKind.NotInstalled, localizer["traffic.helper.notInstalled"], TrafficHelperDot.Neutral,
                ShowsInstall: true, ShowsUpdate: false, ShowsRemove: false);
        }

        if (installedVersion is { } version && version < bundled)
        {
            return new TrafficHelperStatus(TrafficHelperStatusKind.Outdated,
                localizer.Format("traffic.helper.outdated", version.ToString(), bundled.ToString()), TrafficHelperDot.Caution,
                ShowsInstall: false, ShowsUpdate: true, ShowsRemove: true);
        }

        if (state.UnavailableReason is { } reason and not TrafficUnavailableReason.HelperMissing and not TrafficUnavailableReason.HelperOutdated)
        {
            var detail = reason == TrafficUnavailableReason.Denied || string.IsNullOrWhiteSpace(error)
                ? localizer[TrafficText.ReasonKey(reason)]
                : error.Trim();
            return new TrafficHelperStatus(TrafficHelperStatusKind.Error, localizer.Format("traffic.helper.error", detail), TrafficHelperDot.Critical,
                ShowsInstall: false, ShowsUpdate: false, ShowsRemove: true);
        }

        return running
            ? new TrafficHelperStatus(TrafficHelperStatusKind.Running,
                localizer.Format("traffic.helper.running", (installedVersion ?? bundled).ToString()), TrafficHelperDot.Success,
                ShowsInstall: false, ShowsUpdate: false, ShowsRemove: true)
            : new TrafficHelperStatus(TrafficHelperStatusKind.Idle, localizer["traffic.helper.idle"], TrafficHelperDot.Neutral,
                ShowsInstall: false, ShowsUpdate: false, ShowsRemove: true);
    }
}
