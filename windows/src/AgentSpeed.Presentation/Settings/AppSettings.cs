using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Chart;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Settings;

public enum AppearanceMode
{
    Auto,
    Light,
    Dark,
}

public enum BarDisplayMode
{
    GlyphAndValue,
    ValueOnly,
    GlyphOnly,
}

public enum EffectsIntensity
{
    Off,
    Soft,
    Standard,
    Max,
}

/// <summary>Orb center as a fraction (0..1) of a monitor's work area, so it survives resolution and DPI changes.</summary>
public readonly record struct OrbAnchor(double X, double Y);

/// <summary>A window's restore bounds in DIPs (virtual-screen coordinates) and whether it was maximized.</summary>
public readonly record struct WindowFrame(double Left, double Top, double Width, double Height, bool Maximized)
{
    public const double MinHistoryWidth = 720;
    public const double MinHistoryHeight = 540;
    public const double MaxExtent = 16_384;
    public const double MaxOffset = 100_000;

    /// <summary>Null unless every value is finite and inside the history window's limits.</summary>
    public static WindowFrame? ValidHistory(WindowFrame? frame) =>
        frame is { } f
        && double.IsFinite(f.Left) && double.IsFinite(f.Top) && double.IsFinite(f.Width) && double.IsFinite(f.Height)
        && f.Width is >= MinHistoryWidth and <= MaxExtent
        && f.Height is >= MinHistoryHeight and <= MaxExtent
        && Math.Abs(f.Left) <= MaxOffset && Math.Abs(f.Top) <= MaxOffset
            ? f
            : null;
}

/// <summary>All user settings (DESIGN section 5). Immutable; use <c>with</c> to change.</summary>
public sealed record AppSettings
{
    public const double MinOrbSize = 96;
    public const double MaxOrbSize = 240;
    public const double DefaultOrbSize = 148;
    public const double MinOrbOpacity = 0.4;
    public const double MaxOrbOpacity = 1.0;
    public const int MaxStoredMonitors = 16;
    public const int MaxPathLength = 32_767;

    public static IReadOnlyList<double> OrbSizePresets { get; } = [112, 148, 196];

    public static AppSettings Default { get; } = new();

    public AppearanceMode Appearance { get; init; } = AppearanceMode.Auto;

    public AppLanguage Language { get; init; } = AppLanguage.System;

    public SpeedMetric Metric { get; init; } = SpeedMetric.Output;

    /// <summary>Redline for the output-speed metric (tok/s).</summary>
    public double RedlineOutput { get; init; } = HeatMath.DefaultRedline;

    /// <summary>Redline for the total-throughput metric (tok/s, includes cached input).</summary>
    public double RedlineTotal { get; init; } = HeatMath.DefaultRedlineTotal;

    /// <summary>Redline of the selected metric; heat and tiers always use this.</summary>
    public double Redline => RedlineFor(Metric);

    public double RedlineFor(SpeedMetric metric) => metric == SpeedMetric.Total ? RedlineTotal : RedlineOutput;

    public AppSettings WithRedline(SpeedMetric metric, double redline) => metric == SpeedMetric.Total
        ? this with { RedlineTotal = HeatMath.ClampRedlineTotal(redline) }
        : this with { RedlineOutput = HeatMath.ClampRedline(redline) };

    public bool ShowOnTaskbar { get; init; } = true;

    public BarDisplayMode BarMode { get; init; } = BarDisplayMode.GlyphAndValue;

    public bool BarColoredGlyph { get; init; } = true;

    public bool BarShowUnit { get; init; } = true;

    public bool ShowOrb { get; init; } = true;

    public double OrbSize { get; init; } = DefaultOrbSize;

    public double OrbOpacity { get; init; } = MaxOrbOpacity;

    public bool OrbAlwaysOnTop { get; init; } = true;

    public bool OrbClickThrough { get; init; }

    public bool OrbFadeWhenIdle { get; init; }

    public EffectsIntensity Effects { get; init; } = EffectsIntensity.Standard;

    public bool CelebratePeaks { get; init; } = true;

    public bool ResponsePulses { get; init; } = true;

    public bool CodexEnabled { get; init; } = true;

