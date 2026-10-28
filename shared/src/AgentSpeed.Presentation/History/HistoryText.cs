using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.History;

/// <summary>User-visible text of the Usage History window, in the selected language.</summary>
public static class HistoryText
{
    private const string Minus = "−";
    private const string RangeDash = " – ";

    /// <summary><c>30 天</c> / <c>1 day</c></summary>
    public static string Days(int count, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format(count == 1 ? "win.history.days.one" : "win.history.days", count);
    }

    /// <summary>
    /// <c>2026/9/22 – 10/21 · 30 天</c>; the year is written once when both ends share it. A single day is spelled out
    /// with its weekday: <c>今天 · 2026/10/21（週三）</c>, <c>2026/10/14（週三）</c>.
    /// </summary>
    public static string RangeSubtitle(DayRange range, DateOnly today, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (range.IsSingleDay)
        {
            var day = range.Start;
            var date = LongDate(day, loc);
            return day == today ? $"{loc["history.today"]} · {date}"
                : day == today.AddDays(-1) ? $"{loc["history.yesterday"]} · {date}"
                : date;
        }

        string dates;
        if (range.Start.Year == range.End.Year)
        {
            dates = Pattern(range.Start, "win.history.format.rangeStart", loc) + RangeDash + Pattern(range.End, "win.history.format.rangeEnd", loc);
        }
        else
        {
            dates = Date(range.Start, loc) + RangeDash + Date(range.End, loc);
        }

        return $"{dates} · {Days(range.DayCount, loc)}";
    }

    /// <summary>Full date: <c>2026/10/21</c>, <c>Oct 21, 2026</c>.</summary>
    public static string Date(DateOnly day, Localizer loc) => Pattern(day, "win.history.format.date", loc);

    /// <summary>Date without the year: <c>10/21</c>, <c>Oct 21</c>.</summary>
    public static string MonthDay(DateOnly day, Localizer loc) => Pattern(day, "win.history.format.monthDay", loc);

    /// <summary>The month-day form inside the current year, the full date otherwise.</summary>
    public static string ShortDate(DateOnly day, DateOnly today, Localizer loc) =>
        day.Year == today.Year ? MonthDay(day, loc) : Date(day, loc);

