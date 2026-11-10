using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary>
/// Gemini CLI's Code Assist quota (QUOTAS 9.6): <c>loadCodeAssist</c> gives the project, <c>retrieveUserQuota</c> the daily
/// buckets per model, grouped by family with the lowest remaining fraction.
/// </summary>
public static class GeminiQuota
{
    public static string? Family(string? modelId)
    {
        var folded = (modelId ?? string.Empty).ToLowerInvariant();
        return folded.Contains("flash-lite", StringComparison.Ordinal) ? "Flash Lite"
            : folded.Contains("flash", StringComparison.Ordinal) ? "Flash"
            : folded.Contains("pro", StringComparison.Ordinal) ? "Pro"
            : modelId;
    }

    /// <summary><c>cloudaicompanionProject</c>: a string, or an object's <c>id</c>.</summary>
    public static string? Project(JsonElement body)
    {
        var project = VendorValue.Get(body, "cloudaicompanionProject");
        return VendorValue.Name(project) ?? VendorValue.String(VendorValue.Get(VendorValue.Object(project), "id"));
    }

    public static IReadOnlyList<QuotaWindow> Windows(JsonElement body)
    {
        var best = new Dictionary<string, (double Remaining, long? Resets)>(StringComparer.Ordinal);
        foreach (var bucket in VendorValue.Items(VendorValue.Get(body, "buckets")))
        {
            if (VendorValue.StrictNumber(VendorValue.Get(bucket, "remainingFraction")) is not { } remaining
                || Family(VendorValue.String(VendorValue.Get(bucket, "modelId"))) is not { Length: > 0 } family)
            {
                continue;
            }

            if (!best.TryGetValue(family, out var known) || remaining < known.Remaining)
            {
                best[family] = (remaining, VendorValue.TimeMs(VendorValue.Get(bucket, "resetTime")));
            }
        }

        return [.. best.Keys.OrderBy(Rank).ThenBy(f => f, StringComparer.Ordinal).Select(f =>
            QuotaWindow.Of(QuotaWindowKind.Other, (1 - best[f].Remaining) * 100, best[f].Resets, f, VendorValue.DayMinutes))];
    }

    private static int Rank(string family) => family switch
    {
        "Pro" => 0,
        "Flash" => 1,
        "Flash Lite" => 2,
        _ => 3,
    };
}
