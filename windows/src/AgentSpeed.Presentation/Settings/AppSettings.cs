using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Chart;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Titles;
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

    private static readonly SourceKind[] AllSources = Enum.GetValues<SourceKind>();

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

    /// <summary>
    /// Where the Windows taskbar widget sits (DESIGN 3, Position on the taskbar): DIPs from its default spot next to the
    /// notification area toward the taskbar's start; 0 is the default spot. Set by dragging the widget.
    /// </summary>
    public double TaskbarOffset { get; init; }

    /// <summary>
    /// The taskbar's second value (DESIGN 3): with <see cref="CostSettings.LiveUnit"/> tokens, the live money rate after
    /// the value (the live value choice <c>settings.liveUnit.both</c>). Off by default; hidden in glyph-only mode.
    /// </summary>
    public bool BarSecondary { get; init; }

    /// <summary>
    /// <c>settings.bar.intelligence</c> (DESIGN 10, Menu bar and taskbar): the 智力 block after the value, the money block
    /// and the network block. Off by default; hidden in glyph-only mode; needs no network measuring.
    /// </summary>
    public bool BarIntelligence { get; init; }

    /// <summary>
    /// The single orb's visibility before multiple orbs (<c>orb.show</c>). Read and written back unchanged so a downgrade
    /// restores it; the app uses <see cref="ShowOrbs"/>. The same holds for the other <c>Orb*</c> values below.
    /// </summary>
    public bool ShowOrb { get; init; } = true;

    /// <summary>Master switch of all floating orbs (DESIGN 2b); files without it take <see cref="ShowOrb"/>.</summary>
    public bool ShowOrbs { get; init; } = true;

    /// <summary>Every floating orb and its positions per display arrangement (settings value <c>orbs</c>).</summary>
    public OrbsDocument Orbs { get; init; } = OrbsDocument.ProcessDefault;

    /// <summary>The leaderboard boards and their positions (settings value <c>boards</c>, DESIGN 2c); none by default.</summary>
    public BoardsDocument Boards { get; init; } = BoardsDocument.Empty;

    /// <summary>Name plates under the orbs (<c>orbNames</c>, DESIGN 2b Name plate); an orb's <c>showName</c> overrides it.</summary>
    public OrbNamesMode OrbNames { get; init; } = OrbNamesMode.Auto;

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

    public bool OmpEnabled { get; init; } = true;

    public bool AntigravityEnabled { get; init; } = true;

    public bool WorkBuddyEnabled { get; init; } = true;

    public bool WorkBuddyAiEnabled { get; init; } = true;

    public bool ZCodeEnabled { get; init; } = true;

    public bool KimiEnabled { get; init; } = true;

    public bool GrokBuildEnabled { get; init; } = true;

    public bool PiEnabled { get; init; } = true;

    public bool OpenClawEnabled { get; init; } = true;

    public bool HermesEnabled { get; init; } = true;

    public bool DroidEnabled { get; init; } = true;
    public bool TraeEnabled { get; init; } = true;

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

    /// <summary>One omp <c>sessions</c> folder; null reads the default root and every named profile.</summary>
    public string? OmpRoot { get; init; }

    /// <summary>One Antigravity store folder (or its <c>conversations</c> folder); null reads the three default stores.</summary>
    public string? AntigravityRoot { get; init; }

    /// <summary>The WorkBuddy data folder (or its <c>projects</c> folder); null uses <c>~/.workbuddy</c>.</summary>
    public string? WorkBuddyRoot { get; init; }

    /// <summary>The WorkBuddy AI data folder (or its <c>projects</c> folder); null uses <c>~/.workbuddy-ai</c>.</summary>
    public string? WorkBuddyAiRoot { get; init; }

    public string? ZCodeRoot { get; init; }

    public string? KimiRoot { get; init; }

    public string? GrokBuildRoot { get; init; }

    /// <summary>One Pi sessions folder; null reads Pi's default root and the custom session folder of its settings.</summary>
    public string? PiRoot { get; init; }

    /// <summary>One OpenClaw state folder, agent folder or database; null reads every automatic state folder.</summary>
    public string? OpenClawRoot { get; init; }

    /// <summary>A Hermes home or one <c>logs</c> folder; null reads the automatic Hermes home and its profiles.</summary>
    public string? HermesRoot { get; init; }

    /// <summary>The Factory Droid <c>.factory</c> folder or its <c>sessions</c> folder; null uses <c>~/.factory/sessions</c>.</summary>
    public string? DroidRoot { get; init; }
    public string? TraeRoot { get; init; }

    public int ChartSeconds { get; init; } = 120;

    /// <summary>The dashboard shows every agent instead of the collapsed list.</summary>
    public bool AgentsExpanded { get; init; }

    /// <summary>
    /// Vehicle key of the last non-idle speed title (for example <c>sportsCar</c>); the dashboard hero keeps that vehicle
    /// parked while idle, also across relaunches (DESIGN 4c "Idle"). Null shows the walker.
    /// </summary>
    public string? HeroLastTitle { get; init; }

    /// <summary>The General title theme and the custom title sets (settings value <c>titleThemes</c>, DESIGN 1).</summary>
    public TitleThemesDocument TitleThemes { get; init; } = TitleThemesDocument.Default;

    /// <summary>Last frame of the Usage History window; null opens it at its default size, centered.</summary>
    public WindowFrame? HistoryWindow { get; init; }

    /// <summary>The history's effort and speed section shows this kind (DESIGN 4b, 3b): effort or speed.</summary>
    public ExecutionModeKind HistoryModeKind { get; init; } = ExecutionModeKind.Effort;

    /// <summary>The two sources the history's task performance compares (DESIGN 12); null takes the default pair.</summary>
    public History.Tasks.TaskPair? HistoryTaskPair { get; init; }

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

    /// <summary>NETWORK 1 master switch: off, nothing is ever requested (updates, prices, rates).</summary>
    public bool NetworkEnabled { get; init; } = true;

    /// <summary>Currency, live unit, subscriptions and the price-table sync (DESIGN 9).</summary>
    public CostSettings Cost { get; init; } = CostSettings.Default;

    /// <summary>Network traffic measuring and display (DESIGN 10).</summary>
    public TrafficSettings Traffic { get; init; } = TrafficSettings.Default;

    /// <summary>Live reporting (DESIGN 11, Storage): settings and service identities, never keys.</summary>
    public ReportPreferences Report { get; init; } = ReportPreferences.Default;

    public bool IsSourceEnabled(SourceKind kind) => kind switch
    {
        SourceKind.Codex => CodexEnabled,
        SourceKind.ClaudeCode => ClaudeCodeEnabled,
        SourceKind.ClaudeDesktop => ClaudeDesktopEnabled,
        SourceKind.DeepSeek => DeepSeekEnabled,
        SourceKind.OpenCode => OpenCodeEnabled,
        SourceKind.Gemini => GeminiEnabled,
        SourceKind.Omp => OmpEnabled,
        SourceKind.Antigravity => AntigravityEnabled,
        SourceKind.WorkBuddy => WorkBuddyEnabled,
        SourceKind.WorkBuddyAi => WorkBuddyAiEnabled,
        SourceKind.ZCode => ZCodeEnabled,
        SourceKind.Kimi => KimiEnabled,
        SourceKind.GrokBuild => GrokBuildEnabled,
        SourceKind.Pi => PiEnabled,
        SourceKind.OpenClaw => OpenClawEnabled,
        SourceKind.Hermes => HermesEnabled,
        SourceKind.Droid => DroidEnabled,
        SourceKind.Trae => TraeEnabled,
        _ => false,
    };

    /// <summary>The sources turned on in Settings → Data Sources.</summary>
    public SourceSet EnabledSourceSet()
    {
        var set = SourceSet.Empty;
        foreach (var kind in AllSources)
        {
            if (IsSourceEnabled(kind))
            {
                set = set.With(kind);
            }
        }

        return set;
    }

    public AppSettings WithSourceEnabled(SourceKind kind, bool enabled) => kind switch
    {
        SourceKind.Codex => this with { CodexEnabled = enabled },
        SourceKind.ClaudeCode => this with { ClaudeCodeEnabled = enabled },
        SourceKind.ClaudeDesktop => this with { ClaudeDesktopEnabled = enabled },
        SourceKind.DeepSeek => this with { DeepSeekEnabled = enabled },
        SourceKind.OpenCode => this with { OpenCodeEnabled = enabled },
        SourceKind.Gemini => this with { GeminiEnabled = enabled },
        SourceKind.Omp => this with { OmpEnabled = enabled },
        SourceKind.Antigravity => this with { AntigravityEnabled = enabled },
        SourceKind.WorkBuddy => this with { WorkBuddyEnabled = enabled },
        SourceKind.WorkBuddyAi => this with { WorkBuddyAiEnabled = enabled },
        SourceKind.ZCode => this with { ZCodeEnabled = enabled },
        SourceKind.Kimi => this with { KimiEnabled = enabled },
        SourceKind.GrokBuild => this with { GrokBuildEnabled = enabled },
        SourceKind.Pi => this with { PiEnabled = enabled },
        SourceKind.OpenClaw => this with { OpenClawEnabled = enabled },
        SourceKind.Hermes => this with { HermesEnabled = enabled },
        SourceKind.Droid => this with { DroidEnabled = enabled },
        SourceKind.Trae => this with { TraeEnabled = enabled },
        _ => this,
    };

    public MonitorConfiguration ToMonitorConfiguration()
    {
        var enabled = Enum.GetValues<SourceKind>().Where(IsSourceEnabled).ToHashSet();
        return new MonitorConfiguration(enabled, CodexRoot, ClaudeRoot, DshRoot, OpenCodeRoot, GeminiRoot, OmpRoot, AntigravityRoot,
            WorkBuddyRoot: WorkBuddyRoot, WorkBuddyAiRoot: WorkBuddyAiRoot,
            ZCodeRoot: ZCodeRoot, KimiRoot: KimiRoot, GrokBuildRoot: GrokBuildRoot, PiRoot: PiRoot,
            OpenClawRoot: OpenClawRoot, HermesRoot: HermesRoot, DroidRoot: DroidRoot, TraeRoot: TraeRoot);
    }

    /// <summary>The master switch; turning it on while every orb is individually hidden makes them all visible.</summary>
    public AppSettings WithShowOrbs(bool show) => show
        ? this with { ShowOrbs = true, Orbs = OrbEditing.ShowAll(Orbs ?? OrbsDocument.ProcessDefault) }
        : this with { ShowOrbs = false };

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
        TaskbarOffset = TaskbarPosition.Normalize(TaskbarOffset),
        OrbSize = double.IsFinite(OrbSize) ? Math.Clamp(Math.Round(OrbSize), MinOrbSize, MaxOrbSize) : DefaultOrbSize,
        OrbOpacity = double.IsFinite(OrbOpacity) ? Math.Clamp(OrbOpacity, MinOrbOpacity, MaxOrbOpacity) : MaxOrbOpacity,
        Effects = Enum.IsDefined(Effects) ? Effects : EffectsIntensity.Standard,
        CodexRoot = CleanPath(CodexRoot),
        ClaudeRoot = CleanPath(ClaudeRoot),
        DshRoot = CleanPath(DshRoot),
        OpenCodeRoot = CleanPath(OpenCodeRoot),
        GeminiRoot = CleanPath(GeminiRoot),
        OmpRoot = CleanPath(OmpRoot),
        AntigravityRoot = CleanPath(AntigravityRoot),
        WorkBuddyRoot = CleanPath(WorkBuddyRoot),
        WorkBuddyAiRoot = CleanPath(WorkBuddyAiRoot),
        ZCodeRoot = CleanPath(ZCodeRoot),
        KimiRoot = CleanPath(KimiRoot),
        GrokBuildRoot = CleanPath(GrokBuildRoot),
        PiRoot = CleanPath(PiRoot),
        OpenClawRoot = CleanPath(OpenClawRoot),
        HermesRoot = CleanPath(HermesRoot),
        DroidRoot = CleanPath(DroidRoot),
        TraeRoot = CleanPath(TraeRoot),
        HistoryWindow = WindowFrame.ValidHistory(HistoryWindow),
        HistoryModeKind = HistoryModeKind == ExecutionModeKind.Speed ? ExecutionModeKind.Speed : ExecutionModeKind.Effort,
        HistoryTaskPair = HistoryTaskPair is { IsValid: true } pair ? pair : null,
        ChartSeconds = ChartMath.NormalizeRange(ChartSeconds),
        HeroLastTitle = VehicleArt.NormalizeKey(HeroLastTitle),
        TitleThemes = (TitleThemes ?? TitleThemesDocument.Default).Validated(),
        OrbMonitor = string.IsNullOrWhiteSpace(OrbMonitor) || OrbMonitor.Length > 256 ? null : OrbMonitor,
        OrbAnchors = CleanAnchors(OrbAnchors),
        Orbs = (Orbs ?? OrbsDocument.ProcessDefault).Validated(),
        Boards = (Boards ?? BoardsDocument.Empty).Validated(),
        OrbNames = Enum.IsDefined(OrbNames) ? OrbNames : OrbNamesMode.Auto,
        LastUpdateCheck = CleanTimestamp(LastUpdateCheck),
        SkippedVersion = CleanTag(SkippedVersion),
        NotifiedVersion = CleanTag(NotifiedVersion),
        Cost = (Cost ?? CostSettings.Default).Validated(),
        Traffic = (Traffic ?? TrafficSettings.Default).Validated(),
        Report = (Report ?? ReportPreferences.Default).Validated(),
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

        // Unchanged anchors keep their instance, so equal settings stay equal (SettingsService skips no-op updates).
        return ReferenceEquals(anchors.KeyComparer, builder.KeyComparer) && builder.Count == anchors.Count
            && builder.All(kv => anchors.TryGetValue(kv.Key, out var old) && old == kv.Value)
            ? anchors
            : builder.ToImmutable();
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
