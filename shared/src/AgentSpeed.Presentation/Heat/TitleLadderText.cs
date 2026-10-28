using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Heat;

/// <summary>Text of the speed-title list under the redline slider (DESIGN section 5, Redline speed).</summary>
public static class TitleLadderText
{
    private static readonly Dictionary<(SpeedMetric, SpeedTitle), IReadOnlyList<string>> ShapeCache = new();
    private static readonly Lock CacheGate = new();

    /// <summary>
    /// The title as a list entry: CJK titles as they are, Latin ones in title case (<c>Sports Car</c>, <c>E-Bike</c>)
    /// rather than the orb's capitals.
    /// </summary>
    public static string Name(SpeedTitle title, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return TitleCase(DisplayText.TitleName(title, localizer), localizer);
    }

    /// <summary>
    /// The title of a title theme as a list entry: built-in Latin titles in title case (<c>Gold Core</c>), custom text
    /// exactly as typed.
    /// </summary>
    public static string Name(SpeedTitle title, TitleTheme theme, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(localizer);
        var text = theme.Text(title, localizer);
        return theme.Custom?.Field((int)title) is { Length: > 0 } ? text : TitleCase(text, localizer);
    }

    private static string TitleCase(string name, Localizer localizer)
    {
        if (!localizer.UsesLatinScript)
        {
            return name;
        }

        var culture = localizer.Culture;
        var chars = name.ToLower(culture).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 0 || chars[i - 1] is ' ' or '-')
            {
                chars[i] = char.ToUpper(chars[i], culture);
            }
        }

        return new string(chars);
    }

    /// <summary>Every redline the slider of <paramref name="metric"/> can produce, lowest first.</summary>
    public static IReadOnlyList<double> SliderRedlines(SpeedMetric metric)
    {
        var values = new List<double>();
        if (metric == SpeedMetric.Total)
        {
            // The total-throughput slider is logarithmic: log10 positions in steps of 0.025.
            var low = Math.Log10(HeatMath.MinRedlineTotal);
            var high = Math.Log10(HeatMath.MaxRedlineTotal);
            var steps = (int)Math.Round((high - low) / 0.025);
            for (var i = 0; i <= steps; i++)
            {
                Add(values, HeatMath.ClampRedlineTotal(Math.Pow(10, low + i * 0.025)));
            }
        }
        else
        {
            for (var redline = HeatMath.MinRedline; redline <= HeatMath.MaxRedline + 1e-9; redline += HeatMath.RedlineStep)
            {
                Add(values, HeatMath.ClampRedline(redline));
            }
        }

        return values;
    }

    /// <summary>
    /// The digit patterns a title's threshold takes over the slider range (<c>0.0</c>, <c>00</c>, <c>0.00k</c>, …).
    /// With tabular digits the widest of them is the widest the value ever gets, so reserving it keeps the list still
    /// while the slider moves.
    /// </summary>
    public static IReadOnlyList<string> Shapes(SpeedTitle title, SpeedMetric metric)
    {
        lock (CacheGate)
        {
            if (ShapeCache.TryGetValue((metric, title), out var cached))
            {
                return cached;
            }
        }

        var shapes = new List<string>();
        foreach (var redline in SliderRedlines(metric))
        {
            var shape = Pattern(DisplayText.TitleThreshold(title, redline));
            if (!shapes.Contains(shape, StringComparer.Ordinal))
            {
                shapes.Add(shape);
            }
        }

        lock (CacheGate)
        {
            ShapeCache[(metric, title)] = shapes;
        }

        return shapes;
    }

    /// <summary>Every digit replaced by <c>0</c>: <c>1.28k</c> → <c>0.00k</c>.</summary>
    public static string Pattern(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsAsciiDigit(source[i]) ? '0' : source[i];
            }
        });
    }

    private static void Add(List<double> values, double value)
    {
        if (values.Count == 0 || Math.Abs(values[^1] - value) > 1e-9)
        {
            values.Add(value);
        }
    }
}
