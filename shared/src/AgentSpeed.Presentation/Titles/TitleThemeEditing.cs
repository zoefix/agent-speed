namespace AgentSpeed.Presentation.Titles;

/// <summary>The result of deleting a custom set: the new document and the base preset that replaces the set everywhere.</summary>
public readonly record struct TitleThemeDeletion(TitleThemesDocument Document, TitlePreset Base);

/// <summary>
/// Operations on the title themes setting (DESIGN 1, Custom title sets). Each is pure; the app keeps the previous
/// document (and the orbs document) to undo a delete or a reset for 8 s.
/// </summary>
public static class TitleThemeEditing
{
    /// <summary>
    /// A new custom set copied from <paramref name="sourceId"/> (a preset or a custom set), appended and selected. Its
    /// base is the preset the source is (or is based on); a custom source also passes on its texts. Null at eight sets,
    /// for an unknown source or an id already in use. <paramref name="copyName"/> formats <c>titles.custom.copyName</c>
    /// with the source's name.
    /// </summary>
    public static TitleThemesDocument? Copy(TitleThemesDocument document, string sourceId, string newId, string sourceName,
        Func<string, string> copyName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(copyName);
        if (!document.CanAddCustom || document.Theme(sourceId) is not { } source || CustomTitleSet.CleanId(newId) != newId
            || document.Theme(newId) is not null)
        {
            return null;
        }

        var set = new CustomTitleSet(newId, UniqueName(document, copyName(sourceName)), source.Base,
            source.Custom?.Idle, source.Custom?.Titles);
        return document with { Custom = document.Custom.Add(set), Selected = newId };
    }

    /// <summary><paramref name="name"/>, else <c>name 2</c>, <c>name 3</c> … whichever no custom set has yet (within 32 characters).</summary>
    public static string UniqueName(TitleThemesDocument document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        var taken = document.Custom.Select(set => set.Name).ToHashSet(StringComparer.Ordinal);
        var clean = CustomTitleSet.CleanName(name) ?? name ?? string.Empty;
        if (!taken.Contains(clean))
        {
            return clean;
        }

        for (var number = 2; ; number++)
        {
            var suffix = $" {number}";
            var stem = TitleText.Sanitize(clean, CustomTitleSet.NameLimit - suffix.Length).Text;
            var candidate = stem + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Removes a custom set; the General selection naming it falls back to its base preset, which orbs naming the set
    /// take too (<c>OrbEditing.ReplaceTitleTheme</c>). Null for an unknown id.
    /// </summary>
    public static TitleThemeDeletion? Delete(TitleThemesDocument document, string id)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.CustomSet(id) is not { } set)
        {
            return null;
        }

        var next = document with
        {
            Custom = document.Custom.Remove(set),
            Selected = document.Selected == id ? TitlePresets.Id(set.Base) : document.Selected,
        };
        return new TitleThemeDeletion(next, set.Base);
    }

    /// <summary>Selects a known theme; an unknown id changes nothing.</summary>
    public static TitleThemesDocument Select(TitleThemesDocument document, string id)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Theme(id) is null || document.Selected == id ? document : document with { Selected = id };
    }

    /// <summary>Committing an empty name keeps the previous one.</summary>
    public static TitleThemesDocument Rename(TitleThemesDocument document, string id, string? name) =>
        CustomTitleSet.CleanName(name) is { } clean ? Update(document, id, set => set with { Name = clean }) : document;

    /// <summary>Filled fields stay; empty ones follow the new base at once.</summary>
    public static TitleThemesDocument SetBase(TitleThemesDocument document, string id, TitlePreset @base) =>
        Enum.IsDefined(@base) ? Update(document, id, set => set with { Base = @base }) : document;

    /// <summary>Stores a committed field (already sanitized and fitted by the caller); empty uses the base's title.</summary>
    public static TitleThemesDocument SetField(TitleThemesDocument document, string id, int level, string? text) =>
        Update(document, id, set => set.WithField(level, text));

    /// <summary>Empties all 14 fields.</summary>
    public static TitleThemesDocument ResetAll(TitleThemesDocument document, string id) =>
        Update(document, id, set => set with { Idle = string.Empty, Titles = CustomTitleSet.Normalized(null) });

    private static TitleThemesDocument Update(TitleThemesDocument document, string id, Func<CustomTitleSet, CustomTitleSet> change)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.CustomSet(id) is not { } set)
        {
            return document;
        }

        var index = document.Custom.IndexOf(set);
        var changed = change(set);
        return changed.Equals(set) ? document : document with { Custom = document.Custom.SetItem(index, changed) };
    }
}
