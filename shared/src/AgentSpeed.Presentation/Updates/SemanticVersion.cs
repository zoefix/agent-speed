using System.Globalization;

namespace AgentSpeed.Presentation.Updates;

/// <summary>
/// <c>[v]MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]</c> with SemVer 2 precedence (UPDATES section 2). Build metadata is
/// accepted and dropped; a pre-release sorts before the same version without one.
/// </summary>
public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    public const int MaxLength = 64;
    private const int MaxComponentDigits = 9;

    private readonly string? _preRelease;

    public SemanticVersion(int major, int minor, int patch, string? preRelease = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        if (!string.IsNullOrEmpty(preRelease) && !IsValidDottedIdentifiers(preRelease))
        {
            throw new ArgumentException("Invalid pre-release identifiers.", nameof(preRelease));
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = string.IsNullOrEmpty(preRelease) ? null : preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Pre-release identifiers without the leading '-', or empty.</summary>
    public string PreRelease => _preRelease ?? string.Empty;

    public bool IsPreRelease => _preRelease is not null;

    /// <summary>0.0.0 marks a local development build (UPDATES section 2).</summary>
    public bool IsDevelopment => Major == 0 && Minor == 0 && Patch == 0;

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static SemanticVersion? ParseOrNull(string? text) => TryParse(text, out var version) ? version : null;

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length is 0 or > MaxLength)
        {
            return false;
        }

        if (span[0] is 'v' or 'V')
        {
            span = span[1..];
        }

        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            if (!IsValidDottedIdentifiers(span[(plus + 1)..]))
            {
                return false;
            }

            span = span[..plus];
        }

        string? preRelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            var identifiers = span[(dash + 1)..];
            if (!IsValidDottedIdentifiers(identifiers))
            {
                return false;
            }

            preRelease = identifiers.ToString();
            span = span[..dash];
        }

        Span<Range> parts = stackalloc Range[4];
        if (span.Split(parts, '.') != 3
            || !TryParseComponent(span[parts[0]], out var major)
            || !TryParseComponent(span[parts[1]], out var minor)
            || !TryParseComponent(span[parts[2]], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public bool IsNewerThan(SemanticVersion other) => CompareTo(other) > 0;

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result != 0 ? result : ComparePreRelease(_preRelease, other._preRelease);
    }

    public override string ToString() => _preRelease is null
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{_preRelease}");

    private static bool TryParseComponent(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > MaxComponentDigits)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidDottedIdentifiers(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        var length = 0;
        foreach (var c in text)
        {
            if (c == '.')
            {
                if (length == 0)
                {
                    return false;
                }

                length = 0;
            }
            else if (char.IsAsciiLetterOrDigit(c) || c == '-')
            {
                length++;
            }
            else
            {
                return false;
            }
        }

        return length > 0;
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null ? (right is null ? 0 : 1) : -1;
        }

        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var result = CompareIdentifier(a[i], b[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            var l = left.TrimStart('0');
            var r = right.TrimStart('0');
            return l.Length != r.Length ? l.Length.CompareTo(r.Length) : string.CompareOrdinal(l, r);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    private static bool IsNumeric(string identifier)
    {
        foreach (var c in identifier)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return identifier.Length > 0;
    }
}
