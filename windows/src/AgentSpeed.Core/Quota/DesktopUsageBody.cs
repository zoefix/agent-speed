using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSpeed.Core.Quota;

/// <summary>The windows of Claude Desktop's usage response (QUOTAS 4.3); every other field of the body is ignored.</summary>
internal static partial class DesktopUsageBody
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}(:\d{2}(\.\d{1,6})?)?([Zz]|[+-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoWithOffset();

    public static IReadOnlyList<QuotaWindow> Windows(JsonElement body)
    {
        var windows = new List<QuotaWindow>(4);
        if (body.ValueKind != JsonValueKind.Object)
        {
            return windows;
        }

        foreach (var (field, kind) in (ReadOnlySpan<(string, QuotaWindowKind)>)[("five_hour", QuotaWindowKind.FiveHour), ("seven_day", QuotaWindowKind.SevenDay)])
        {
            if (Utilization(body, field) is { } w)
            {
                windows.Add(QuotaWindow.Of(kind, w.Used, w.Resets));
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (body.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !IsString(item, "kind", "weekly_scoped")
                    || Number(item, "percent") is not { } percent)
                {
                    continue;
                }

                var name = item.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String
                        ? display.GetString()
                        : null;
                if (!string.IsNullOrWhiteSpace(name) && names.Add(name))
                {
                    windows.Add(QuotaWindow.Of(QuotaWindowKind.ModelWeek, percent, IsoMs(item, "resets_at"), name));
                }
            }
        }

        foreach (var (field, name) in (ReadOnlySpan<(string, string)>)[("seven_day_opus", "Opus"), ("seven_day_sonnet", "Sonnet")])
        {
            if (!names.Contains(name) && Utilization(body, field) is { } w)
            {
                names.Add(name);
                windows.Add(QuotaWindow.Of(QuotaWindowKind.ModelWeek, w.Used, w.Resets, name));
            }
        }

        return windows;
    }

    private static (double Used, long? Resets)? Utilization(JsonElement body, string field) =>
        body.TryGetProperty(field, out var w) && w.ValueKind == JsonValueKind.Object && Number(w, "utilization") is { } used
            ? (used, IsoMs(w, "resets_at"))
            : null;

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d)
            ? d
            : null;

    private static bool IsString(JsonElement obj, string name, string expected) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

    /// <summary>ISO 8601 with an offset (up to 6 fractional digits) in Unix ms, truncated; null otherwise.</summary>
    internal static long? IsoMs(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
            || !IsoWithOffset().IsMatch(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMillisecond
            : null;
    }
}
