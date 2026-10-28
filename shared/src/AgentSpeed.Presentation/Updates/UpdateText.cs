using System.Globalization;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Updates;

/// <summary>Localized update text (strings <c>update.*</c>).</summary>
public static class UpdateText
{
    public static string FailureKey(UpdateFailure failure) => failure switch
    {
        UpdateFailure.Checksum => "update.failed.checksum",
        UpdateFailure.Permission => "update.failed.permission",
        UpdateFailure.Invalid => "update.failed.invalid",
        _ => "update.failed.network",
    };

    public static string FailureReason(UpdateFailure failure, Localizer localizer) => localizer[FailureKey(failure)];

    /// <summary>"Update failed: reason" for install errors.</summary>
    public static string InstallFailed(UpdateFailure failure, Localizer localizer) =>
        localizer.Format("update.failed", FailureReason(failure, localizer));

    public static string Current(string currentVersion, Localizer localizer) => localizer.Format("update.current", currentVersion);

    public static string AvailableTitle(ReleaseInfo release, Localizer localizer) =>
        localizer.Format("update.available.title", release.DisplayVersion);

    public static string MenuItem(ReleaseInfo release, Localizer localizer) => localizer.Format("update.menu", release.DisplayVersion);

    public static string Pill(ReleaseInfo release, Localizer localizer) => localizer.Format("update.available.short", release.DisplayVersion);

    public static string Percent(double progress, CultureInfo culture)
    {
        var value = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        return Math.Floor((value * 100) + 1e-9).ToString("0", culture) + "%";
    }

    public static string Downloading(double progress, Localizer localizer) =>
        localizer.Format("update.downloading", Percent(progress, localizer.Culture));

    /// <summary>One line for the settings About section and the update window; empty while idle.</summary>
    public static string StatusLine(UpdateStatus status, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Phase switch
        {
            UpdatePhase.Checking => localizer["update.checking"],
            UpdatePhase.UpToDate => localizer["update.upToDate"],
            UpdatePhase.Available when status.Release is { } release => AvailableTitle(release, localizer),
            UpdatePhase.Downloading => Downloading(status.Progress, localizer),
            UpdatePhase.Verifying => localizer["update.verifying"],
            UpdatePhase.Installing or UpdatePhase.Relaunching => localizer["update.installing"],
            UpdatePhase.CheckFailed => FailureReason(status.Failure ?? UpdateFailure.Network, localizer),
            UpdatePhase.InstallFailed => InstallFailed(status.Failure ?? UpdateFailure.Invalid, localizer),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// "Last checked …": just now, minutes ago, the time today, or date and time; empty when never checked.
    /// </summary>
    public static string LastChecked(DateTimeOffset? lastCheck, DateTimeOffset now, Localizer localizer, TimeZoneInfo? zone = null)
    {
        if (lastCheck is not { } last)
        {
            return string.Empty;
        }

        return localizer.Format("update.lastChecked", RelativeTime(last, now, localizer, zone ?? TimeZoneInfo.Local));
    }

    internal static string RelativeTime(DateTimeOffset last, DateTimeOffset now, Localizer localizer, TimeZoneInfo zone)
    {
        var elapsed = now - last;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return localizer["time.justNow"];
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return localizer.Format("time.minutesAgo", (int)elapsed.TotalMinutes);
        }

        var local = TimeZoneInfo.ConvertTime(last, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        var culture = localizer.Culture;
        return local.Date == today.Date
            ? local.ToString("t", culture)
            : local.ToString("g", culture);
    }
}
