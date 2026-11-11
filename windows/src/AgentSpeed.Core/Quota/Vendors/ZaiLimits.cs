using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary><c>GET &lt;base&gt;/api/monitor/usage/quota/limit</c> of the GLM Coding Plan (QUOTAS 9.8), for ZCode.</summary>
public static class ZaiLimits
{
    /// <summary>Minutes per <c>unit</c>.</summary>
    private static readonly Dictionary<int, int> Units = new() { [1] = VendorValue.DayMinutes, [3] = 60, [5] = 1, [6] = VendorValue.WeekMinutes };

    /// <summary>Null unless <c>success == true</c> and <c>code == 200</c>.</summary>
    public static IReadOnlyList<QuotaWindow>? Windows(JsonElement body, long nowMs)
    {
        if (!VendorValue.IsTrue(VendorValue.Get(body, "success")) || VendorValue.StrictNumber(VendorValue.Get(body, "code")) is not 200.0)
        {
            return null;
        }

        var windows = new List<QuotaWindow>();
        foreach (var item in VendorValue.Items(VendorValue.Get(VendorValue.Object(body, "data"), "limits")))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var usage = VendorValue.Number(VendorValue.Get(item, "usage"));
            var current = VendorValue.Number(VendorValue.Get(item, "currentValue"));
            var used = usage is > 0 && current is { } value ? value / usage.Value * 100 : VendorValue.Number(VendorValue.Get(item, "percentage"));
            if (used is null)
            {
                continue;
            }

            var resets = VendorValue.TimeMs(VendorValue.Get(item, "nextResetTime"));
            var type = VendorValue.String(VendorValue.Get(item, "type"));
            if (type == "TIME_LIMIT")
            {
                windows.Add(QuotaWindow.Of(QuotaWindowKind.Month, used.Value, resets, "MCP"));
                continue;
            }

            if (type is not ("TOKENS_LIMIT" or "CREDIT_LIMIT") || VendorValue.StrictNumber(VendorValue.Get(item, "unit")) is not { } unitValue
                || unitValue != Math.Round(unitValue) || Math.Abs(unitValue) > int.MaxValue || !Units.TryGetValue((int)unitValue, out var unit)
                || VendorValue.Number(VendorValue.Get(item, "number")) is not { } count)
            {
                continue;
            }

            var minutes = VendorValue.Minutes(count * unit);
            var kind = VendorValue.KindFor(minutes);
            if (kind == QuotaWindowKind.FiveHour && resets is { } at && at - nowMs > 301 * 60_000L)
            {
                resets = null;
            }

            windows.Add(QuotaWindow.Of(kind, used.Value, resets, minutes: kind == QuotaWindowKind.Other ? minutes : null));
        }

        return windows;
    }
}
