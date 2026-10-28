using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Formatting;

public static class ExecutionModeText
{
    public static string Aggregate(IEnumerable<ExecutionModes> modes, Localizer loc)
    {
        var summaries = modes.Select(m => Summary(m, loc)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return summaries.Length switch { 0 => loc["mode.unknown"], 1 => summaries[0], _ => loc["mode.mixed"] };
    }

    public static string Value(string value, Localizer loc)
    {
        var key = "mode.value." + value;
        return loc[key] == key ? value : loc[key];
    }

    public static string Summary(ExecutionModes modes, Localizer loc)
    {
        var facts = modes.Facts.GroupBy(f => (f.Kind, f.Provider)).Select(g => g
            .OrderByDescending(f => f.Evidence).ThenByDescending(f => f.Scope).First()).ToArray();
        return facts.Length == 0 ? loc["mode.unknown"] : string.Join(" · ", facts.Select(f =>
            $"{loc["mode.kind." + ExecutionModes.Name(f.Kind)]}"
            + (facts.Count(other => other.Kind == f.Kind) > 1 && f.Provider is { } provider ? " · " + provider : string.Empty)
            + $": {Value(f.Value, loc)} ({loc["mode.evidence." + ExecutionModes.Name(f.Evidence)]})"));
    }

    public static string Detail(ExecutionModes modes, Localizer loc)
    {
        if (modes.IsEmpty) return loc["mode.label"] + ": " + loc["mode.unknown"];
        var lines = modes.Facts.OrderBy(f => f.Kind).ThenBy(f => f.Evidence).Select(f =>
            $"{loc["mode.kind." + ExecutionModes.Name(f.Kind)]} · {loc["mode.evidence." + ExecutionModes.Name(f.Evidence)]}: "
            + Value(f.Value, loc) + (f.Provider is { } provider ? " · " + provider : string.Empty)
            + (f.Value != f.RawValue ? " (" + f.RawValue + ")" : string.Empty));
        var text = string.Join(Environment.NewLine, lines);
        return modes.Facts.All(f => f.Evidence == ExecutionModeEvidence.Configured)
            ? text + Environment.NewLine + loc["mode.defaultNote"] : text;
    }

    public static IEnumerable<ExecutionModeUsage> VisibleUsage(IEnumerable<ExecutionModeUsage> rows)
    {
        var list = rows.ToArray();
        var known = list.Where(r => r.Evidence != "unknown").Select(r => r.Kind).ToHashSet();
        return list.Where(r => known.Contains(r.Kind));
    }

    public static string Usage(ExecutionModeUsage row, Localizer loc) =>
        $"{loc["mode.kind." + ExecutionModes.Name(row.Kind)]} · {Value(row.Mode, loc)}"
        + $" · {(row.Evidence == "unknown" ? loc["mode.unknown"] : loc["mode.evidence." + row.Evidence])}"
        + (row.Provider is { } provider ? " · " + provider : string.Empty)
        + $" · {loc["history.table.requests"]}: {row.Requests.ToString("N0", loc.Culture)} · {loc["history.table.output"]}: {SpeedFormat.Tokens(row.Output)}"
        + (row.AverageSpeed is { } speed ? $" · {SpeedFormat.Speed(speed)} {loc["unit.tokS"]}" : string.Empty);
}