    public bool ClaudeCodeEnabled { get; init; } = true;

    public bool ClaudeDesktopEnabled { get; init; } = true;

    public bool DeepSeekEnabled { get; init; } = true;

    public bool OpenCodeEnabled { get; init; } = true;

    public bool GeminiEnabled { get; init; } = true;

    public bool AntigravityEnabled { get; init; } = true;

    public bool AntigravityCliEnabled { get; init; } = true;

    /// <summary>Null uses the default Codex sessions folder.</summary>
    public string? CodexRoot { get; init; }

    /// <summary>Null uses the default Claude projects folder.</summary>
    public string? ClaudeRoot { get; init; }

    /// <summary>Null uses the default DeepSeek Harness sessions folder.</summary>
    public string? DshRoot { get; init; }

    /// <summary>Null uses the default OpenCode data folder (the one that contains opencode.db).</summary>
    public string? OpenCodeRoot { get; init; }

    /// <summary>Null uses the default Gemini CLI <c>tmp</c> folder.</summary>
    public string? GeminiRoot { get; init; }

    public int ChartSeconds { get; init; } = 120;

    /// <summary>The dashboard shows every agent instead of the collapsed list.</summary>
    public bool AgentsExpanded { get; init; }

    /// <summary>
    /// Vehicle key of the last non-idle speed title (for example <c>sportsCar</c>); the dashboard hero keeps that vehicle
    /// parked while idle, also across relaunches (DESIGN 4c "Idle"). Null shows the walker.
    /// </summary>
    public string? HeroLastTitle { get; init; }

    /// <summary>Last frame of the Usage History window; null opens it at its default size, centered.</summary>
    public WindowFrame? HistoryWindow { get; init; }

    /// <summary>Key of the monitor the orb was last placed on.</summary>
    public string? OrbMonitor { get; init; }

    public ImmutableDictionary<string, OrbAnchor> OrbAnchors { get; init; } =
        ImmutableDictionary<string, OrbAnchor>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>Check GitHub for a new version at launch and every 6 hours (UPDATES section 3).</summary>
    public bool AutoCheckUpdates { get; init; } = true;

    /// <summary>Time of the last successful update check.</summary>
    public DateTimeOffset? LastUpdateCheck { get; init; }

    /// <summary>Release tag the user chose to skip; automatic checks never offer it.</summary>
    public string? SkippedVersion { get; init; }

    /// <summary>Release tag a notification was already shown for (one notification per version).</summary>
    public string? NotifiedVersion { get; init; }

    public bool IsSourceEnabled(SourceKind kind) => kind switch
    {
        SourceKind.Codex => CodexEnabled,
        SourceKind.ClaudeCode => ClaudeCodeEnabled,
        SourceKind.ClaudeDesktop => ClaudeDesktopEnabled,
        SourceKind.DeepSeek => DeepSeekEnabled,
        SourceKind.OpenCode => OpenCodeEnabled,
        SourceKind.Gemini => GeminiEnabled,
        SourceKind.Antigravity => AntigravityEnabled,
        SourceKind.AntigravityCli => AntigravityCliEnabled,
        _ => false,
    };

    public AppSettings WithSourceEnabled(SourceKind kind, bool enabled) => kind switch
    {
        SourceKind.Codex => this with { CodexEnabled = enabled },
        SourceKind.ClaudeCode => this with { ClaudeCodeEnabled = enabled },
        SourceKind.ClaudeDesktop => this with { ClaudeDesktopEnabled = enabled },
        SourceKind.DeepSeek => this with { DeepSeekEnabled = enabled },
        SourceKind.OpenCode => this with { OpenCodeEnabled = enabled },
        SourceKind.Gemini => this with { GeminiEnabled = enabled },
        SourceKind.Antigravity => this with { AntigravityEnabled = enabled },
        SourceKind.AntigravityCli => this with { AntigravityCliEnabled = enabled },
        _ => this,
    };

    public MonitorConfiguration ToMonitorConfiguration()
    {
        var enabled = Enum.GetValues<SourceKind>().Where(IsSourceEnabled).ToHashSet();
        return new MonitorConfiguration(enabled, CodexRoot, ClaudeRoot, DshRoot, OpenCodeRoot, GeminiRoot);
    }

