namespace AgentSpeed.Core;

/// <summary>
/// Fact kinds (EXECUTION-MODES, Common facts). <see cref="Speed"/> is never a fact: it names the breakdown derived from
/// the service tier, accelerator and highspeed model variant (Reasoning effort and speed).
/// </summary>
public enum ExecutionModeKind { ServiceTier, Accelerator, ModelVariant, Routing, Workflow, Effort, Speed }
public enum ExecutionModeEvidence { Configured, Selected, Recorded, Served }
public enum ExecutionModeScope { Configuration, Session, Turn, Request, Response }

public sealed record ExecutionModeFact(
    ExecutionModeKind Kind, string Value, string RawValue, ExecutionModeEvidence Evidence,
    string? Provider = null, ExecutionModeScope Scope = ExecutionModeScope.Response)
{
    public static ExecutionModeFact? Create(ExecutionModeKind kind, string? raw, ExecutionModeEvidence evidence,
        string? provider = null, ExecutionModeScope scope = ExecutionModeScope.Response)
    {
        if (!ExecutionModes.IsIdentifier(raw, 64) || (provider is not null && !ExecutionModes.IsIdentifier(provider, 32))
            || !ExecutionModes.IsFactKind(kind) || !Enum.IsDefined(evidence) || !Enum.IsDefined(scope)) return null;
        var value = raw!.ToLowerInvariant();
        if (kind == ExecutionModeKind.ServiceTier)
            value = (provider, value) switch { ("openai", "default") => "standard", ("openai" or "google", "priority") => "fast", _ => value };
        return new(kind, value, raw, evidence, provider, scope);
    }
}

public readonly record struct ExecutionModeAttribution(ExecutionModeKind Kind, string Mode, string Evidence, string? Provider)
{
    public bool IsKnown => Mode != "unknown";

    /// <summary>A value only a config file holds (EXECUTION-MODES, Current values): shown with its own form, reported never.</summary>
    public bool IsConfigured => Evidence == "configured";

    public static ExecutionModeAttribution Unknown(ExecutionModeKind kind) => new(kind, "unknown", "unknown", null);
}

public sealed class ExecutionModes : IEquatable<ExecutionModes>
{
    public const int MaxFacts = 20;
    private static readonly ExecutionModeKind[] SpeedSources = [ExecutionModeKind.ServiceTier, ExecutionModeKind.Accelerator, ExecutionModeKind.ModelVariant];
    private readonly ExecutionModeFact[] _facts;
    public static ExecutionModes Empty { get; } = new([]);

    /// <summary>The kinds of report extension version 1; version 2 adds <c>effort</c>. <c>speed</c> stays local.</summary>
    public static IReadOnlyList<ExecutionModeKind> ReportKinds { get; } = Array.AsReadOnly(
        [ExecutionModeKind.ServiceTier, ExecutionModeKind.Accelerator, ExecutionModeKind.ModelVariant, ExecutionModeKind.Routing, ExecutionModeKind.Workflow]);

    private static readonly IReadOnlyList<ExecutionModeKind> ReportKindsWithEffort = Array.AsReadOnly([.. ReportKinds, ExecutionModeKind.Effort]);

    /// <summary>The kinds a report carries under the negotiated extension <paramref name="version"/>; none for an unknown one.</summary>
    public static IReadOnlyList<ExecutionModeKind> ReportKindsOf(int version) => version switch
    {
        1 => ReportKinds,
        2 => ReportKindsWithEffort,
        _ => [],
    };

    /// <summary>Every independent breakdown of the history, the mode CSV and today's totals.</summary>
    public static IReadOnlyList<ExecutionModeKind> BreakdownKinds { get; } = Array.AsReadOnly(Enum.GetValues<ExecutionModeKind>());

    public IReadOnlyList<ExecutionModeFact> Facts { get; }
    public bool IsEmpty => _facts.Length == 0;

    public ExecutionModes(IEnumerable<ExecutionModeFact> facts)
    {
        _facts = facts.Where(f => f is not null && IsIdentifier(f.Value, 64) && IsIdentifier(f.RawValue, 64)
                && (f.Provider is null || IsIdentifier(f.Provider, 32)) && IsFactKind(f.Kind) && Enum.IsDefined(f.Evidence) && Enum.IsDefined(f.Scope))
            .Select(f => f with { Value = f.Value.ToLowerInvariant() })
            .GroupBy(f => (f.Kind, f.Evidence, f.Scope, f.Provider)).Select(g => g.Last())
            .OrderByDescending(f => f.Evidence).ThenByDescending(f => f.Scope).ThenBy(f => f.Kind).ThenBy(f => f.Provider, StringComparer.Ordinal)
            .Take(MaxFacts)
            .OrderBy(f => f.Kind).ThenBy(f => f.Evidence).ThenBy(f => f.Scope).ThenBy(f => f.Provider, StringComparer.Ordinal)
            .ToArray();
        Facts = Array.AsReadOnly(_facts);
    }

    public static ExecutionModes Of(ExecutionModeFact? fact) => fact is null ? Empty : new([fact]);
    public ExecutionModes Merge(ExecutionModes? newer) => newer is null || newer.IsEmpty ? this : new(_facts.Concat(newer._facts));

    /// <summary>Adds a fact, replacing the one with the same kind, evidence, provider and scope.</summary>
    public ExecutionModes With(ExecutionModeFact? fact) => fact is null ? this : new(_facts.Append(fact));
    public ExecutionModes Without(ExecutionModeEvidence evidence) => Without(f => f.Evidence == evidence);
    public ExecutionModes Without(Func<ExecutionModeFact, bool> match) => _facts.Any(match) ? new(_facts.Where(f => !match(f))) : this;

