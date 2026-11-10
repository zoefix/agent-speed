using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary>
/// <c>GET api.factory.ai/api/billing/limits</c> (QUOTAS 9.7): the standard pool and the optional <c>Core</c> pool, each with
/// a 5-hour, weekly and monthly window.
/// </summary>
public static class DroidLimits
{
    private static readonly (string Pool, string? Name)[] Pools = [("standard", null), ("core", "Core")];

    private static readonly (string Key, QuotaWindowKind Kind)[] Lengths =
        [("fiveHour", QuotaWindowKind.FiveHour), ("weekly", QuotaWindowKind.SevenDay), ("monthly", QuotaWindowKind.Month)];

    /// <summary>
    /// The <c>FACTORY_API_KEY=</c> line of <c>~/.factory/.env</c> (an <c>export </c> prefix and matching quotes allowed); every
    /// other line is passed over unread.
    /// </summary>
    public static string? KeyFromEnvFile(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var raw in text.Split('\n', '\r'))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].Trim();
            }

            if (!line.StartsWith("FACTORY_API_KEY=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["FACTORY_API_KEY=".Length..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
            {
                value = value[1..^1];
            }

            return value.Length == 0 ? null : value;
        }

        return null;
    }

    public static IReadOnlyList<QuotaWindow> Windows(JsonElement body, long nowMs)
    {
        var limits = VendorValue.Object(body, "limits");
        var windows = new List<QuotaWindow>();
        foreach (var (poolName, name) in Pools)
        {
            if (VendorValue.Object(limits, poolName) is not { } pool)
            {
                continue;
            }

            foreach (var (key, kind) in Lengths)
            {
                if (VendorValue.Object(pool, key) is not { } window || VendorValue.Number(VendorValue.Get(window, "usedPercent")) is not { } used)
                {
                    continue;
                }

                var end = VendorValue.TimeMs(VendorValue.Get(window, "windowEnd"));
                if (VendorValue.Number(VendorValue.Get(window, "secondsRemaining")) is { } remaining)
                {
                    windows.Add(QuotaWindow.Of(kind, used, unchecked(nowMs + VendorValue.Truncated(remaining * 1000)), name));
                }
                else if (end is { } past && past <= nowMs)
                {
                    windows.Add(QuotaWindow.Of(kind, 0, null, name));
                }
                else
                {
                    windows.Add(QuotaWindow.Of(kind, used, end, name));
                }
            }
        }

        return windows;
    }
}
