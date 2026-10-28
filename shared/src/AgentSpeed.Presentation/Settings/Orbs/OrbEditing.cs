using AgentSpeed.Core;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>
/// The result of an operation that creates orbs: the new document, the ids it created in list order, and whether a
/// split stopped at eight orbs (<c>orbs.didSplitLimited</c>).
/// </summary>
public sealed record OrbEditResult(OrbsDocument Document, IReadOnlyList<string> Created, bool Limited = false);

/// <summary>
/// Document operations (DESIGN 2b). Each one is pure and returns null when it is refused; the app keeps the previous
/// document to undo split, merge and remove for 8 s. <c>available</c> is the set of detected, enabled sources.
/// New orbs get no position here: place them with <see cref="OrbArrangement"/>.
/// </summary>
public static class OrbEditing
{
    /// <summary>
    /// Adds an orb after <paramref name="templateId"/> (else after the first orb) with that orb's style, size, opacity,
    /// toggles and name plate setting, tracking the first available source no orb tracks explicitly, else all sources.
    /// Null at eight.
    /// </summary>
    public static OrbEditResult? Add(OrbsDocument document, string? templateId, SourceSet available, Func<string>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CanAdd || (document.Find(templateId) ?? document.Orbs.FirstOrDefault()) is not { } template)
        {
            return null;
        }

        var tracked = SourceSet.Empty;
        foreach (var orb in document.Orbs)
        {
            tracked = tracked.Union(orb.Sources.Set ?? SourceSet.Empty);
        }

