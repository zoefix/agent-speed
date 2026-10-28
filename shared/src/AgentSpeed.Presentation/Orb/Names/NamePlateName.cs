using System.Globalization;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>A name fitted into a width: the text shown and whether it was shortened.</summary>
public readonly record struct NamePlateFit(string Text, double Width, bool Shortened);

/// <summary>
/// An orb's name on its plate and how it shortens (DESIGN 2b, Name plate, Content). The type never shrinks: an
/// automatic pair <c>A + B</c> becomes <c>A +1</c> (<c>orbs.name.more</c>), then <c>A… +1</c>; an automatic
/// <c>A +n</c> becomes <c>A… +n</c>; a single source, all sources or a custom name gets a tail ellipsis on a grapheme
/// boundary.
/// </summary>
public sealed record NamePlateName
{
    private NamePlateName(string text, string? head, int more, string moreTemplate, CultureInfo culture)
    {
        Text = text;
        Head = head;
        More = more;
        MoreTemplate = moreTemplate;
        Culture = culture;
    }

    /// <summary>The full name.</summary>
    public string Text { get; }

    /// <summary>The first source's name of an automatic multi-source name; null for any other name.</summary>
    public string? Head { get; }

    /// <summary>How many sources follow <see cref="Head"/>.</summary>
    public int More { get; }

    /// <summary>The <c>orbs.name.more</c> template (<c>%@ +%d</c>).</summary>
    public string MoreTemplate { get; }

    public CultureInfo Culture { get; }

    /// <summary>The name contains CJK: +0.04 em tracking and the ideographic center for every shortened form too.</summary>
    public bool IsCjk => NamePlateScript.HasCjk(Text);

    /// <summary>A plain name (custom, one source, all sources).</summary>
    public static NamePlateName Plain(string text) =>
        new(text ?? string.Empty, null, 0, "%@ +%d", CultureInfo.InvariantCulture);

    /// <summary>The name of an orb: its custom name, else the automatic one with its shortening steps.</summary>
    public static NamePlateName For(OrbDefinition orb, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(orb);
        ArgumentNullException.ThrowIfNull(localizer);
        if (orb.Name is { } custom)
        {
            return Plain(custom);
        }

        var full = OrbNaming.AutomaticName(orb.Sources, localizer);
        if (orb.Sources.Set is not { Count: >= 2 } set)
        {
            return Plain(full);
        }

        var head = OrbNaming.SourceName(set.First(), localizer);
        return new NamePlateName(full, head, set.Count - 1, localizer.Get("orbs.name.more"), localizer.Culture);
    }

    /// <summary>
    /// The longest form whose width (<paramref name="width"/>, tracking included) fits <paramref name="budget"/>; the
    /// last form is ellipsized and keeps at least one grapheme, so it may still exceed a tiny budget.
    /// </summary>
    public NamePlateFit Fit(double budget, Func<string, double> width)
    {
        ArgumentNullException.ThrowIfNull(width);
        bool Fits(string candidate) => width(candidate) <= budget + 0.001;

        if (Fits(Text))
        {
            return new NamePlateFit(Text, width(Text), false);
        }

        string text;
        if (Head is { } head)
        {
            var compact = Compose(head);
            text = !string.Equals(compact, Text, StringComparison.Ordinal) && Fits(compact)
                ? compact
                : Ellipsis.Fit(head, Compose, Fits);
        }
        else
        {
            text = Ellipsis.Fit(Text, t => t, Fits);
        }

        return new NamePlateFit(text, width(text), true);
    }

    /// <summary><c>orbs.name.more</c> with a (possibly shortened) head.</summary>
    public string Compose(string head) => Localizer.FormatTemplate(MoreTemplate, Culture, head, More);

    public bool Equals(NamePlateName? other) =>
        other is not null && Text == other.Text && Head == other.Head && More == other.More && MoreTemplate == other.MoreTemplate;

    public override int GetHashCode() => HashCode.Combine(Text, Head, More);
}
