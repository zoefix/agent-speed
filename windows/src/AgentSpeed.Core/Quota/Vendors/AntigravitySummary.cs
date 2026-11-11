using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary>
/// The language server's answers (QUOTAS 9.4): <c>RetrieveUserQuotaSummary</c> groups of buckets, or the IDE server's
/// <c>GetUserStatus</c> model configs.
/// </summary>
public static class AntigravitySummary
{
    private static readonly string[] WeekWords = ["weekly", "week", "7d"];
    private static readonly string[] FiveHourWords = ["5h", "5-hour", "five-hour", "five hour", "session"];

    /// <summary>A group's model family from its display name.</summary>
    public static string? GroupName(string? display)
    {
        var folded = (display ?? string.Empty).ToLowerInvariant();
        if (folded.Contains("gemini", StringComparison.Ordinal) && folded.Contains("argon", StringComparison.Ordinal))
        {
            return "Gemini Argon";
        }

        if (folded.Contains("gemini", StringComparison.Ordinal))
        {
            return "Gemini";
        }

        if (folded.Contains("claude", StringComparison.Ordinal) || folded.Contains("gpt", StringComparison.Ordinal)
            || folded.Contains("third", StringComparison.Ordinal))
        {
            return "Claude/GPT";
        }

        return string.IsNullOrEmpty(display) ? null : display;
    }

    /// <summary>The window a bucket covers, from its <c>window</c>, <c>bucketId</c> and <c>displayName</c>; null for any other.</summary>
    public static QuotaWindowKind? Kind(JsonElement bucket)
    {
        var text = string.Join(' ', VendorValue.Text(VendorValue.Get(bucket, "window")), VendorValue.Text(VendorValue.Get(bucket, "bucketId")),
            VendorValue.Text(VendorValue.Get(bucket, "displayName"))).ToLowerInvariant().Replace('_', '-');
        if (WeekWords.Any(w => text.Contains(w, StringComparison.Ordinal)))
        {
            return QuotaWindowKind.SevenDay;
        }

        return FiveHourWords.Any(w => text.Contains(w, StringComparison.Ordinal)) ? QuotaWindowKind.FiveHour : null;
    }

    /// <summary><c>remainingFraction</c> as a number, a <c>{case, value}</c> oneof, or under <c>remaining</c>.</summary>
    public static double? Remaining(JsonElement bucket)
    {
        var fraction = VendorValue.Get(bucket, "remainingFraction");
        if (VendorValue.StrictNumber(fraction) is { } value)
        {
            return value;
        }

        if (VendorValue.Object(fraction) is { } oneOf && VendorValue.String(VendorValue.Get(oneOf, "case")) == "remainingFraction"
            && VendorValue.StrictNumber(VendorValue.Get(oneOf, "value")) is { } inner)
        {
            return inner;
        }

        return VendorValue.StrictNumber(VendorValue.Get(VendorValue.Object(bucket, "remaining"), "remainingFraction"));
    }

    public static IReadOnlyList<QuotaWindow> SummaryWindows(JsonElement body)
    {
        var root = VendorValue.Object(body, "response") ?? VendorValue.Object(body);
        var windows = new List<QuotaWindow>();
        foreach (var group in VendorValue.Items(VendorValue.Get(root, "groups")))
        {
            if (group.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GroupName(VendorValue.String(VendorValue.Get(group, "displayName")));
            foreach (var bucket in VendorValue.Items(VendorValue.Get(group, "buckets")))
            {
                if (bucket.ValueKind != JsonValueKind.Object || VendorValue.IsTrue(VendorValue.Get(bucket, "disabled"))
                    || Kind(bucket) is not { } kind || Remaining(bucket) is not { } remaining)
                {
                    continue;
                }

                windows.Add(QuotaWindow.Of(kind, (1 - remaining) * 100, VendorValue.TimeMs(VendorValue.Get(bucket, "resetTime")), name));
            }
        }

        return windows;
    }

    public static IReadOnlyList<QuotaWindow> StatusWindows(JsonElement body)
    {
        var data = VendorValue.Object(VendorValue.Object(body, "userStatus"), "cascadeModelConfigData");
        var windows = new List<QuotaWindow>();
        foreach (var config in VendorValue.Items(VendorValue.Get(data, "clientModelConfigs")))
        {
            if (VendorValue.Object(config, "quotaInfo") is not { } info || VendorValue.StrictNumber(VendorValue.Get(info, "remainingFraction")) is not { } remaining)
            {
                continue;
            }

            windows.Add(QuotaWindow.Of(QuotaWindowKind.FiveHour, (1 - remaining) * 100, VendorValue.TimeMs(VendorValue.Get(info, "resetTime")),
                VendorValue.Name(VendorValue.Get(config, "label"))));
        }

        return windows;
    }
}
