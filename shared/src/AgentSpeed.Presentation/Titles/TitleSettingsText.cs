using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Titles;

/// <summary>Text of the title theme settings (DESIGN 5, General and Title themes): lists, cards, the editor's rows.</summary>
public static class TitleSettingsText
{
    /// <summary>The texts of the four strip levels (1, 5, 9, 13) as typed or localized.</summary>
    public static IReadOnlyList<string> StripTitles(TitleTheme theme, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return TitleCardStrip.Levels.Select(level => theme.Text(level, localizer)).ToArray();
    }

    /// <summary>The whole ladder with the thresholds at <paramref name="redline"/>: <c>凡人 0.5 · 練氣 40 · … · 道祖 2.00k</c>.</summary>
    public static string Ladder(TitleTheme theme, double redline, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return string.Join(TitleCardStrip.Separator,
            SpeedTitles.Ladder.Select(step => $"{theme.Text(step.Title, localizer)} {DisplayText.TitleThreshold(step.Title, redline)}"));
    }

    /// <summary><c>titles.card.a11y</c>: the theme's name and its strip titles joined with <c>list.separator</c>.</summary>
    public static string CardAccessibleName(string name, IEnumerable<string> titles, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Format("titles.card.a11y", name, string.Join(localizer["list.separator"], titles));
    }

    /// <summary>A row's speed column: the threshold of level 1–13 at <paramref name="redline"/>; empty for idle.</summary>
    public static string Threshold(int level, double redline) =>
        level <= 0 ? string.Empty : DisplayText.TitleThreshold(LevelTitle(level), redline);

    /// <summary>
    /// A field's accessibility label: <c>titles.custom.row.a11y</c> (level, threshold with the unit, the base title) or
    /// <c>titles.custom.idle.a11y</c>.
    /// </summary>
    public static string RowAccessibleName(int level, double redline, TitlePreset @base, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var baseTitle = BaseText(level, @base, localizer);
        return level <= 0
            ? localizer.Format("titles.custom.idle.a11y", baseTitle)
            : localizer.Format("titles.custom.row.a11y", level, $"{Threshold(level, redline)} {localizer["unit.tokS"]}", baseTitle);
    }

    /// <summary>The base preset's title of a level in the current language (a field's placeholder).</summary>
    public static string BaseText(int level, TitlePreset @base, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return TitleTheme.Of(@base).Text(Math.Clamp(level, 0, TitlePresets.LevelCount), localizer.Get);
    }

    /// <summary>The speed title of level 0 (idle) or 1–13.</summary>
    public static SpeedTitle LevelTitle(int level) => (SpeedTitle)Math.Clamp(level, 0, TitlePresets.LevelCount);
}
