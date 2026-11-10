using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary>
/// Grok Build's billing config (QUOTAS 9.2), from its log line <c>billing: fetched credits config</c> or the live
/// <c>v1/billing?format=credits</c> response: one window, weekly or monthly.
/// </summary>
public static class GrokBilling
{
    public const string LogMessage = "billing: fetched credits config";

    /// <summary>The newest config among the log's lines, with its time; null when no line has one.</summary>
    public static (long ObservedMs, IReadOnlyList<QuotaWindow> Windows)? FromLog(IEnumerable<ReadOnlyMemory<byte>> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        (long Ts, IReadOnlyList<QuotaWindow> Windows)? best = null;
        foreach (var line in lines)
        {
            if (line.Length >= 1 << 20 || line.Span.IndexOf(LogMessageUtf8) < 0)
            {
                continue;
            }

            using var document = VendorValue.ParseObject(line);
            if (document is null)
            {
                continue;
            }

            var root = document.RootElement;
            if (VendorValue.String(VendorValue.Get(root, "msg")) != LogMessage
                || VendorValue.Object(VendorValue.Object(root, "ctx"), "config") is not { } config
                || VendorValue.TimeMs(VendorValue.Get(root, "ts")) is not { } ts)
            {
                continue;
            }

            if (best is null || ts > best.Value.Ts)
            {
                best = (ts, Windows(config));
            }
        }

        return best is { } found ? (found.Ts, found.Windows) : null;
    }

    /// <summary>The live response's <c>config</c>; null when the body has none.</summary>
    public static IReadOnlyList<QuotaWindow>? FromResponse(JsonElement body) =>
        VendorValue.Object(body, "config") is { } config ? Windows(config) : null;

    /// <summary>
    /// <c>currentPeriod.type</c> containing <c>MONTH</c> is a month, else a week; <c>creditUsagePercent</c>, or 0 for a
    /// unified billing user with both period ends (protobuf drops zeros); the reset is the period's end, else
    /// <c>billingPeriodEnd</c>.
    /// </summary>
    public static IReadOnlyList<QuotaWindow> Windows(JsonElement config)
    {
        var period = VendorValue.Object(config, "currentPeriod");
        var kind = VendorValue.Text(VendorValue.Get(period, "type")).ToUpperInvariant().Contains("MONTH", StringComparison.Ordinal)
            ? QuotaWindowKind.Month
            : QuotaWindowKind.SevenDay;
        var used = VendorValue.Number(VendorValue.Get(config, "creditUsagePercent"));
        if (used is null)
        {
            if (!VendorValue.IsTrue(VendorValue.Get(config, "isUnifiedBillingUser")) || !Truthy(VendorValue.Get(period, "start"))
                || !Truthy(VendorValue.Get(period, "end")))
            {
                return [];
            }

            used = 0;
        }

        var end = VendorValue.TimeMs(VendorValue.Get(period, "end"));
        var resets = end is { } e && e != 0 ? e : VendorValue.TimeMs(VendorValue.Get(config, "billingPeriodEnd"));
        int? minutes = null;
        if (kind == QuotaWindowKind.Month && VendorValue.TimeMs(VendorValue.Get(period, "start")) is { } start && end is { } stop)
        {
            minutes = (int)Math.Clamp(Math.Floor((stop - start) / 60_000.0), int.MinValue, int.MaxValue);
        }

        return [QuotaWindow.Of(kind, used.Value, resets, minutes: minutes)];
    }

    private static ReadOnlySpan<byte> LogMessageUtf8 => "billing: fetched credits config"u8;

    /// <summary>Python truthiness of a period end: a non-empty string, a non-zero number, a non-empty container.</summary>
    private static bool Truthy(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString() is { Length: > 0 },
        { ValueKind: JsonValueKind.Number } number => number.TryGetDouble(out var d) && d != 0,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.Array } array => array.GetArrayLength() > 0,
        { ValueKind: JsonValueKind.Object } obj => obj.EnumerateObject().Any(),
        _ => false,
    };
}
