using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Menus;

/// <summary>Whether a source can be read (DESIGN 2b, Unavailable sources).</summary>
public enum SourceState
{
    Available,
    NotFound,
    Disabled,
}

/// <summary>
/// Which sources are available (enabled and detected) and which are disabled; every other source is not detected.
/// Use <see cref="All"/> or <see cref="From"/>: <c>default</c> treats every source as not detected.
/// </summary>
public readonly record struct SourceAvailability(SourceSet Available, SourceSet Disabled)
{
    /// <summary>Every source available, for example before the first snapshot.</summary>
    public static SourceAvailability All => new(SourceSet.Everything, SourceSet.Empty);

    /// <summary>
    /// From a snapshot's sources (<see cref="SpeedSnapshot.Sources"/>) by the rule of <see cref="OrbSubset.IsUnavailable"/>;
    /// a source the snapshot does not list counts as available, as it does for the orbs.
    /// </summary>
    public static SourceAvailability From(IEnumerable<SourceStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var available = SourceSet.Everything;
        var disabled = SourceSet.Empty;
        foreach (var status in statuses)
        {
            if (status is null || !SourceSet.Everything.Contains(status.Kind))
            {
                continue;
            }

            if (!status.Enabled)
            {
                disabled = disabled.With(status.Kind);
                available = available.Without(status.Kind);
            }
            else if (OrbSubset.IsUnavailable(status))
            {
                available = available.Without(status.Kind);
            }
        }

        return new SourceAvailability(available, disabled);
    }

    public SourceState StateOf(SourceKind kind) =>
        Available.Contains(kind) ? SourceState.Available
        : Disabled.Contains(kind) ? SourceState.Disabled
        : SourceState.NotFound;
}
