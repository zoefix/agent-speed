using System.Globalization;
using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>One floating orb (DESIGN 2b, Data model). Immutable; <see cref="Validated"/> brings every field into range.</summary>
public sealed record OrbDefinition
{
    public const int MaxNameLength = 32;
    public const int MaxIdLength = 64;
    public const double DefaultSize = 148;
    public const double MinOpacity = 0.4;
    public const double MaxOpacity = 1.0;

    public OrbDefinition(string id)
    {
        Id = id;
    }

    /// <summary>UUID string, never reused.</summary>
    public string Id { get; init; }

    /// <summary>Custom name (1–32 characters, trimmed); null = automatic (<see cref="OrbNaming"/>).</summary>
    public string? Name { get; init; }

    public OrbSources Sources { get; init; } = OrbSources.All;

    public OrbStyle Style { get; init; } = OrbStyle.Led;

    /// <summary>Diameter in DIPs within the style's range.</summary>
    public double Size { get; init; } = DefaultSize;

    public double Opacity { get; init; } = MaxOpacity;

    public bool AlwaysOnTop { get; init; } = true;

    public bool ClickThrough { get; init; }

    public bool FadeWhenIdle { get; init; }

    /// <summary>macOS only; kept for the round trip.</summary>
    public bool AllSpaces { get; init; } = true;

    public bool Visible { get; init; } = true;

    /// <summary>Null follows the General redline of the output metric.</summary>
    public double? RedlineOutput { get; init; }

    /// <summary>Null follows the General redline of the total metric.</summary>
    public double? RedlineTotal { get; init; }

    /// <summary>Null follows the global <see cref="AppSettings.OrbNames"/>; true / false shows or hides this orb's name plate.</summary>
    public bool? ShowName { get; init; }

    /// <summary>
    /// Null follows the General title theme; otherwise a preset id or a custom set id naming this orb's titles (DESIGN 2b
    /// <c>titleTheme</c>). An unknown id is kept (a newer version's preset) and reads as null.
    /// </summary>
    public string? TitleTheme { get; init; }

    /// <summary>tok/s or the money unit (DESIGN 2b <c>unit</c>, 9); Add, Duplicate and Split copy it, Merge keeps the target's.</summary>
    public OrbUnit Unit { get; init; } = OrbUnit.Tokens;

    /// <summary>The traffic readout (DESIGN 2b <c>traffic</c>, 10); Add, Duplicate and Split copy it, Merge keeps the target's.</summary>
    public OrbTraffic Traffic { get; init; } = OrbTraffic.Sources;

    /// <summary>The engine's source set: null tracks every enabled source.</summary>
    public SourceSet? TrackedSources => Sources.Set;

    public static string NewId() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    /// <summary>This orb's redline for a metric: its own, else the General one.</summary>
    public double EffectiveRedline(SpeedMetric metric, AppSettings general)
    {
        ArgumentNullException.ThrowIfNull(general);
        return (metric == SpeedMetric.Total ? RedlineTotal : RedlineOutput) ?? general.RedlineFor(metric);
    }

    /// <summary>Null follows the General redline again; values are clamped to the metric's range.</summary>
    public OrbDefinition WithRedline(SpeedMetric metric, double? redline) => metric == SpeedMetric.Total
        ? this with { RedlineTotal = CleanRedline(SpeedMetric.Total, redline) }
        : this with { RedlineOutput = CleanRedline(SpeedMetric.Output, redline) };

    /// <summary>Another style; the size is clamped to its range (a 72 pt <c>minimal</c> becomes 96 pt).</summary>
    public OrbDefinition WithStyle(OrbStyle style)
    {
        var defined = Enum.IsDefined(style) ? style : OrbStyle.Led;
        return this with { Style = defined, Size = OrbStyles.ClampSize(defined, Size) };
    }

    public OrbDefinition WithSize(double size) => this with { Size = OrbStyles.ClampSize(Style, size) };

    /// <summary>The disc radius in physical pixels at a display scale.</summary>
    public double RadiusPx(double scale) => Size / 2 * scale;

    public OrbDefinition Validated()
    {
        var style = Enum.IsDefined(Style) ? Style : OrbStyle.Led;
        return this with
        {
            Id = CleanId(Id) ?? NewId(),
            Name = CleanName(Name),
            Style = style,
            Size = OrbStyles.ClampSize(style, Size),
            Opacity = CleanOpacity(Opacity),
            RedlineOutput = CleanRedline(SpeedMetric.Output, RedlineOutput),
            RedlineTotal = CleanRedline(SpeedMetric.Total, RedlineTotal),
            TitleTheme = CleanId(TitleTheme),
            Unit = Enum.IsDefined(Unit) ? Unit : OrbUnit.Tokens,
            Traffic = Enum.IsDefined(Traffic) ? Traffic : OrbTraffic.Off,
        };
    }

    /// <summary>0.4–1, rounded to 0.001 so the stored value survives the JSON round trip.</summary>
    public static double CleanOpacity(double opacity) =>
        double.IsFinite(opacity) ? Math.Round(Math.Clamp(opacity, MinOpacity, MaxOpacity), 3) : MaxOpacity;

    public static double? CleanRedline(SpeedMetric metric, double? redline) =>
        redline is { } value && double.IsFinite(value) ? HeatMath.ClampRedline(metric, value) : null;

    /// <summary>A trimmed id of printable characters, at most 64 long; null when unusable.</summary>
    public static string? CleanId(string? id)
    {
        var trimmed = id?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxIdLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }

    /// <summary>
    /// Control characters become spaces, then trimmed and cut to 32 characters (text elements, so emoji stay whole);
    /// empty means automatic (null).
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = name.Length > 1024 ? name[..1024] : name;
        if (text.Any(char.IsControl))
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                builder.Append(char.IsControl(c) ? ' ' : c);
            }

            text = builder.ToString();
        }

        text = Truncate(text.Trim(), MaxNameLength).Trim();
        return text.Length == 0 ? null : text;
    }

    internal static string Truncate(string text, int maxElements)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= maxElements ? text : info.SubstringByTextElements(0, maxElements);
    }
}
