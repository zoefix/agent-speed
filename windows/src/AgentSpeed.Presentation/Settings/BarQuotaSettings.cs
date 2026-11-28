using AgentSpeed.Core;
using AgentSpeed.Core.Quota.Estimate;

namespace AgentSpeed.Presentation.Settings;

/// <summary><c>settings.bar.quota.percent</c> (DESIGN 10, Menu bar and taskbar, <i>Quota</i>): the block's percents used or remaining.</summary>
public enum BarQuotaPercent
{
    Used,
    Remaining,
}

/// <summary>
/// The stored values of the taskbar quota block (DESIGN 10, Settings, <i>Storage</i>): <c>taskbar.quota</c> is <c>off</c> or a
/// quota source by its source key, <c>taskbar.quotaPercent</c> is <c>used</c> or <c>remaining</c>; an unknown value reads as
/// the default.
/// </summary>
public static class BarQuotaSettings
{
    public const string OffKey = "off";

    /// <summary>A quota source of this computer (<see cref="QuotaSettings.Sources"/>); anything else reads as off.</summary>
    public static SourceKind? Valid(SourceKind? source) => source is { } s && QuotaSettings.Sources.Contains(s) ? s : null;

    public static string Key(SourceKind? source) => Valid(source) is { } s ? EstimateNames.Source(s) : OffKey;

    public static SourceKind? Parse(string? key) => Valid(EstimateNames.ParseSource(key));

    public static string PercentKey(BarQuotaPercent percent) => percent == BarQuotaPercent.Remaining ? "remaining" : "used";

    public static BarQuotaPercent ParsePercent(string? key) => key == "remaining" ? BarQuotaPercent.Remaining : BarQuotaPercent.Used;
}
