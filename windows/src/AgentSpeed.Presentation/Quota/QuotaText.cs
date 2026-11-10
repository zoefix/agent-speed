using System.Globalization;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Quota;

/// <summary>The quota texts of QUOTAS 7: long and short resets, the age, the pace and the spoken windows.</summary>
public static class QuotaText
{
    private const long MinuteMs = 60_000;
    private const long HourMs = 60 * MinuteMs;
    private const long DayMs = 24 * HourMs;

    public static QuotaLabels Labels(Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return new QuotaLabels(loc["quota.window.month"], loc["quota.window.credits"]);
    }

    /// <summary>
    /// The dashboard's reset: <c>42 分後重置</c> under an hour, <c>3 小時 12 分後重置</c> under a day (minutes rounded down, an
    /// exact hour without them), else <c>週五 09:00 重置</c> (<c>11/17 09:00 重置</c> on today's weekday).
    /// </summary>
    public static string ResetLong(long resetsAtMs, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(loc);
        zone ??= TimeZoneInfo.Local;
        var d = resetsAtMs - nowMs;
        if (d < DayMs)
        {
            return loc.Format("quota.reset.in", Duration(Math.Max(0, d), loc));
        }

        var at = Local(resetsAtMs, zone);
        var time = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        return loc.Format("quota.reset.at", WeekdayOrDate(at, Local(nowMs, zone), loc) + " " + time);
    }

    /// <summary>The orb card's reset, the same in every language: <c>42m</c>, <c>3h</c>, else the weekday (<c>M/d</c> on today's weekday).</summary>
    public static string ResetShort(long resetsAtMs, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(loc);
        zone ??= TimeZoneInfo.Local;
        var d = resetsAtMs - nowMs;
        if (d < HourMs)
        {
            return Whole(Math.Max(0, d) / MinuteMs) + "m";
        }

        if (d < DayMs)
        {
            return Whole(d / HourMs) + "h";
        }

        return WeekdayOrDate(Local(resetsAtMs, zone), Local(nowMs, zone), loc);
    }

    /// <summary><c>quota.age</c> once the snapshot is an hour old (whole hours, from a day whole days); null before.</summary>
    public static string? Age(long observedMs, long nowMs, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var age = nowMs - observedMs;
        if (age < HourMs)
        {
            return null;
        }

        var duration = age < DayMs
            ? loc.Format("quota.duration.h", age / HourMs)
            : loc.Format("quota.duration.d", age / DayMs);
        return loc.Format("quota.age", duration);
    }

    /// <summary><c>quota.pace</c> with the share of the window that has passed as a whole percent.</summary>
    public static string Pace(double pace, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("quota.pace", (int)Math.Round(Math.Clamp(pace, 0, 1) * 100, MidpointRounding.AwayFromZero));
    }

    /// <summary><c>quota.updated</c> with the snapshot's time: the time alone today, else the date and time.</summary>
    public static string Updated(long observedMs, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("quota.updated", When(observedMs, nowMs, loc, zone));
    }

    /// <summary>A short time today, else the short date and time, in the app language.</summary>
    public static string When(long ms, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(loc);
        zone ??= TimeZoneInfo.Local;
        var at = Local(ms, zone);
        var today = Local(nowMs, zone).Date == at.Date;
        return today ? at.ToString("t", loc.Culture) : at.ToString("g", loc.Culture);
    }

    /// <summary><c>a11y.quota.window</c> (label, percent, long reset), or <c>a11y.quota.windowNoReset</c> without a reset.</summary>
    public static string Spoken(QuotaShownWindow window, long nowMs, Localizer loc, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(loc);
        return window.ResetsAtMs is { } reset
            ? loc.Format("a11y.quota.window", window.Label, window.Percent, ResetLong(reset, nowMs, loc, zone))
            : loc.Format("a11y.quota.windowNoReset", window.Label, window.Percent);
    }

    private static readonly string[] Percents = [.. Enumerable.Range(0, 101).Select(i => Whole(i) + "%")];

    /// <summary>The percent text: <c>23%</c> in every language (shared strings: the orb draws them every frame).</summary>
    public static string Percent(int percent) => percent is >= 0 and <= 100 ? Percents[percent] : Whole(percent) + "%";

    private static string Duration(long ms, Localizer loc)
    {
        var minutes = ms / MinuteMs;
        if (ms < HourMs)
        {
            return loc.Format("quota.duration.m", minutes);
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? loc.Format("quota.duration.h", hours) : loc.Format("quota.duration.hm", hours, rest);
    }

    private static string WeekdayOrDate(DateTime at, DateTime now, Localizer loc) =>
        at.DayOfWeek == now.DayOfWeek
            ? Whole(at.Month) + "/" + Whole(at.Day)
            : loc.Culture.DateTimeFormat.GetAbbreviatedDayName(at.DayOfWeek);

    private static DateTime Local(long ms, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddMilliseconds(ms), zone);

    private static string Whole(long value) => value.ToString(CultureInfo.InvariantCulture);
}
