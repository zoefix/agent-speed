using System.Globalization;

namespace AgentSpeed.Presentation.Perf;

/// <summary>
/// The score's two number forms (DESIGN 12, <i>Number formats</i>), both from the rounded integer: <b>full</b> with a comma
/// every three digits up to 99,999 (the card, the neon and timing heroes, History, tooltips, accessibility) and
/// <b>compact</b> from 10,000 on, three significant digits with <c>k</c> or <c>M</c> (rows, glass's dial, orbs, the bar).
/// </summary>
public static class PerfNumber
{
    /// <summary>The app's absent mark (U+2013): no score is never <c>0</c>.</summary>
    public const string Dash = "–";

    /// <summary>Every compact cell is measured with this text: below a million no compact value is wider.</summary>
    public const string WidestCompact = "8,888";

    /// <summary>The widest full value a timing or neon cell holds.</summary>
    public const string WidestFull = "99,999";

    private const int FullLimit = 100_000;
    private const int CompactFrom = 10_000;

    /// <summary><paramref name="value"/> grouped, from 100,000 the compact form; <c>–</c> for none.</summary>
    public static string Full(int? value)
    {
        Span<char> text = stackalloc char[MaxCompactLength];
        return new string(text[..FormatFull(value, text)]);
    }

    /// <summary><see cref="Full"/> written into <paramref name="destination"/> without allocating; returns its length.</summary>
    public static int FormatFull(int? value, Span<char> destination)
    {
        if (value is { } v && Math.Abs((long)v) < FullLimit)
        {
            v.TryFormat(destination, out var grouped, "#,0", CultureInfo.InvariantCulture);
            return grouped;
        }

        return FormatCompact(value, destination);
    }

    /// <summary>Below 10,000 the full form; from 10,000 three significant digits and <c>k</c> / <c>M</c>; <c>–</c> for none.</summary>
    public static string Compact(int? value)
    {
        Span<char> text = stackalloc char[MaxCompactLength];
        return new string(text[..FormatCompact(value, text)]);
    }

    /// <summary>Room for any full or compact text (the longest, such as <c>-99,999</c>, is 7 characters).</summary>
    public const int MaxCompactLength = 16;

    /// <summary>
    /// <see cref="Compact"/> written into <paramref name="destination"/> (at least <see cref="MaxCompactLength"/> long) without
    /// allocating, for the renderers' frames; returns its length.
    /// </summary>
    public static int FormatCompact(int? value, Span<char> destination)
    {
        if (value is not { } v)
        {
            destination[0] = Dash[0];
            return 1;
        }

        if (Math.Abs((long)v) < CompactFrom)
        {
            v.TryFormat(destination, out var grouped, "#,0", CultureInfo.InvariantCulture);
            return grouped;
        }

        var length = 0;
        if (v < 0)
        {
            destination[length++] = '-';
        }

        var magnitude = Math.Abs((decimal)v);
        var (thousands, decimals, overflow) = Significant(magnitude / 1000m);
        var suffix = 'k';
        if (overflow)
        {
            (thousands, decimals, _) = Significant(magnitude / 1_000_000m, allowAbove: true);
            suffix = 'M';
        }

        thousands.TryFormat(destination[length..], out var written, decimals switch { 0 => "F0", 1 => "F1", _ => "F2" },
            CultureInfo.InvariantCulture);
        length += written;
        destination[length++] = suffix;
        return length;
    }

    /// <summary>The integer as accessibility speaks it: always in full (<c>12.8k</c> is read as 12,840).</summary>
    public static string Spoken(int value) => Grouped(value);

    /// <summary>A comma every three digits, in every language (DESIGN 12).</summary>
    public static string Grouped(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Three significant digits of <paramref name="value"/> (≥ 1), rounded half away from zero; true when it rounds to
    /// 1000 or more, so the next suffix takes it (<c>999,500</c> → <c>1.00M</c>).
    /// </summary>
    private static (decimal Rounded, int Decimals, bool Overflow) Significant(decimal value, bool allowAbove = false)
    {
        var decimals = value < 10m ? 2 : value < 100m ? 1 : 0;
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (decimals == 2 && rounded >= 10m)
        {
            decimals = 1;
            rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        }

        if (decimals == 1 && rounded >= 100m)
        {
            decimals = 0;
            rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        }

        return (rounded, decimals, rounded >= 1000m && !allowAbove);
    }
}
