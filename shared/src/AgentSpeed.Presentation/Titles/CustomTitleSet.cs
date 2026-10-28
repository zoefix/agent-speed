using System.Collections.Immutable;

namespace AgentSpeed.Presentation.Titles;

/// <summary>
/// A user's copy of a theme (DESIGN 1, Custom title sets): a literal name, the preset it is based on, and 13 titles plus
/// an idle label where an empty field means "the base's title in the current language".
/// </summary>
public sealed record CustomTitleSet
{
    public const int NameLimit = 32;
    public const int MaxIdLength = 64;

    /// <param name="titles">Level 1 first; padded with empty fields or cut to 13, each re-normalized by the text rules.</param>
    public CustomTitleSet(string id, string name, TitlePreset @base, string? idle = null, IEnumerable<string?>? titles = null)
    {
        Id = id;
        Name = CleanName(name) ?? string.Empty;
        Base = Enum.IsDefined(@base) ? @base : TitlePreset.Vehicles;
        Idle = TitleText.Sanitize(idle).Text;
        Titles = Normalized(titles);
    }

    /// <summary>UUID string, never reused.</summary>
    public string Id { get; }

    public string Name { get; init; }

    public TitlePreset Base { get; init; }

    /// <summary>The idle label; empty = the base's.</summary>
    public string Idle { get; init; }

    /// <summary>Exactly 13 strings, level 1 first; empty = the base's title.</summary>
    public ImmutableArray<string> Titles { get; init; }

    /// <summary>Every field is empty (Reset All has nothing to do).</summary>
    public bool IsEmpty => Idle.Length == 0 && Titles.All(t => t.Length == 0);

    /// <summary>The field of level 0 (idle) or 1–13.</summary>
    public string Field(int level) =>
        level <= 0 ? Idle : Titles[Math.Min(level, TitlePresets.LevelCount) - 1];

    public CustomTitleSet WithField(int level, string? text)
    {
        var clean = TitleText.Sanitize(text).Text;
        return level <= 0
            ? this with { Idle = clean }
            : this with { Titles = Titles.SetItem(Math.Min(level, TitlePresets.LevelCount) - 1, clean) };
    }

    /// <summary>1–32 characters after the text rules, trimmed; null when empty (committing an empty name keeps the old one).</summary>
    public static string? CleanName(string? name)
    {
        var text = TitleText.Sanitize(name, NameLimit).Text;
        return text.Length == 0 ? null : text;
    }

    /// <summary>A trimmed id of printable characters, at most 64 long; null when unusable.</summary>
    public static string? CleanId(string? id)
    {
        var trimmed = id?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxIdLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }

    public static ImmutableArray<string> Normalized(IEnumerable<string?>? titles)
    {
        var builder = ImmutableArray.CreateBuilder<string>(TitlePresets.LevelCount);
        if (titles is not null)
        {
            foreach (var title in titles)
            {
                if (builder.Count == TitlePresets.LevelCount)
                {
                    break;
                }

                builder.Add(TitleText.Sanitize(title).Text);
            }
        }

        while (builder.Count < TitlePresets.LevelCount)
        {
            builder.Add(string.Empty);
        }

        return builder.MoveToImmutable();
    }

    public bool Equals(CustomTitleSet? other) =>
        ReferenceEquals(this, other)
        || (other is not null && Id == other.Id && Name == other.Name && Base == other.Base && Idle == other.Idle
            && Titles.SequenceEqual(other.Titles, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Id, Name, Base, Idle);
}
