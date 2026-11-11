using System.Globalization;
using AgentSpeed.Core.History;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>The wire names of windows and sources, and a window's identity <c>kind:name:minutes</c> (QUOTAS 10.2).</summary>
public static class EstimateNames
{
    public static string Kind(QuotaWindowKind kind) => kind switch
    {
        QuotaWindowKind.FiveHour => "fiveHour",
        QuotaWindowKind.SevenDay => "sevenDay",
        QuotaWindowKind.ModelWeek => "modelWeek",
        QuotaWindowKind.Other => "other",
        QuotaWindowKind.Month => "month",
        _ => "credits",
    };

    public static QuotaWindowKind? ParseKind(string? name) => name switch
    {
        "fiveHour" => QuotaWindowKind.FiveHour,
        "sevenDay" => QuotaWindowKind.SevenDay,
        "modelWeek" => QuotaWindowKind.ModelWeek,
        "other" => QuotaWindowKind.Other,
        "month" => QuotaWindowKind.Month,
        "credits" => QuotaWindowKind.Credits,
        _ => null,
    };

    /// <summary>QUOTAS 2's kind order for windows of equal length.</summary>
    public static int KindRank(QuotaWindowKind kind) => kind switch
    {
        QuotaWindowKind.FiveHour => 0,
        QuotaWindowKind.Other => 1,
        QuotaWindowKind.SevenDay => 2,
        QuotaWindowKind.ModelWeek => 3,
        QuotaWindowKind.Month => 4,
        _ => 5,
    };

    public static string WindowId(QuotaWindowKind kind, string? name, int? minutes) =>
        Kind(kind) + ":" + (name ?? "") + ":" + (minutes is { } m ? m.ToString(CultureInfo.InvariantCulture) : "None");

    public static string WindowId(QuotaWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return WindowId(window.Kind, window.Name, window.Minutes);
    }

    /// <summary>The minutes of a window id, null when unknown.</summary>
    public static int? MinutesOf(string windowId)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        var colon = windowId.LastIndexOf(':');
        return int.TryParse(windowId.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? minutes : null;
    }

    public static string Source(SourceKind source) => SourceNames.Of(source);

    public static SourceKind? ParseSource(string? name) => SourceNames.Parse(name);

    /// <summary>The vendor a source's plan belongs to (cap classes of QUOTAS 10.2).</summary>
    public static string Vendor(SourceKind source) => source switch
    {
        SourceKind.Codex => "openai",
        SourceKind.ClaudeCode or SourceKind.ClaudeDesktop => "anthropic",
        _ => Source(source),
    };
}
