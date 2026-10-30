namespace AgentSpeed.Core.Reporting;

/// <summary>
/// One model's <c>models[].modeUsage</c> (EXECUTION-MODES, Report extension; <c>mode_rows</c> of
/// <c>execution_modes_reference.py</c>): every kind of the negotiated version that is observed, plus <c>serviceTier</c> and
/// (version 2) <c>effort</c>, complete per source with an unknown bucket, ordered by <c>source/kind/provider/mode/evidence</c>.
/// Over 64 rows version 2 leaves <c>effort</c> out first; null when the breakdown still does not fit or cannot be complete.
/// </summary>
public static class ReportModeRows
{
    public const int MaxRows = 64;

    /// <summary>A frozen attribution of one source's responses.</summary>
    public readonly record struct Group(SourceKind Source, ExecutionModeKind Kind, string Mode, string Evidence, string? Provider);

    /// <summary>The rows for <paramref name="sources"/> (each source's totals) and <paramref name="groups"/> (known modes only).</summary>
    public static IReadOnlyList<(Group Key, ReportUsage Usage)>? Build(IReadOnlyDictionary<SourceKind, ReportUsage> sources,
        IReadOnlyDictionary<Group, ReportUsage> groups, int version)
    {
        var reported = ExecutionModes.ReportKindsOf(version);
        if (reported.Count == 0)
        {
            return null;
        }

        var observed = groups.Keys.Where(g => g.Mode != "unknown").Select(g => g.Kind).ToHashSet();
        var complete = new Dictionary<Group, ReportUsage>();
        foreach (var kind in reported.Where(k => observed.Contains(k) || k is ExecutionModeKind.ServiceTier or ExecutionModeKind.Effort))
        {
            foreach (var (source, total) in sources)
            {
                var remaining = Counts(total);
                foreach (var (key, usage) in groups.Where(g => g.Key.Source == source && g.Key.Kind == kind))
                {
                    var part = Counts(usage);
                    if (!Valid(key) || !Fits(part, remaining))
                    {
                        return null;
                    }

                    remaining -= part;
                    complete[key] = complete.GetValueOrDefault(key) + part;
                }

                if (Relevant(remaining))
                {
                    var unknown = new Group(source, kind, "unknown", "unknown", null);
                    complete[unknown] = complete.GetValueOrDefault(unknown) + remaining;
                }
            }
        }

        var rows = complete.Where(p => Relevant(p.Value)).OrderBy(p => Order(p.Key), StringComparer.Ordinal)
            .Select(p => (p.Key, p.Value)).ToList();
        if (rows.Count <= MaxRows)
        {
            return rows;
        }

        return version == 2
            ? Build(sources, groups.Where(g => g.Key.Kind != ExecutionModeKind.Effort).ToDictionary(g => g.Key, g => g.Value), 1)
            : null;
    }

    /// <summary>The rows as the report writes them.</summary>
    public static ReportJson Json(IEnumerable<(Group Key, ReportUsage Usage)> rows) => ReportJson.Array(rows.Select(row =>
    {
        var (key, usage) = row;
        var members = new List<(string, ReportJson)>
        {
            ("source", ReportJson.Of(ReportBody.SourceId(key.Source))), ("kind", ReportJson.Of(ExecutionModes.Name(key.Kind))),
            ("mode", ReportJson.Of(key.Mode)), ("evidence", ReportJson.Of(key.Evidence)), ("requests", ReportJson.Of(usage.Requests)),
            ("output", ReportJson.Of(usage.Output)), ("measuredOutput", ReportJson.Of(usage.MeasuredOutput)), ("measuredMs", ReportJson.Of(usage.MeasuredMs)),
        };
        if (key.Provider is { } provider)
        {
            members.Insert(1, ("provider", ReportJson.Of(provider)));
        }

        return ReportJson.Object(members);
    }));

    private static string Order(Group key) =>
        string.Join('/', ReportBody.SourceId(key.Source), ExecutionModes.Name(key.Kind), key.Provider ?? "", key.Mode, key.Evidence);

    private static ReportUsage Counts(ReportUsage usage) =>
        new() { Requests = usage.Requests, Output = usage.Output, MeasuredOutput = usage.MeasuredOutput, MeasuredMs = usage.MeasuredMs };

    private static bool Relevant(ReportUsage usage) =>
        usage.Requests != 0 || usage.Output != 0 || usage.MeasuredOutput != 0 || usage.MeasuredMs != 0;

    private static bool Fits(ReportUsage part, ReportUsage remaining) =>
        Safe(part.Requests) && Safe(part.Output) && Safe(part.MeasuredOutput) && Safe(part.MeasuredMs)
        && part.Requests <= remaining.Requests && part.Output <= remaining.Output
        && part.MeasuredOutput <= remaining.MeasuredOutput && part.MeasuredMs <= remaining.MeasuredMs;

    private static bool Safe(long value) => value is >= 0 and <= 9_007_199_254_740_991;

    private static bool Valid(Group key)
    {
        if (!ExecutionModes.IsIdentifier(key.Mode, 64) || (key.Provider is not null && !ExecutionModes.IsIdentifier(key.Provider, 32)))
        {
            return false;
        }

        return key.Mode == "unknown"
            ? key.Evidence == "unknown" && key.Provider is null
            : key.Evidence is "served" or "recorded" or "selected";
    }
}
