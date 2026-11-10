using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary>
/// The plan Devin Desktop caches in its <c>state.vscdb</c> (QUOTAS 9.3): a daily and a weekly remaining percent. Several
/// rows are several accounts; the greatest <c>endTimestamp</c> wins. <c>accountIdentityText</c> is never read.
/// </summary>
public static class DevinPlan
{
    public static IReadOnlyList<string> KeyPatterns { get; } = ["windsurf.reactSettings.cachedPlanInfoData%", "windsurf.settings.cachedPlanInfo%"];

    /// <summary><paramref name="rows"/>: the query's values as UTF-8 bytes.</summary>
    public static IReadOnlyList<QuotaWindow> Windows(IEnumerable<ReadOnlyMemory<byte>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        JsonDocument? best = null;
        var bestEnd = 0.0;
        try
        {
            foreach (var row in rows)
            {
                if (row.Length > 1 << 20 || VendorValue.ParseObject(row) is not { } plan)
                {
                    continue;
                }

                var end = VendorValue.Number(VendorValue.Get(plan.RootElement, "endTimestamp")) is { } n && n != 0 ? n : 0;
                if (best is null || end > bestEnd)
                {
                    best?.Dispose();
                    (best, bestEnd) = (plan, end);
                }
                else
                {
                    plan.Dispose();
                }
            }

            if (best is null)
            {
                return [];
            }

            var root = best.RootElement;
            var windows = new List<QuotaWindow>(2);
            foreach (var (prefix, hide, kind, minutes) in Pools)
            {
                if (VendorValue.IsTrue(VendorValue.Get(root, hide))
                    || VendorValue.Number(VendorValue.Get(root, prefix + "RemainingPercent")) is not { } remaining)
                {
                    continue;
                }

                windows.Add(QuotaWindow.Of(kind, 100 - remaining, VendorValue.TimeMs(VendorValue.Get(root, prefix + "ResetAtUnix")), minutes: minutes));
            }

            return windows;
        }
        finally
        {
            best?.Dispose();
        }
    }

    /// <summary><c>&lt;folder&gt;/logs/&lt;YYYYMMDDTHHMMSS&gt;</c>: a launch folder's name read as local time.</summary>
    public static long? LaunchMs(string name, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(zone);
        if (name.Length != 15 || name[8] != 'T')
        {
            return null;
        }

        for (var i = 0; i < 15; i++)
        {
            if (i != 8 && !char.IsAsciiDigit(name[i]))
            {
                return null;
            }
        }

        int Number(int start, int count) => int.Parse(name.AsSpan(start, count), System.Globalization.CultureInfo.InvariantCulture);
        int year = Number(0, 4), month = Number(4, 2), day = Number(6, 2), hour = Number(9, 2), minute = Number(11, 2), second = Number(13, 2);
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(local) ? zone.GetUtcOffset(local.AddHours(1)) : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }

    private static readonly (string Prefix, string Hide, QuotaWindowKind Kind, int? Minutes)[] Pools =
    [
        ("daily", "hideDailyQuota", QuotaWindowKind.Other, VendorValue.DayMinutes),
        ("weekly", "hideWeeklyQuota", QuotaWindowKind.SevenDay, null),
    ];
}
