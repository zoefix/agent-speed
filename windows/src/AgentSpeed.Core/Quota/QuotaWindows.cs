using System.Globalization;

namespace AgentSpeed.Core.Quota;

/// <summary>The language-dependent base labels (<c>quota.window.month</c>, <c>quota.window.credits</c>).</summary>
public sealed record QuotaLabels(string Month, string Credits)
{
    public static QuotaLabels English { get; } = new("mo", "credits");
}

/// <summary>Order, labels, percents, reset-passed and pace of windows (QUOTAS 2, 7).</summary>
public static class QuotaWindows
{
    /// <summary>The base label of a window: <c>5h</c>, <c>7d</c>, the month or credits word, else <c>&lt;n&gt;d</c>, <c>&lt;n&gt;h</c>, <c>&lt;n&gt;m</c>.</summary>
    public static string BaseLabel(QuotaWindow window, QuotaLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        labels ??= QuotaLabels.English;
        switch (window.Kind)
        {
            case QuotaWindowKind.FiveHour:
                return "5h";
            case QuotaWindowKind.SevenDay or QuotaWindowKind.ModelWeek:
                return "7d";
            case QuotaWindowKind.Month:
                return labels.Month;
            case QuotaWindowKind.Credits:
                return labels.Credits;
        }

        var minutes = window.Minutes ?? 0;
        return minutes % 1440 == 0 ? Whole(minutes / 1440, "d")
            : minutes % 60 == 0 ? Whole(minutes / 60, "h")
            : Whole(minutes, "m");
    }

    /// <summary><c>&lt;name&gt; &lt;base&gt;</c> for a named window (<c>Fable 7d</c>), else the base label.</summary>
    public static string Label(QuotaWindow window, QuotaLabels? labels = null)
    {
        var label = BaseLabel(window, labels);
        return string.IsNullOrEmpty(window.Name) ? label : window.Name + " " + label;
    }

    /// <summary>
    /// Unnamed windows by length (equal lengths in kind order; credits and unknown lengths last), then the named ones
    /// grouped by name in the vendor's order, each group by length the same way.
    /// </summary>
    public static IReadOnlyList<QuotaWindow> Ordered(IEnumerable<QuotaWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var all = windows as IReadOnlyList<QuotaWindow> ?? [.. windows];
        var result = new List<QuotaWindow>(all.Count);
        result.AddRange(all.Where(w => string.IsNullOrEmpty(w.Name)).OrderBy(Length));
        var names = new List<string>();
        foreach (var window in all)
        {
            if (!string.IsNullOrEmpty(window.Name) && !names.Contains(window.Name, StringComparer.Ordinal))
            {
                names.Add(window.Name);
            }
        }

        foreach (var name in names)
        {
            result.AddRange(all.Where(w => string.Equals(w.Name, name, StringComparison.Ordinal)).OrderBy(Length));
        }

        return result;
    }

    /// <summary>The used percent as text shows it: rounded half up, clamped to 0…100.</summary>
    public static int Percent(double used) => (int)Math.Clamp(Math.Floor(used + 0.5), 0, 100);

    /// <summary>The ordered windows as drawn at <paramref name="nowMs"/>: a passed reset shows 0 % and no reset time.</summary>
    public static IReadOnlyList<QuotaShownWindow> Shown(QuotaSnapshot snapshot, long nowMs, QuotaLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ordered = Ordered(snapshot.Windows);
        var shown = new QuotaShownWindow[ordered.Count];
        for (var i = 0; i < shown.Length; i++)
        {
            var w = ordered[i];
            var passed = w.ResetsAtMs is { } reset && nowMs >= reset;
            shown[i] = new QuotaShownWindow(w, Label(w, labels), passed ? 0 : Percent(w.UsedPercent),
                passed ? 0 : Math.Clamp(w.UsedPercent, 0, 100), passed ? null : w.ResetsAtMs, passed);
        }

        return shown;
    }

    /// <summary>The share of the window that has passed (QUOTAS 7), 0…1; null without a known, future reset or a length.</summary>
    public static double? Pace(QuotaWindow window, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.ResetsAtMs is not { } reset || nowMs >= reset || window.Minutes is not > 0 || window.Kind == QuotaWindowKind.Credits)
        {
            return null;
        }

        return Math.Clamp(1 - (reset - nowMs) / (window.Minutes.Value * 60_000.0), 0, 1);
    }

    private static (double Minutes, int Rank) Length(QuotaWindow w) =>
        (w.Minutes is not { } minutes || w.Kind == QuotaWindowKind.Credits ? double.PositiveInfinity : minutes, Rank(w.Kind));

    private static int Rank(QuotaWindowKind kind) => kind switch
    {
        QuotaWindowKind.FiveHour => 0,
        QuotaWindowKind.Other => 1,
        QuotaWindowKind.SevenDay => 2,
        QuotaWindowKind.ModelWeek => 3,
        QuotaWindowKind.Month => 4,
        _ => 5,
    };

    private static string Whole(int value, string unit) => value.ToString(CultureInfo.InvariantCulture) + unit;
}
