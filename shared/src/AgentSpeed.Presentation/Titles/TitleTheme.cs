using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Titles;

/// <summary>A title theme in use: a preset, or a custom set filled in from its base preset.</summary>
public sealed record TitleTheme
{
    private TitleTheme(TitlePreset preset, CustomTitleSet? custom)
    {
        Base = preset;
        Custom = custom;
    }

    public static TitleTheme Vehicles { get; } = new(TitlePreset.Vehicles, null);

    public static TitleTheme Of(TitlePreset preset) => preset == TitlePreset.Vehicles ? Vehicles : new(preset, null);

    public static TitleTheme Of(CustomTitleSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new(set.Base, set);
    }

    /// <summary>The preset itself, or the preset that fills a custom set's empty fields.</summary>
    public TitlePreset Base { get; }

    /// <summary>The custom set; null for a preset.</summary>
    public CustomTitleSet? Custom { get; }

    public bool IsCustom => Custom is not null;

    public string Id => Custom?.Id ?? TitlePresets.Id(Base);

    /// <summary>The dashboard hero keeps the vehicle for Vehicles and the sets based on it (DESIGN 4c).</summary>
    public bool ShowsVehicleArt => TitlePresets.ShowsVehicleArt(Base);

    /// <summary>
    /// The text of level 0 (idle) or 1–13 as typed (custom) or as localized (preset): the editor, the title lists and
    /// accessibility use it as it is.
    /// </summary>
    public string Text(int level, Func<string, string> localized)
    {
        ArgumentNullException.ThrowIfNull(localized);
        if (Custom?.Field(level) is { Length: > 0 } field)
        {
            return field;
        }

        return localized(TitlePresets.Key(Base, level));
    }

    public string Text(SpeedTitle title, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return Text((int)title, localizer.Get);
    }

    /// <summary>The text as the readout and the badge draw it: Latin uppercase when the app language is English.</summary>
    public string Label(SpeedTitle title, Localizer localizer) =>
        TitleText.DisplayCase(Text(title, localizer), localizer.UsesLatinScript);

    /// <summary>Every level's <see cref="Label"/>, idle first (indexed by <see cref="SpeedTitle"/>).</summary>
    public string[] Labels(Localizer localizer)
    {
        var labels = new string[TitlePresets.LevelCount + 1];
        for (var level = 0; level < labels.Length; level++)
        {
            labels[level] = Label((SpeedTitle)level, localizer);
        }

        return labels;
    }

    /// <summary>
    /// The name shown in pickers: the localized preset name, or the custom set's literal name (a hand-edited set without
    /// one reads as a copy of its base).
    /// </summary>
    public string Name(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var preset = localizer[TitlePresets.NameKey(Base)];
        return Custom switch
        {
            null => preset,
            { Name.Length: > 0 } set => set.Name,
            _ => localizer.Format("titles.custom.copyName", preset),
        };
    }

    public bool Equals(TitleTheme? other) =>
        ReferenceEquals(this, other) || (other is not null && Base == other.Base && Equals(Custom, other.Custom));

    public override int GetHashCode() => HashCode.Combine(Base, Custom);
}
