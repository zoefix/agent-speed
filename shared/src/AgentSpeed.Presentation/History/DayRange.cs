using System.Globalization;

namespace AgentSpeed.Presentation.History;

/// <summary>The range presets of the Usage History window (DESIGN 4b, toolbar).</summary>
public enum HistoryRangeKind
{
    Today,
    Yesterday,
    Days7,
    Days30,
    Days90,
    Year,
    All,
    Custom,
}

/// <summary>An inclusive range of local calendar days.</summary>
public readonly record struct DayRange
{
    public DayRange(DateOnly start, DateOnly end)
    {
        Start = start <= end ? start : end;
        End = start <= end ? end : start;
    }

    public DateOnly Start { get; }

    public DateOnly End { get; }

    public int DayCount => End.DayNumber - Start.DayNumber + 1;

    public bool Contains(DateOnly day) => day >= Start && day <= End;

    /// <summary>Position of <paramref name="day"/> in the range (0 = <see cref="Start"/>).</summary>
    public int IndexOf(DateOnly day) => day.DayNumber - Start.DayNumber;

    public DateOnly DayAt(int index) => Start.AddDays(index);

    /// <summary>One calendar day: the window shows that day's breakdown instead of the daily chart.</summary>
    public bool IsSingleDay => Start == End;

    public static DayRange Single(DateOnly day) => new(day, day);
}

public static class HistoryRanges
{
    /// <summary>Days a single day's sparklines cover, ending on that day.</summary>
    public const int SingleDaySparkDays = 7;

    public static IReadOnlyList<HistoryRangeKind> Presets { get; } =
    [
        HistoryRangeKind.Today, HistoryRangeKind.Yesterday, HistoryRangeKind.Days7, HistoryRangeKind.Days30, HistoryRangeKind.Days90,
        HistoryRangeKind.Year, HistoryRangeKind.All,
    ];

    public static string LabelKey(HistoryRangeKind kind) => kind switch
    {
        HistoryRangeKind.Today => "history.range.today",
        HistoryRangeKind.Yesterday => "history.range.yesterday",
        HistoryRangeKind.Days7 => "history.range.7d",
        HistoryRangeKind.Days30 => "history.range.30d",
        HistoryRangeKind.Days90 => "history.range.90d",
        HistoryRangeKind.Year => "history.range.year",
        HistoryRangeKind.All => "history.range.all",
        _ => "history.range.custom",
    };

    /// <summary>
    /// The days a preset covers, ending today (<see cref="HistoryRangeKind.Yesterday"/> is the single day before).
    /// <see cref="HistoryRangeKind.All"/> starts at the first day with data (today when there is none);
    /// <see cref="HistoryRangeKind.Custom"/> uses <paramref name="custom"/>, or the last 30 days when none was chosen.
    /// </summary>
    public static DayRange Resolve(HistoryRangeKind kind, DateOnly today, DateOnly? firstDataDay, DayRange? custom = null) => kind switch
    {
        HistoryRangeKind.Today => DayRange.Single(today),
        HistoryRangeKind.Yesterday => DayRange.Single(today.AddDays(-1)),
        HistoryRangeKind.Days7 => Last(today, 7),
        HistoryRangeKind.Days90 => Last(today, 90),
        HistoryRangeKind.Year => new DayRange(new DateOnly(today.Year, 1, 1), today),
        HistoryRangeKind.All => new DayRange(firstDataDay is { } first && first < today ? first : today, today),
        HistoryRangeKind.Custom when custom is { } range => range,
        _ => Last(today, 30),
    };

    /// <summary>The equally long period that ends the day before <paramref name="range"/> starts.</summary>
    public static DayRange Previous(DayRange range)
    {
        var end = range.Start.AddDays(-1);
        return new DayRange(end.AddDays(-(range.DayCount - 1)), end);
    }

    /// <summary>The range itself, or up to 7 days ending on a single day, bounded by the start of the calendar.</summary>
    public static DayRange SparkRange(DayRange range) =>
        range.IsSingleDay ? Last(range.Start, SingleDaySparkDays) : range;

    /// <summary>A custom range clamped to the days with data, with the end never before the start.</summary>
    public static DayRange Clamp(DateOnly from, DateOnly to, DateOnly minimum, DateOnly maximum)
    {
        if (maximum < minimum)
        {
            (minimum, maximum) = (maximum, minimum);
        }

        var start = Bound(from, minimum, maximum);
        var end = Bound(to, minimum, maximum);
        return new DayRange(start, end < start ? start : end);
    }

    /// <summary>Parses a stored <c>YYYY-MM-DD</c> day.</summary>
    public static bool TryParseDay(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    public static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DayRange Last(DateOnly today, int days) =>
        new(DateOnly.FromDayNumber(Math.Max(0, today.DayNumber - (days - 1))), today);

    private static DateOnly Bound(DateOnly day, DateOnly minimum, DateOnly maximum) =>
        day < minimum ? minimum : day > maximum ? maximum : day;
}
