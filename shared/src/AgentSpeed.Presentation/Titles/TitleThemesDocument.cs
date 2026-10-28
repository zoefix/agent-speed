using System.Collections.Immutable;

namespace AgentSpeed.Presentation.Titles;

/// <summary>
/// The title themes setting (DESIGN 1, Persistence; settings value <c>titleThemes</c>): the General theme and up to eight
/// custom sets in creation order.
/// </summary>
public sealed record TitleThemesDocument
{
    public const int MaxCustom = 8;
    public const int CurrentVersion = 1;

    public static TitleThemesDocument Default { get; } = new();

    public int Version { get; init; } = CurrentVersion;

    /// <summary>A preset id or a custom set id; default and fallback <c>vehicles</c>.</summary>
    public string Selected { get; init; } = TitlePresets.Id(TitlePreset.Vehicles);

    public ImmutableArray<CustomTitleSet> Custom { get; init; } = [];

    public bool CanAddCustom => Custom.Length < MaxCustom;

    public CustomTitleSet? CustomSet(string? id) =>
        id is null ? null : Custom.FirstOrDefault(set => string.Equals(set.Id, id, StringComparison.Ordinal));

    /// <summary>The theme an id names, or null when it names nothing known (a newer version's preset, a deleted set).</summary>
    public TitleTheme? Theme(string? id)
    {
        if (TitlePresets.Parse(id) is { } preset)
        {
            return TitleTheme.Of(preset);
        }

        return CustomSet(id) is { } set ? TitleTheme.Of(set) : null;
    }

    /// <summary>The General theme; an unknown selection falls back to Vehicles.</summary>
    public TitleTheme General => Theme(Selected) ?? TitleTheme.Vehicles;

    /// <summary>An orb's theme: its own when it names a known theme, else the General theme (DESIGN 2b <c>titleTheme</c>).</summary>
    public TitleTheme OrbTheme(string? id) => Theme(id) ?? General;

    /// <summary>Every theme in picker order: the presets, then the custom sets in creation order.</summary>
    public IReadOnlyList<TitleTheme> AllThemes =>
        [.. TitlePresets.All.Select(TitleTheme.Of), .. Custom.Select(TitleTheme.Of)];

    /// <summary>
    /// Unique, usable ids (a set whose id repeats or is a preset id is dropped), at most eight sets, a non-empty
    /// selection. The selection is kept even when unknown: it then reads as Vehicles.
    /// </summary>
    public TitleThemesDocument Validated()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sets = ImmutableArray.CreateBuilder<CustomTitleSet>(Math.Min(Custom.Length, MaxCustom));
        foreach (var set in Custom.IsDefault ? [] : Custom)
        {
            if (sets.Count == MaxCustom)
            {
                break;
            }

            if (set is null || CustomTitleSet.CleanId(set.Id) is not { } id || id != set.Id || TitlePresets.Parse(id) is not null
                || !seen.Add(id))
            {
                continue;
            }

            sets.Add(set);
        }

        var selected = Selected?.Trim();
        return this with
        {
            Version = CurrentVersion,
            Custom = sets.ToImmutable(),
            Selected = string.IsNullOrEmpty(selected) || selected.Length > CustomTitleSet.MaxIdLength
                ? TitlePresets.Id(TitlePreset.Vehicles)
                : selected,
        };
    }

    public bool Equals(TitleThemesDocument? other) =>
        ReferenceEquals(this, other)
        || (other is not null && Version == other.Version && string.Equals(Selected, other.Selected, StringComparison.Ordinal)
            && Custom.SequenceEqual(other.Custom));

    public override int GetHashCode() => HashCode.Combine(Version, Selected, Custom.Length);
}
