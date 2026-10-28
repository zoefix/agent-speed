using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.History;

/// <summary>Traffic wording of the Usage History window (DESIGN 10, Usage history).</summary>
public static class HistoryTrafficText
{
    /// <summary><c>↓ 48.2 MB · ↑ 3.10 MB</c>.</summary>
    public static string Pair(TrafficBytes bytes, Localizer loc) => new TrafficText(loc, bits: false).Pair(bytes);

    /// <summary>
    /// The pair as read aloud, without arrows and without a day, which its context names: <c>下載 48.2 MB，上傳 3.10 MB</c>
    /// (<c>traffic.a11y.pair</c>).
    /// </summary>
    public static string Spoken(TrafficBytes bytes, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("traffic.a11y.pair", TrafficFormat.Total(bytes.Rx), TrafficFormat.Total(bytes.Tx));
    }

    /// <summary>A byte axis value without trailing zeros: <c>500 MB</c>, <c>1 GB</c>, <c>2.5 GB</c> (METRICS 10.11 units).</summary>
    public static string AxisBytes(double value)
    {
        var parts = TrafficFormat.TotalParts(Math.Abs(value));
        if (!parts.Number.Contains('.', StringComparison.Ordinal))
        {
            return parts.Text;
        }

        var digits = parts.Number.TrimEnd('0').TrimEnd('.');
        return digits + TrafficFormat.NoBreakSpace + parts.Unit;
    }

    /// <summary>How long a partial day was recorded: <c>3 小時 20 分鐘</c>, <c>45 min</c> (at least a minute).</summary>
    public static string Duration(long ms, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var minutes = Math.Max(ms, 60_000) / 60_000;
        if (minutes < 60)
        {
            return loc.Format("time.minutes", minutes);
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? loc.Format("time.hours", hours) : loc.Format("time.hoursMinutes", hours, rest);
    }

    /// <summary>The traffic cell: the pair, or <c>–</c> before measuring began.</summary>
    public static string Cell(DayTraffic? traffic, Localizer loc) => traffic is null ? "–" : Pair(traffic.Shown, loc);

    /// <summary>The traffic cell read aloud: the spoken pair, or <c>history.traffic.notRecorded</c>.</summary>
    public static string AccessibleCell(DayTraffic? traffic, Localizer loc) =>
        traffic is null ? loc["history.traffic.notRecorded"] : Spoken(traffic.Shown, loc);

    /// <summary>
    /// The traffic cell's tooltip: not recorded before measuring began; else the tools' part when tools are included and
    /// the recorded time on a partial day; null when there is nothing to add.
    /// </summary>
    public static string? Tooltip(DayTraffic? traffic, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (traffic is null)
        {
            return loc["history.traffic.notRecorded"];
        }

        var lines = Notes(traffic, loc, machine: false);
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The notes under a day's traffic: <c>history.traffic.tools</c> when tools are included, <c>history.traffic.machine</c>
    /// with every source, and <c>history.traffic.partial</c> on a partial day.
    /// </summary>
    public static IReadOnlyList<string> Notes(DayTraffic traffic, Localizer loc, bool machine = true)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(loc);
        var lines = new List<string>(3);
        if (traffic.IncludeTools && !traffic.Tools.IsZero)
        {
            lines.Add(loc.Format("history.traffic.tools", Pair(traffic.Tools, loc)));
        }

        if (machine && traffic.Machine is { } computer)
        {
            lines.Add(loc.Format("history.traffic.machine", Pair(computer, loc)));
        }

        if (traffic.Partial)
        {
            lines.Add(loc.Format("history.traffic.partial", Duration(traffic.RecordedMs, loc)));
        }

        return lines;
    }
}