    public AppSettings WithOrbAnchor(string monitorKey, OrbAnchor anchor)
    {
        ArgumentException.ThrowIfNullOrEmpty(monitorKey);
        var anchors = OrbAnchors.SetItem(monitorKey, Clamp(anchor));
        if (anchors.Count > MaxStoredMonitors)
        {
            var keep = anchors.Keys.Where(k => !string.Equals(k, monitorKey, StringComparison.OrdinalIgnoreCase))
                .Take(MaxStoredMonitors - 1)
                .Append(monitorKey)
                .ToArray();
            anchors = anchors.Where(kv => keep.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        }

        return this with { OrbMonitor = monitorKey, OrbAnchors = anchors };
    }

    /// <summary>Returns a copy with every value inside its valid range.</summary>
    public AppSettings Validated() => this with
    {
        Appearance = Enum.IsDefined(Appearance) ? Appearance : AppearanceMode.Auto,
        Language = Enum.IsDefined(Language) ? Language : AppLanguage.System,
        Metric = Enum.IsDefined(Metric) ? Metric : SpeedMetric.Output,
        RedlineOutput = HeatMath.ClampRedline(RedlineOutput),
        RedlineTotal = HeatMath.ClampRedlineTotal(RedlineTotal),
        BarMode = Enum.IsDefined(BarMode) ? BarMode : BarDisplayMode.GlyphAndValue,
        OrbSize = double.IsFinite(OrbSize) ? Math.Clamp(Math.Round(OrbSize), MinOrbSize, MaxOrbSize) : DefaultOrbSize,
        OrbOpacity = double.IsFinite(OrbOpacity) ? Math.Clamp(OrbOpacity, MinOrbOpacity, MaxOrbOpacity) : MaxOrbOpacity,
        Effects = Enum.IsDefined(Effects) ? Effects : EffectsIntensity.Standard,
        CodexRoot = CleanPath(CodexRoot),
        ClaudeRoot = CleanPath(ClaudeRoot),
        DshRoot = CleanPath(DshRoot),
        OpenCodeRoot = CleanPath(OpenCodeRoot),
        GeminiRoot = CleanPath(GeminiRoot),
        HistoryWindow = WindowFrame.ValidHistory(HistoryWindow),
        ChartSeconds = ChartMath.NormalizeRange(ChartSeconds),
        HeroLastTitle = VehicleArt.NormalizeKey(HeroLastTitle),
        OrbMonitor = string.IsNullOrWhiteSpace(OrbMonitor) || OrbMonitor.Length > 256 ? null : OrbMonitor,
        OrbAnchors = CleanAnchors(OrbAnchors),
        LastUpdateCheck = CleanTimestamp(LastUpdateCheck),
        SkippedVersion = CleanTag(SkippedVersion),
        NotifiedVersion = CleanTag(NotifiedVersion),
    };

    internal static string? CleanTag(string? tag) =>
        !string.IsNullOrWhiteSpace(tag) && SemanticVersion.TryParse(tag, out _) ? tag.Trim() : null;

    internal static DateTimeOffset? CleanTimestamp(DateTimeOffset? value) =>
        value is { } v && v.Year is >= 2000 and <= 9000 ? v.ToUniversalTime() : null;

    private static ImmutableDictionary<string, OrbAnchor> CleanAnchors(ImmutableDictionary<string, OrbAnchor> anchors)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, OrbAnchor>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, anchor) in anchors.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (builder.Count >= MaxStoredMonitors)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(key) && key.Length <= 256 && !builder.ContainsKey(key))
            {
                builder[key] = Clamp(anchor);
            }
        }

        return builder.ToImmutable();
    }

    internal static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (trimmed.Length > MaxPathLength || trimmed.IndexOf('\0') >= 0)
        {
            return null;
        }

        return Path.IsPathFullyQualified(trimmed) ? trimmed : null;
    }

    private static OrbAnchor Clamp(OrbAnchor anchor) => new(
        double.IsFinite(anchor.X) ? Math.Clamp(anchor.X, 0, 1) : 1,
        double.IsFinite(anchor.Y) ? Math.Clamp(anchor.Y, 0, 1) : 1);
}
