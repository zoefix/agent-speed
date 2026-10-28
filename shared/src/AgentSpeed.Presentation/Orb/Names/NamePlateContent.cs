using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>
/// The lines a plate shows (DESIGN 2b, Name plate): the orb's dots and name, the source chip folded into the plate on
/// hover, during a drag and after a change, and the undo chip stacked beyond the plate.
/// </summary>
public static class NamePlateContent
{
    /// <summary>A plate shows at most three source dots.</summary>
    public const int MaxDots = 3;

    private const string StatusSeparator = " · ";

    /// <summary>
    /// One spectrum dot for an all-sources orb; else the first three tracked sources in source order, hollow when
    /// unavailable.
    /// </summary>
    public static ImmutableArray<NamePlateDot> Dots(OrbSources sources, SourceAvailability availability)
    {
        if (sources.Set is not { } set)
        {
            return [NamePlateDot.Spectrum];
        }

        return [.. set.Take(MaxDots).Select(kind => SourceDot(kind, availability))];
    }

    /// <summary>The name plate fitted into <paramref name="budget"/> (pt): dots, then the name shortened as needed.</summary>
    public static NamePlateLayout Plate(OrbDefinition orb, SourceAvailability availability, Localizer localizer, NamePlateMetrics metrics,
        double budget, NamePlateMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(orb);
        ArgumentNullException.ThrowIfNull(measure);
        var dots = Dots(orb.Sources, availability);
        var name = NamePlateName.For(orb, localizer);
        var cjk = name.IsCjk;
        var fit = name.Fit(metrics.TextBudget(dots.Length, budget), t => NamePlateLayout.TextWidth(t, cjk, metrics, measure));
        var line = new NamePlateLine([new NamePlateRun(dots, fit.Text, NamePlateInk.Primary, cjk)]);
        return NamePlateLayout.Of(line, metrics, measure);
    }

    /// <summary>
    /// The source chip in the plate's metrics: dot + name per source for up to two sources, else the first with
    /// <c>+n</c>; an unavailable source shows a hollow dot and its status in the secondary colour; an all-sources orb
    /// shows <c>orbs.sources.all</c>.
    /// </summary>
    public static NamePlateLine Chip(OrbDefinition orb, SourceAvailability availability, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(orb);
        ArgumentNullException.ThrowIfNull(localizer);
        if (orb.Sources.Set is not { } set)
        {
            var all = localizer["orbs.sources.all"];
            return new NamePlateLine([new NamePlateRun([NamePlateDot.Spectrum], all, NamePlateInk.Primary, NamePlateScript.HasCjk(all))]);
        }

        var kinds = set.ToArray();
        if (kinds.Length <= 2)
        {
            return new NamePlateLine([.. kinds.Select(kind => SourceRun(kind, availability, localizer, null))]);
        }

        return new NamePlateLine([SourceRun(kinds[0], availability, localizer, kinds.Length - 1)]);
    }

    /// <summary>The undo chip: the message, then <c>orbs.undo</c> in the accent colour.</summary>
    public static NamePlateLine Undo(string message, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(localizer);
        var undo = localizer["orbs.undo"];
        return new NamePlateLine(
        [
            new NamePlateRun([], message, NamePlateInk.Primary, NamePlateScript.HasCjk(message)),
            new NamePlateRun([], undo, NamePlateInk.Accent, NamePlateScript.HasCjk(undo)),
        ]);
    }

    /// <summary>Whether the folded chip shows anything the plate does not (otherwise nothing moves).</summary>
    public static bool ChipDiffers(NamePlateLine plate, NamePlateLine chip)
    {
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentNullException.ThrowIfNull(chip);
        return !plate.Equals(chip);
    }

    private static NamePlateDot SourceDot(SourceKind kind, SourceAvailability availability) =>
        availability.Available.Contains(kind) ? new NamePlateDot(NamePlateDotKind.Source, kind) : new NamePlateDot(NamePlateDotKind.Hollow, kind);

    private static NamePlateRun SourceRun(SourceKind kind, SourceAvailability availability, Localizer localizer, int? more)
    {
        var name = OrbNaming.SourceName(kind, localizer);
        var state = availability.StateOf(kind);
        var text = state switch
        {
            SourceState.Disabled => name + StatusSeparator + localizer["source.disabled"],
            SourceState.NotFound => name + StatusSeparator + localizer["source.notFound"],
            _ => name,
        };

        if (more is { } count)
        {
            text = localizer.Format("orbs.name.more", text, count);
        }

        var ink = state == SourceState.Available ? NamePlateInk.Primary : NamePlateInk.Secondary;
        return new NamePlateRun([SourceDot(kind, availability)], text, ink, NamePlateScript.HasCjk(text));
    }
}
