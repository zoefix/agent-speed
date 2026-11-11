using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary>The two browser-session sources (QUOTAS 9.9): Qoder's big-model credits and WorkBuddy's resource packages.</summary>
public static class BrowserSessionCredits
{
    /// <summary><c>totalQuota</c> and <c>sharedQuota</c> summed into one <c>credits</c> window.</summary>
    public static IReadOnlyList<QuotaWindow> QoderWindows(JsonElement body)
    {
        double used = 0, limit = 0;
        var seen = false;
        var percents = new List<double>(2);
        foreach (var key in new[] { "totalQuota", "sharedQuota" })
        {
            if (VendorValue.Object(VendorValue.Object(body, key), "quotaSummary") is not { } summary)
            {
                continue;
            }

            var value = VendorValue.Number(VendorValue.Pick(summary, "usedValue", "used_value"));
            var cap = VendorValue.Number(VendorValue.Pick(summary, "limitValue", "limit_value"));
            if (value is { } v && cap is > 0)
            {
                used += v;
                limit += cap.Value;
                seen = true;
            }
            else if (VendorValue.Number(VendorValue.Pick(summary, "usagePercentage", "usage_percentage")) is { } percent)
            {
                percents.Add(percent);
            }
        }

        var resets = VendorValue.Pick(body, "nextResetAt", "next_reset_at") is { } reset ? VendorValue.TimeMs(reset) : null;
        if (seen)
        {
            return [QuotaWindow.Of(QuotaWindowKind.Credits, used / limit * 100, resets)];
        }

        return percents.Count > 0 ? [QuotaWindow.Of(QuotaWindowKind.Credits, percents.Max(), resets)] : [];
    }

    /// <summary>Null unless <c>code == 0</c>: the <c>credits</c> packages summed into one monthly window without a reset time.</summary>
    public static IReadOnlyList<QuotaWindow>? WorkBuddyWindows(JsonElement body)
    {
        if (VendorValue.StrictNumber(VendorValue.Get(body, "code")) is not 0.0)
        {
            return null;
        }

        double total = 0, remain = 0;
        var seen = false;
        foreach (var package in VendorValue.Items(VendorValue.Get(VendorValue.Object(body, "data"), "Packages")))
        {
            if (VendorValue.String(VendorValue.Get(package, "CapacityUnit")) != "credits"
                || VendorValue.Number(VendorValue.Get(package, "CycleTotalCapacity")) is not { } cycle
                || VendorValue.Number(VendorValue.Get(package, "CycleRemainCapacity")) is not { } left)
            {
                continue;
            }

            total += cycle;
            remain += left;
            seen = true;
        }

        return seen && total > 0 ? [QuotaWindow.Of(QuotaWindowKind.Month, (total - remain) / total * 100, null)] : [];
    }
}
