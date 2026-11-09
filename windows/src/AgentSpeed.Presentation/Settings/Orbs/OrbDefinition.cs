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

    /// <summary>
    /// The <c>character</c> style's character (DESIGN 2b <c>character</c>): a pack id, or null = Auto, following the
    /// fastest model vendor (CHARACTERS 7). An id that is not installed counts as Auto.
    /// </summary>
    public string? Character { get; init; }

    /// <summary>The cards the <c>character</c> style floats beside her (DESIGN 2a.6, 2b <c>characterReadouts</c>).</summary>
    public CharacterReadouts CharacterReadouts { get; init; } = CharacterReadouts.Default;

    /// <summary>The footprint disc's diameter D in DIPs: the size, or twice it for <c>character</c> (DESIGN 2a.6).</summary>
    public double Footprint => OrbStyles.FootprintDiameter(Style, Size);

    /// <summary>The hosts it tracks (DESIGN 13); absent in the document = all.</summary>
    public OrbHosts Hosts { get; init; } = OrbHosts.All;

    /// <summary>The engine's source set: null tracks every enabled source.</summary>
    public SourceSet? TrackedSources => Sources.Set;

    /// <summary>The engine's host set: null tracks every host.</summary>
    public IReadOnlySet<string>? TrackedHosts => Hosts.HostSet;

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

    /// <summary>Another style; the size stays, since every style shares one range.</summary>
    public OrbDefinition WithStyle(OrbStyle style) => this with { Style = Enum.IsDefined(style) ? style : OrbStyle.Led };

    public OrbDefinition WithSize(double size) => this with { Size = OrbStyles.ClampSize(size) };

    /// <summary>The footprint disc's radius in physical pixels at a display scale.</summary>
    public double RadiusPx(double scale) => Footprint / 2 * scale;

    public OrbDefinition Validated()
    {
        var style = Enum.IsDefined(Style) ? Style : OrbStyle.Led;
        return this with
        {
            Id = CleanId(Id) ?? NewId(),
            Name = CleanName(Name),
            Style = style,
            Size = OrbStyles.ClampSize(Size),
            Opacity = CleanOpacity(Opacity),
            RedlineOutput = CleanRedline(SpeedMetric.Output, RedlineOutput),
            RedlineTotal = CleanRedline(SpeedMetric.Total, RedlineTotal),
            TitleTheme = CleanId(TitleTheme),
            Unit = Enum.IsDefined(Unit) ? Unit : OrbUnit.Tokens,
            Traffic = Enum.IsDefined(Traffic) ? Traffic : OrbTraffic.Off,
            Character = CleanCharacter(Character),
            CharacterReadouts = CharacterReadouts ?? CharacterReadouts.Default,
        };
    }

    /// <summary>A pack id (lowercase letters, digits and dashes, CHARACTERS 1); anything else means Auto (null).</summary>
    public static string? CleanCharacter(string? id)
    {
        var trimmed = id?.Trim();
        return trimmed is { Length: > 0 and <= 64 } && !trimmed.StartsWith('-') && !trimmed.EndsWith('-')
            && trimmed.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            ? trimmed
            : null;
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