    /// <summary>Served, then recorded, then selected; scope breaks ties, and facts that still tie and disagree are unknown.</summary>
    public ExecutionModeAttribution Attribute(ExecutionModeKind kind) => Attribute(kind, ExecutionModeEvidence.Selected, ExecutionModeEvidence.Served);

    /// <summary>
    /// An agent's current value (EXECUTION-MODES, Current values): the first known of its own facts, a session-current fact
    /// and a configured value. Only the last two can carry their evidence here; they reach nothing but this value.
    /// </summary>
    public static ExecutionModeAttribution Current(ExecutionModeKind kind, ExecutionModes facts, ExecutionModes session, ExecutionModes configured)
    {
        var own = facts.Attribute(kind);
        if (own.IsKnown) return own;
        var selected = session.Attribute(kind, ExecutionModeEvidence.Selected, ExecutionModeEvidence.Selected);
        return selected.IsKnown ? selected : configured.Attribute(kind, ExecutionModeEvidence.Configured, ExecutionModeEvidence.Configured);
    }

    /// <summary>
    /// The effort the report ledger freezes for a response (EXECUTION-MODES, Current values, Ledger): its own when known, else
    /// the agent's current one when that is <c>selected</c> or better, without a provider.
    /// </summary>
    public static ExecutionModeAttribution LedgerEffort(ExecutionModeAttribution response, ExecutionModeAttribution current) =>
        response.IsKnown || !current.IsKnown || current.IsConfigured ? response : current with { Provider = null };

    /// <summary>These facts as the ledger freezes them: an unknown effort takes <see cref="LedgerEffort"/>.</summary>
    public ExecutionModes WithLedgerEffort(ExecutionModeAttribution current)
    {
        var own = Attribute(ExecutionModeKind.Effort);
        var frozen = LedgerEffort(own, current);
        return frozen == own ? this : Without(f => f.Kind == ExecutionModeKind.Effort).With(new(ExecutionModeKind.Effort, frozen.Mode, frozen.Mode,
            Enum.Parse<ExecutionModeEvidence>(frozen.Evidence, ignoreCase: true), Scope: ExecutionModeScope.Session));
    }

    private ExecutionModeAttribution Attribute(ExecutionModeKind kind, ExecutionModeEvidence lowest, ExecutionModeEvidence highest)
    {
        if (kind == ExecutionModeKind.Speed) return AttributeSpeed(lowest, highest);
        ExecutionModeFact? chosen = null;
        var disagree = false;
        foreach (var fact in _facts)
        {
            if (fact.Kind != kind || fact.Evidence < lowest || fact.Evidence > highest) continue;
            if (chosen is null || fact.Evidence > chosen.Evidence || (fact.Evidence == chosen.Evidence && fact.Scope > chosen.Scope))
            {
                chosen = fact;
                disagree = false;
            }
            else if (fact.Evidence == chosen.Evidence && fact.Scope == chosen.Scope)
            {
                disagree |= fact.Value != chosen.Value || fact.Provider != chosen.Provider;
            }
        }

        return chosen is null || disagree || chosen.Value is "unknown" or "none" or "auto" or "inherit" or "inherited"
            ? ExecutionModeAttribution.Unknown(kind)
            : new(kind, chosen.Value, Name(chosen.Evidence), chosen.Provider);
    }

    /// <summary>The service tier when resolved, else the accelerator, else a <c>highspeed</c> model variant.</summary>
    private ExecutionModeAttribution AttributeSpeed(ExecutionModeEvidence lowest, ExecutionModeEvidence highest)
    {
        foreach (var kind in SpeedSources)
        {
            var value = Attribute(kind, lowest, highest);
            if (value.IsKnown && (kind != ExecutionModeKind.ModelVariant || value.Mode == "highspeed"))
                return value with { Kind = ExecutionModeKind.Speed };
        }
        return ExecutionModeAttribution.Unknown(ExecutionModeKind.Speed);
    }

    public static bool IsFactKind(ExecutionModeKind kind) => Enum.IsDefined(kind) && kind != ExecutionModeKind.Speed;
    /// <summary>A kind some report version carries (version 2 adds <c>effort</c>); <c>speed</c> never.</summary>
    public static bool IsReportKind(ExecutionModeKind kind) => kind is >= ExecutionModeKind.ServiceTier and <= ExecutionModeKind.Effort;
    public static bool IsIdentifier(string? value, int max) => value is { Length: > 0 } && value.Length <= max
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');
    public static string Name(ExecutionModeKind value) => value switch
    {
        ExecutionModeKind.ServiceTier => "serviceTier", ExecutionModeKind.ModelVariant => "modelVariant",
        ExecutionModeKind.Accelerator => "accelerator", ExecutionModeKind.Routing => "routing",
        ExecutionModeKind.Effort => "effort", ExecutionModeKind.Speed => "speed", _ => "workflow",
    };
    public static string Name(ExecutionModeEvidence value) => value.ToString().ToLowerInvariant();
    public static string Name(ExecutionModeScope value) => value.ToString().ToLowerInvariant();
    public bool Equals(ExecutionModes? other) => other is not null && _facts.SequenceEqual(other._facts);
    public override bool Equals(object? obj) => obj is ExecutionModes modes && Equals(modes);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var fact in _facts) hash.Add(fact); return hash.ToHashCode(); }
}