    /// <summary>Abbreviated weekday in the selected language: <c>週二</c>, <c>Tue</c>, <c>火</c>.</summary>
    public static string Weekday(DateOnly day, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Culture.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek);
    }

    /// <summary>The table's date cell: <c>今天</c>, <c>昨天</c>, else <c>10/19（週日）</c>.</summary>
    public static string TableDate(DateOnly day, DateOnly today, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (day == today)
        {
            return loc["history.today"];
        }

        if (day == today.AddDays(-1))
        {
            return loc["history.yesterday"];
        }

        return loc.Format("history.weekdayDate", ShortDate(day, today, loc), Weekday(day, loc));
    }

    /// <summary>The tooltip title: <c>2026/10/21（週二）</c>.</summary>
    public static string LongDate(DateOnly day, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("history.weekdayDate", Date(day, loc), Weekday(day, loc));
    }

    /// <summary>A month rule's label: <c>10月</c> / <c>Oct</c>, with the year at year boundaries.</summary>
    public static string Month(DateOnly day, bool withYear, Localizer loc) =>
        Pattern(day, withYear ? "win.history.format.yearMonth" : "win.history.format.month", loc);

    /// <summary>Exact count with grouping separators, for tooltips: <c>1,234,567</c>.</summary>
    public static string FullNumber(long value, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return value.ToString("N0", loc.Culture);
    }

    /// <summary>Compact speed with its unit, or <c>history.noSpeed</c>.</summary>
    public static string Speed(double? speed, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return speed is { } value && double.IsFinite(value) && value > 0
            ? $"{SpeedFormat.Speed(value)} {loc["unit.tokS"]}"
            : loc["history.noSpeed"];
    }

    /// <summary>A share as a whole percentage (<c>92%</c>); below 1 % but above 0 it reads <c>&lt;1%</c>.</summary>
    public static string Percent(double? ratio)
    {
        if (ratio is not { } r || !double.IsFinite(r))
        {
            return "—";
        }

        r = Math.Clamp(r, 0, 1);
        if (r > 0 && r < 0.005)
        {
            return "<1%";
        }

        return Math.Round(r * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>A signed change: <c>+12%</c>, <c>−8%</c>, <c>+0.4%</c>, <c>0%</c>.</summary>
    public static string Change(double ratio)
    {
        if (!double.IsFinite(ratio))
        {
            return "—";
        }

        var percent = ratio * 100;
        var magnitude = Math.Abs(percent);
        var text = magnitude < 0.05
            ? "0"
            : magnitude < 1
                ? Math.Round(magnitude, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture)
                : Math.Round(magnitude, MidpointRounding.AwayFromZero).ToString("#,0", CultureInfo.InvariantCulture);
        if (text == "0")
        {
            return "0%";
        }

        return (percent > 0 ? "+" : Minus) + text + "%";
    }

    /// <summary><c>比前一段期間 +12%</c></summary>
    public static string VersusPrevious(double ratio, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("history.summary.vsPrevious", Change(ratio));
    }

    /// <summary><c>比前一天 +12%</c></summary>
    public static string VersusPreviousDay(double ratio, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("history.summary.vsPreviousDay", Change(ratio));
    }

    /// <summary><c>活躍 18 天</c></summary>
    public static string ActiveDays(int days, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("history.summary.activeDays", days);
    }

    /// <summary><c>單日最高 10/3</c></summary>
    public static string BusiestDay(DateOnly day, DateOnly today, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("history.summary.bestDay", ShortDate(day, today, loc));
    }

    /// <summary>Up to <paramref name="max"/> model names, the rest as <c>+n</c>: <c>gpt-6, claude-opus +2</c>.</summary>
    public static string TopModels(IReadOnlyList<ModelUsage> models, Localizer loc, int max = 2)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(loc);
        if (models.Count == 0)
        {
            return "—";
        }

        var shown = string.Join(", ", models.Take(Math.Max(1, max)).Select(m => m.Model));
        var rest = models.Count - Math.Max(1, max);
        return rest > 0 ? $"{shown} {loc.Format("win.history.models.more", rest)}" : shown;
    }

    /// <summary><c>更新於 剛剛</c>, <c>更新於 5 分鐘前</c>, then the time of day, then the date and time.</summary>
    public static string Updated(DateTimeOffset at, DateTimeOffset now, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var elapsed = now - at;
        string when;
        if (elapsed < TimeSpan.FromHours(1))
        {
            when = DisplayText.TimeAgo(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed, loc);
        }
        else
        {
            var local = at.ToLocalTime();
            var time = local.ToString("t", loc.Culture);
            when = local.Date == now.ToLocalTime().Date ? time : $"{Date(DateOnly.FromDateTime(local.DateTime), loc)} {time}";
        }

        return loc.Format("history.updated", when);
    }

    /// <summary><c>正在分析歷史紀錄… 42%</c></summary>
    public static string Analyzing(double progress, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var percent = double.IsFinite(progress) ? (int)Math.Floor(Math.Clamp(progress, 0, 1) * 100) : 0;
        return loc.Format("history.analyzing", percent);
    }

    /// <summary><c>12 / 340 個檔案</c></summary>
    public static string AnalyzingFiles(int done, int total, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("history.analyzingFiles", Math.Max(0, done), Math.Max(0, total));
    }

    /// <summary>One labelled value for screen readers: <c>輸出：1.2M</c>.</summary>
    public static string Labelled(string label, string value, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("win.history.a11y.item", label, value);
    }

    public static string JoinParts(IEnumerable<string> parts, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(loc);
        return string.Join(loc["win.history.a11y.join"], parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>Everything a day holds, as one sentence for screen readers; with <paramref name="money"/>, its cost too.</summary>
    public static string Accessible(DaySummary day, DateOnly today, Localizer loc, HistoryMoney? money = null)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(loc);
        var title = day.Day == today ? $"{LongDate(day.Day, loc)} {loc["history.inProgress"]}" : LongDate(day.Day, loc);
        var parts = new List<string>
        {
            Labelled(loc["history.table.output"], SpeedFormat.Tokens(day.Output), loc),
            Labelled(loc["history.table.input"], SpeedFormat.Tokens(day.Input), loc),
            money is null ? string.Empty : Labelled(loc["history.table.cost"], SpokenCost(day.Cost, money, loc), loc),
            Labelled(loc["history.table.requests"], FullNumber(day.Requests, loc), loc),
            Labelled(loc["history.table.cacheHit"], Percent(day.CacheHitRatio), loc),
            Labelled(loc["history.table.speed"], Speed(day.AverageSpeed, loc), loc),
        };
        if (day.Peak is { } peak)
        {
            parts.Add(Labelled(loc["history.table.peak"], Speed(peak, loc), loc));
        }

        parts.Add(Labelled(loc["history.table.agents"], FullNumber(day.Agents, loc), loc));
        if (day.Models.Count > 0)
        {
            var models = day.Models.Take(3).Select(m => ModelWithSpeed(m, loc)).ToList();
            if (day.Models.Count > 3)
            {
                models.Add(loc.Format("win.history.models.more", day.Models.Count - 3));
            }

            parts.Add(Labelled(loc["history.table.models"], string.Join(", ", models), loc));
        }

        return Labelled(title, JoinParts(parts, loc), loc);
    }

    /// <summary>A model with its average speed when it was measured: <c>claude-opus-5-5 · 83 tok/s</c>.</summary>
    public static string ModelWithSpeed(ModelUsage model, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(loc);
        return model.AverageSpeed is { } speed && double.IsFinite(speed) && speed > 0 ? $"{model.Model} · {Speed(speed, loc)}" : model.Model;
    }

    /// <summary>
    /// The values of one breakdown bar: <c>輸出：312,345，費用：4.20 美元，佔比：38%，平均速度：88.2 tok/s，請求：151</c> (the cost
    /// only with <paramref name="money"/>).
    /// </summary>
    public static string BreakdownValue(BreakdownRow row, Localizer loc, HistoryMoney? money = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(loc);
        return JoinParts(
        [
            Labelled(loc["history.table.output"], FullNumber(row.Output, loc), loc),
            money is null ? string.Empty : Labelled(loc["history.table.cost"], SpokenCost(row.Cost, money, loc), loc),
            row.Traffic is { } traffic ? Labelled(loc["history.table.traffic"], HistoryTrafficText.Spoken(traffic, loc), loc) : string.Empty,
            Labelled(loc["win.history.share"], Percent(row.Share), loc),
            Labelled(loc["history.table.speed"], Speed(row.Speed, loc), loc),
            Labelled(loc["history.table.requests"], FullNumber(row.Requests, loc), loc),
        ], loc);
    }

    /// <summary>The name of a breakdown bar: the source, the model, or <c>其他模型</c>.</summary>
    public static string BreakdownName(BreakdownRow row, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(loc);
        return row.OtherModels ? loc["win.history.breakdown.otherModels"] : row.Model ?? loc[DisplayText.SourceKey(row.Source)];
    }

    /// <summary>One breakdown bar for screen readers: its name, then its values.</summary>
    public static string Accessible(BreakdownRow row, Localizer loc, HistoryMoney? money = null) =>
        Labelled(BreakdownName(row, loc), BreakdownValue(row, loc, money), loc);

    /// <summary>Source names in source order, joined with <c>list.separator</c>.</summary>
    public static string SourceList(IEnumerable<SourceKind> sources, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(loc);
        return string.Join(loc["list.separator"], sources.Select(s => loc[DisplayText.SourceKey(s)]));
    }

    /// <summary>The line under a cost: unpriced requests, no cost data, or the excluded subscription sources; null when none.</summary>
    public static string? CostNote(HistoryCost cost, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return cost.Kind switch
        {
            HistoryCostKind.Priced => cost.IsPartial ? loc.Format("history.unpricedDay", cost.Unpriced) : null,
            HistoryCostKind.Unknown => loc.Format("history.unpricedDay", cost.Requests),
            HistoryCostKind.Unavailable => loc["history.costUnavailable"],
            _ => loc.Format("cost.subscriptionExcluded", SourceList(cost.Excluded, loc)),
        };
    }

    /// <summary>
    /// A model's cost next to its share: the amount (<c>≥</c> when partial), <c>cost.free</c>, <c>cost.unknown</c>, or
    /// <c>cost.subscription.tag</c> when only subscription sources used it.
    /// </summary>
    public static string ModelCostText(ModelCost? cost, HistoryMoney money, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(money);
        ArgumentNullException.ThrowIfNull(loc);
        if (cost is not { Requests: > 0 } counted)
        {
            return loc["cost.subscription.tag"];
        }

        if (counted.Cost is not { } tally || tally.IsUnknown(counted.Requests))
        {
            return loc["cost.unknown"];
        }

        if (tally.CostMicroUsd == 0 && tally.UnpricedRequests == 0)
        {
            return loc["cost.free"];
        }

        return (tally.UnpricedRequests > 0 ? CostDisplay.AtLeastSign : string.Empty) + money.Amount(tally.CostMicroUsd);
    }

    /// <summary>
    /// How accessibility reads a cost: the amount with the currency's name, <c>≥</c> as <c>a11y.orb.costAtLeast</c>, <c>–</c>
    /// as <c>cost.unknown</c>, a subscription as <c>cost.subscription.tag</c> (DESIGN 9).
    /// </summary>
    public static string SpokenCost(HistoryCost cost, HistoryMoney money, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(money);
        ArgumentNullException.ThrowIfNull(loc);
        return cost.Kind switch
        {
            HistoryCostKind.Priced when cost.IsPartial => loc.Format("a11y.orb.costAtLeast", money.Spoken(cost.MicroUsd)),
            HistoryCostKind.Priced => money.Spoken(cost.MicroUsd),
            HistoryCostKind.Subscription => loc["cost.subscription.tag"],
            _ => loc["cost.unknown"],
        };
    }

    private static string Pattern(DateOnly day, string key, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var pattern = loc[key];
        try
        {
            return day.ToString(pattern, loc.Culture);
        }
        catch (FormatException)
        {
            return day.ToString("d", loc.Culture);
        }
    }
}
