namespace AgentSpeed.Presentation.Updates;

public enum UpdateOffer
{
    UpToDate,
    Available,

    /// <summary>Newer, but the user skipped this version; only a manual check offers it again.</summary>
    Skipped,
}

/// <summary>When to check and what to offer (UPDATES sections 2 and 3). Pure functions of the clock and settings.</summary>
public static class UpdateSchedule
{
    public static TimeSpan LaunchDelay { get; } = TimeSpan.FromSeconds(30);

    public static TimeSpan Interval { get; } = TimeSpan.FromHours(6);

    /// <summary>Longest timer step, so sleep, resume and clock changes are noticed within this time.</summary>
    public static TimeSpan MaxTimerStep { get; } = TimeSpan.FromMinutes(15);

    public static TimeSpan MinTimerStep { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Automatic checks need the setting on and a release build (0.0.0 checks only when asked).</summary>
    public static bool AutomaticChecksAllowed(bool autoCheckSetting, SemanticVersion? currentVersion) =>
        autoCheckSetting && currentVersion is { IsDevelopment: false };

    /// <summary>
    /// The first automatic check after launch (or after automatic checks are switched on): 30 s from now, or later
    /// when the last successful check was less than 6 h ago. A last check in the future (clock moved back) is ignored.
    /// </summary>
    public static DateTimeOffset FirstCheck(DateTimeOffset now, DateTimeOffset? lastSuccessfulCheck)
    {
        var earliest = now + LaunchDelay;
        if (lastSuccessfulCheck is not { } last || last > now)
        {
            return earliest;
        }

        var due = last + Interval;
        return due > earliest ? due : earliest;
    }

    /// <summary>After any automatic check, successful or not (failures and rate limits wait for the next interval).</summary>
    public static DateTimeOffset NextCheck(DateTimeOffset now) => now + Interval;

    public static TimeSpan TimerStep(DateTimeOffset now, DateTimeOffset due)
    {
        var remaining = due - now;
        if (remaining < MinTimerStep)
        {
            return MinTimerStep;
        }

        return remaining > MaxTimerStep ? MaxTimerStep : remaining;
    }

    public static bool IsDue(DateTimeOffset now, DateTimeOffset due) => now >= due;

    public static UpdateOffer Evaluate(SemanticVersion currentVersion, ReleaseInfo? latest, string? skippedVersion, bool manual)
    {
        if (latest is not { IsOfferable: true } release || !release.Version.IsNewerThan(currentVersion))
        {
            return UpdateOffer.UpToDate;
        }

        return !manual && IsSkipped(release, skippedVersion) ? UpdateOffer.Skipped : UpdateOffer.Available;
    }

    public static bool IsSkipped(ReleaseInfo release, string? skippedVersion) => IsSameVersion(release, skippedVersion);

    /// <summary>One notification per version: true unless <paramref name="notifiedVersion"/> is this release.</summary>
    public static bool ShouldNotify(ReleaseInfo release, string? notifiedVersion) => !IsSameVersion(release, notifiedVersion);

    /// <summary>Compares a stored tag with a release by version (so "v1.2.0" and "1.2.0" match), else by tag text.</summary>
    public static bool IsSameVersion(ReleaseInfo release, string? storedTag)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (string.IsNullOrWhiteSpace(storedTag))
        {
            return false;
        }

        return SemanticVersion.TryParse(storedTag, out var stored)
            ? stored == release.Version
            : string.Equals(storedTag.Trim(), release.Tag, StringComparison.OrdinalIgnoreCase);
    }
}
