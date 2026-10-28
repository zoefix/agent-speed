using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>Orb names (DESIGN 2b <c>name</c>): the custom one, else built from the sources in source order.</summary>
public static class OrbNaming
{
    public static string DisplayName(OrbDefinition orb, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(orb);
        return orb.Name ?? AutomaticName(orb.Sources, localizer);
    }

    /// <summary>
    /// All sources → <c>orbs.sources.all</c>; one → its name; two → <c>orbs.name.pair</c>; three or more →
    /// <c>orbs.name.more</c> (the first and the number of the others). Unknown keys are ignored.
    /// </summary>
    public static string AutomaticName(OrbSources sources, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (sources.Set is not { } set)
        {
            return localizer["orbs.sources.all"];
        }

        var kinds = set.ToArray();
        return kinds.Length switch
        {
            1 => SourceName(kinds[0], localizer),
            2 => localizer.Format("orbs.name.pair", SourceName(kinds[0], localizer), SourceName(kinds[1], localizer)),
            _ => localizer.Format("orbs.name.more", SourceName(kinds[0], localizer), kinds.Length - 1),
        };
    }

    public static string SourceName(SourceKind kind, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[DisplayText.SourceKey(kind)];
    }

    /// <summary>Source names in source order joined with <c>list.separator</c> (for <c>a11y.orb.sources</c>).</summary>
    public static string SourceList(SourceSet sources, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return string.Join(localizer["list.separator"], sources.Select(k => SourceName(k, localizer)));
    }

    /// <summary>A duplicate's name: a custom name gets <c> 2</c> (kept within 32 characters); automatic stays automatic.</summary>
    public static string? DuplicateName(string? name)
    {
        if (OrbDefinition.CleanName(name) is not { } clean)
        {
            return null;
        }

        const string Suffix = " 2";
        var room = OrbDefinition.MaxNameLength - Suffix.Length;
        return OrbDefinition.Truncate(clean, room).TrimEnd() + Suffix;
    }
}