        var free = available.Except(tracked);
        var id = FreshId(document, newId);
        var added = Child(template, id) with
        {
            Sources = free.IsEmpty ? OrbSources.All : OrbSources.Of(free.First()),
            RedlineOutput = null,
            RedlineTotal = null,
        };
        return new OrbEditResult(Insert(document, added, template.Id), [id]);
    }

    /// <summary>An exact copy after the original with a new id; a custom name gets <c> 2</c>. Null at eight.</summary>
    public static OrbEditResult? Duplicate(OrbsDocument document, string id, Func<string>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CanAdd || document.Find(id) is not { } original)
        {
            return null;
        }

        var copy = original with { Id = FreshId(document, newId), Name = OrbNaming.DuplicateName(original.Name) };
        return new OrbEditResult(Insert(document, copy, original.Id), [copy.Id]);
    }

    /// <summary>Removes an orb and its positions; null for the last orb (the master switch hides it) or an unknown id.</summary>
    public static OrbsDocument? Remove(OrbsDocument document, string id)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.CanRemove && document.Find(id) is not null
            ? document.WithOrbs(document.Orbs.Where(o => o.Id != id))
            : null;
    }

    /// <summary>Whether "Split this orb" is enabled: two or more available sources in its set, and room for one more orb.</summary>
    public static bool CanSplit(OrbsDocument document, string id, SourceSet available)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.CanAdd && document.Find(id) is { } orb && orb.Sources.Resolve(available).Count >= 2;
    }

    /// <summary>
    /// "Split this orb": one orb per available source of its set (every available source for an all-sources orb), in
    /// source order. The first keeps the id and so its position; the others follow it in the list with the same
    /// style, size, opacity, toggles, redlines and name plate setting. Every resulting orb has an automatic name. At most
    /// eight orbs in total (<see cref="OrbEditResult.Limited"/>); null with fewer than two available sources or at eight
    /// orbs.
    /// </summary>
    public static OrbEditResult? Split(OrbsDocument document, string id, SourceSet available, Func<string>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanSplit(document, id, available))
        {
            return null;
        }

        var orb = document.Find(id)!;
        var sources = orb.Sources.Resolve(available).ToArray();
        var count = Math.Min(sources.Length, OrbsDocument.MaxOrbs - document.Count + 1);
        var next = document.Replace(orb with { Name = null, Sources = OrbSources.Of(sources[0]) });
        var created = new List<string>(count - 1);
        var after = id;
        for (var i = 1; i < count; i++)
        {
            var child = Child(orb, FreshId(next, newId)) with { Sources = OrbSources.Of(sources[i]) };
            next = Insert(next, child, after);
            after = child.Id;
            created.Add(child.Id);
        }

        return new OrbEditResult(next, created.ToArray(), count < sources.Length);
    }

    /// <summary>Whether Settings → Split by source is enabled (two or more available sources).</summary>
    public static bool CanSplitBySource(SourceSet available) => available.Count >= 2;

    /// <summary>
    /// Settings → Split by source: every orb is replaced by one per available source, styled like
    /// <paramref name="selectedId"/> (else the first orb). The first reuses the first orb's id and position; at most
    /// eight. Null with fewer than two available sources.
    /// </summary>
    public static OrbEditResult? SplitBySource(OrbsDocument document, string? selectedId, SourceSet available, Func<string>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanSplitBySource(available) || document.Orbs.IsEmpty)
        {
            return null;
        }

        var first = document.Orbs[0];
        var template = document.Find(selectedId) ?? first;
        var sources = available.ToArray();
        var count = Math.Min(sources.Length, OrbsDocument.MaxOrbs);
        var head = Child(template, first.Id) with { Sources = OrbSources.Of(sources[0]) };
        var next = document.WithOrbs([head]);
        var created = new List<string>(count - 1);
        for (var i = 1; i < count; i++)
        {
            var child = Child(template, FreshId(document, newId, next)) with { Sources = OrbSources.Of(sources[i]) };
            next = next with { Orbs = next.Orbs.Add(child) };
            created.Add(child.Id);
        }

        return new OrbEditResult(next, created.ToArray(), count < sources.Length);
    }

    /// <summary>
    /// Merge: every orb is replaced by one all-sources orb that keeps <paramref name="keepId"/>'s id (and position),
    /// style, size, opacity, toggles, redlines and name plate setting, with an automatic name. Null with a single orb or
    /// an unknown id.
    /// </summary>
    public static OrbsDocument? Merge(OrbsDocument document, string keepId)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CanMerge || document.Find(keepId) is not { } kept)
        {
            return null;
        }

        return document.WithOrbs([kept with { Name = null, Sources = OrbSources.All, Visible = true }]);
    }

    /// <summary>Replaces the sources; an explicit selection without a known source is refused (null).</summary>
    public static OrbsDocument? SetSources(OrbsDocument document, string id, OrbSources sources)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!sources.IsAll && sources.Known.IsEmpty)
        {
            return null;
        }

        return Update(document, id, o => o with { Sources = sources });
    }

    /// <summary>Checking <c>orbs.sources.all</c> replaces the set.</summary>
    public static OrbsDocument SetAllSources(OrbsDocument document, string id) => Update(document, id, o => o with { Sources = OrbSources.All });

    /// <summary>
    /// A click on a source row: checks it (on an all-sources orb the set becomes that source) or unchecks it; null when
    /// unchecking the last tracked source (<c>orbs.sources.lastOne</c>).
    /// </summary>
    public static OrbsDocument? ToggleSource(OrbsDocument document, string id, SourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Find(id)?.Sources.Toggling(kind) is { } sources ? Update(document, id, o => o with { Sources = sources }) : null;
    }

    /// <summary>Whether unchecking <paramref name="kind"/> is allowed (false for the last tracked source).</summary>
    public static bool CanUncheck(OrbDefinition orb, SourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(orb);
        return orb.Sources.Unchecking(kind) is not null;
    }

    /// <summary>Changes the style and clamps the size to its range.</summary>
    public static OrbsDocument SetStyle(OrbsDocument document, string id, OrbStyle style) => Update(document, id, o => o.WithStyle(style));

    public static OrbsDocument SetSize(OrbsDocument document, string id, double size) => Update(document, id, o => o.WithSize(size));

    public static OrbsDocument SetOpacity(OrbsDocument document, string id, double opacity) =>
        Update(document, id, o => o with { Opacity = OrbDefinition.CleanOpacity(opacity) });

    public static OrbsDocument SetAlwaysOnTop(OrbsDocument document, string id, bool value) => Update(document, id, o => o with { AlwaysOnTop = value });

    public static OrbsDocument SetClickThrough(OrbsDocument document, string id, bool value) => Update(document, id, o => o with { ClickThrough = value });

    public static OrbsDocument SetFadeWhenIdle(OrbsDocument document, string id, bool value) => Update(document, id, o => o with { FadeWhenIdle = value });

    public static OrbsDocument SetAllSpaces(OrbsDocument document, string id, bool value) => Update(document, id, o => o with { AllSpaces = value });

    public static OrbsDocument SetVisible(OrbsDocument document, string id, bool value) => Update(document, id, o => o with { Visible = value });

    /// <summary>Trimmed and limited to 32 characters; empty means automatic.</summary>
    public static OrbsDocument SetName(OrbsDocument document, string id, string? name) =>
        Update(document, id, o => o with { Name = OrbDefinition.CleanName(name) });

    /// <summary>The orb's name plate: null follows the global setting, true / false overrides it.</summary>
    public static OrbsDocument SetShowName(OrbsDocument document, string id, bool? show) => Update(document, id, o => o with { ShowName = show });

    /// <summary>tok/s or the money unit (DESIGN 9).</summary>
    public static OrbsDocument SetUnit(OrbsDocument document, string id, OrbUnit unit) =>
        Update(document, id, o => o with { Unit = Enum.IsDefined(unit) ? unit : OrbUnit.Tokens });

    /// <summary>The traffic readout (DESIGN 10).</summary>
    public static OrbsDocument SetTraffic(OrbsDocument document, string id, OrbTraffic traffic) =>
        Update(document, id, o => o with { Traffic = Enum.IsDefined(traffic) ? traffic : OrbTraffic.Sources });

    /// <summary>The orb's title theme: null follows the General theme, else a preset or custom set id.</summary>
    public static OrbsDocument SetTitleTheme(OrbsDocument document, string id, string? themeId) =>
        Update(document, id, o => o with { TitleTheme = OrbDefinition.CleanId(themeId) });

    /// <summary>Every orb naming <paramref name="themeId"/> takes <paramref name="replacement"/> (a deleted custom set's base).</summary>
    public static OrbsDocument ReplaceTitleTheme(OrbsDocument document, string themeId, string? replacement)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Orbs.Any(o => o.TitleTheme == themeId))
        {
            return document;
        }

        return document with
        {
            Orbs = [.. document.Orbs.Select(o => o.TitleTheme == themeId ? o with { TitleTheme = OrbDefinition.CleanId(replacement) } : o)],
        };
    }

    /// <summary>Undo of <see cref="ReplaceTitleTheme"/>: every orb that still exists takes its title theme from <paramref name="previous"/>.</summary>
    public static OrbsDocument RestoreTitleThemes(OrbsDocument document, OrbsDocument previous)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(previous);
        if (!document.Orbs.Any(o => previous.Find(o.Id) is { } old && old.TitleTheme != o.TitleTheme))
        {
            return document;
        }

        return document with
        {
            Orbs = [.. document.Orbs.Select(o => previous.Find(o.Id) is { } old ? o with { TitleTheme = old.TitleTheme } : o)],
        };
    }

    /// <summary>Null follows the General redline of that metric.</summary>
    public static OrbsDocument SetRedline(OrbsDocument document, string id, SpeedMetric metric, double? redline) =>
        Update(document, id, o => o.WithRedline(metric, redline));

    /// <summary>Moves an orb to <paramref name="index"/> in the list (clamped); positions are unchanged.</summary>
    public static OrbsDocument Move(OrbsDocument document, string id, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        var from = document.IndexOf(id);
        if (from < 0)
        {
            return document;
        }

        var to = Math.Clamp(index, 0, document.Count - 1);
        if (to == from)
        {
            return document;
        }

        var orb = document.Orbs[from];
        return document with { Orbs = document.Orbs.RemoveAt(from).Insert(to, orb) };
    }

    /// <summary>The master switch turned on: when every orb is individually hidden, all of them become visible.</summary>
    public static OrbsDocument ShowAll(OrbsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.AllHidden ? document with { Orbs = [.. document.Orbs.Select(o => o with { Visible = true })] } : document;
    }

    private static OrbsDocument Update(OrbsDocument document, string id, Func<OrbDefinition, OrbDefinition> change)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Find(id) is { } orb ? document.Replace(change(orb) with { Id = orb.Id }) : document;
    }

    /// <summary>A new orb with the template's look, window options and name plate setting; automatic name, visible, all sources.</summary>
    private static OrbDefinition Child(OrbDefinition template, string id) => new(id)
    {
        Style = template.Style,
        Size = template.Size,
        Opacity = template.Opacity,
        AlwaysOnTop = template.AlwaysOnTop,
        ClickThrough = template.ClickThrough,
        FadeWhenIdle = template.FadeWhenIdle,
        AllSpaces = template.AllSpaces,
        RedlineOutput = template.RedlineOutput,
        RedlineTotal = template.RedlineTotal,
        ShowName = template.ShowName,
        TitleTheme = template.TitleTheme,
        Unit = template.Unit,
        Traffic = template.Traffic,
        Visible = true,
    };

    private static OrbsDocument Insert(OrbsDocument document, OrbDefinition orb, string afterId)
    {
        var index = document.IndexOf(afterId);
        return document with { Orbs = document.Orbs.Insert(index < 0 ? document.Count : index + 1, orb) };
    }

    /// <summary>A UUID that no orb of the documents uses (ids are never reused).</summary>
    private static string FreshId(OrbsDocument document, Func<string>? newId, OrbsDocument? also = null)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var id = OrbDefinition.CleanId(newId?.Invoke()) ?? OrbDefinition.NewId();
            if (document.Find(id) is null && also?.Find(id) is null)
            {
                return id;
            }
        }

        return OrbDefinition.NewId();
    }
}
