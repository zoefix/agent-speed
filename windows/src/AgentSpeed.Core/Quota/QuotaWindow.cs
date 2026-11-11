namespace AgentSpeed.Core.Quota;

/// <summary>The kinds of limit window (QUOTAS 2, 9.1).</summary>
public enum QuotaWindowKind
{
    FiveHour,
    SevenDay,

    /// <summary>A weekly window of one model or model family; the same as <see cref="SevenDay"/> with a name.</summary>
    ModelWeek,

    /// <summary>Any other length, <see cref="QuotaWindow.Minutes"/> known.</summary>
    Other,

    /// <summary>A monthly or billing-cycle window.</summary>
    Month,

    /// <summary>A credit pool with no stated length (no minutes, no pace).</summary>
    Credits,
}

/// <summary>One limit window of an account (QUOTAS 2).</summary>
/// <param name="Name">A model group (<c>Fable</c>, <c>Gemini</c>); null for the account's own windows.</param>
/// <param name="Minutes">The window's length; null for credits.</param>
/// <param name="ResetsAtMs">When the window starts over; null while unknown.</param>
public sealed record QuotaWindow(QuotaWindowKind Kind, string? Name, int? Minutes, double UsedPercent, long? ResetsAtMs)
{
    public const int FiveHourMinutes = 300;
    public const int WeekMinutes = 10080;

    /// <summary>A month without known ends counts 30 days (QUOTAS 9.1).</summary>
    public const int MonthMinutes = 43200;

    /// <summary>A window with the kind's own length (300 for 5 h, 10080 for the weekly kinds, 43200 for a month) unless one is given.</summary>
    public static QuotaWindow Of(QuotaWindowKind kind, double usedPercent, long? resetsAtMs, string? name = null, int? minutes = null) =>
        new(kind, name, minutes ?? kind switch
        {
            QuotaWindowKind.FiveHour => FiveHourMinutes,
            QuotaWindowKind.SevenDay or QuotaWindowKind.ModelWeek => WeekMinutes,
            QuotaWindowKind.Month => MonthMinutes,
            _ => null,
        }, usedPercent, resetsAtMs);
}

/// <summary>The quota of one source: when the vendor computed it and the windows its account has (QUOTAS 2).</summary>
public sealed record QuotaSnapshot(SourceKind Source, long ObservedMs, IReadOnlyList<QuotaWindow> Windows)
{
    public bool HasWindows => Windows.Count > 0;

    public bool Equals(QuotaSnapshot? other) =>
        other is not null && Source == other.Source && ObservedMs == other.ObservedMs && Windows.SequenceEqual(other.Windows);

    public override int GetHashCode() => HashCode.Combine(Source, ObservedMs, Windows.Count);
}

/// <summary>A window as drawn (QUOTAS 2): its label, the rounded percent, the bar's share and the reset, all reset-aware.</summary>
/// <param name="Bar">The bar's length, 0…100.</param>
/// <param name="ResetsAtMs">Null while unknown or once passed.</param>
/// <param name="Passed">The reset time has passed: the window shows 0 % until a newer snapshot arrives.</param>
public sealed record QuotaShownWindow(QuotaWindow Window, string Label, int Percent, double Bar, long? ResetsAtMs, bool Passed);
