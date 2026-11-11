using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary><c>GET api.kimi.com/coding/v1/usages</c> (QUOTAS 9.5): the current <c>usages</c> shape, else the legacy <c>limits</c> and <c>usage</c>.</summary>
public static class KimiUsage
{
    private static readonly Dictionary<string, int> TimeUnits = new(StringComparer.Ordinal)
    {
        ["TIME_UNIT_MINUTE"] = 1,
        ["TIME_UNIT_HOUR"] = 60,
        ["TIME_UNIT_DAY"] = VendorValue.DayMinutes,
        ["TIME_UNIT_WEEK"] = VendorValue.WeekMinutes,
    };

    public static IReadOnlyList<QuotaWindow> Windows(JsonElement body)
    {
        var windows = new List<QuotaWindow>();
        if (VendorValue.Object(body, "usages") is { } usages)
        {
            foreach (var (key, kind) in new[] { ("limit_5h", QuotaWindowKind.FiveHour), ("limit_7d", QuotaWindowKind.SevenDay) })
            {
                if (VendorValue.Object(usages, key) is { } window && VendorValue.Number(VendorValue.Get(window, "used_ratio")) is { } ratio)
                {
                    windows.Add(QuotaWindow.Of(kind, ratio * 100, Reset(window)));
                }
            }

            foreach (var key in new[] { "limit_month_total", "limit_month" })
            {
                if (VendorValue.Object(usages, key) is { } window && VendorValue.Number(VendorValue.Get(window, "used_ratio")) is { } ratio)
                {
                    windows.Add(QuotaWindow.Of(QuotaWindowKind.Month, ratio * 100, Reset(window)));
                    break;
                }
            }

            if (windows.Count > 0)
            {
                return windows;
            }
        }

        foreach (var item in VendorValue.Items(VendorValue.Get(body, "limits")))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var window = VendorValue.Object(item, "window");
            var detail = VendorValue.Object(item, "detail");
            if (VendorValue.Number(VendorValue.Get(window, "duration")) is not { } duration
                || VendorValue.String(VendorValue.Get(window, "timeUnit")) is not { } unitName || !TimeUnits.TryGetValue(unitName, out var unit)
                || Counts(detail) is not { } used)
            {
                continue;
            }

            var minutes = VendorValue.Minutes(duration * unit);
            var kind = VendorValue.KindFor(minutes);
            windows.Add(QuotaWindow.Of(kind, used, Reset(detail), minutes: kind == QuotaWindowKind.Other ? minutes : null));
        }

        if (VendorValue.Object(body, "usage") is { } usage && !windows.Any(w => w.Kind == QuotaWindowKind.SevenDay) && Counts(usage) is { } total)
        {
            windows.Add(QuotaWindow.Of(QuotaWindowKind.SevenDay, total, Reset(usage)));
        }

        return windows;
    }

    private static long? Reset(JsonElement? members) =>
        VendorValue.Pick(members, "resetTime", "reset_time", "resetAt", "reset_at") is { } value ? VendorValue.TimeMs(value) : null;

    /// <summary><c>used / limit × 100</c>, with <c>used = limit − remaining</c> when <c>used</c> is missing.</summary>
    private static double? Counts(JsonElement? members)
    {
        if (VendorValue.Number(VendorValue.Get(members, "limit")) is not { } limit || limit <= 0)
        {
            return null;
        }

        var used = VendorValue.Number(VendorValue.Get(members, "used"));
        if (used is null && VendorValue.Number(VendorValue.Get(members, "remaining")) is { } remaining)
        {
            used = limit - remaining;
        }

        return used / limit * 100;
    }
}
