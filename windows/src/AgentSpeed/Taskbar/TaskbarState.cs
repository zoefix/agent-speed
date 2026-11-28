using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Taskbar;

/// <summary>
/// Everything the taskbar thread needs to draw, passed as an immutable snapshot from the UI thread. <see cref="Offset"/>
/// is the stored position (DIPs from the default spot next to the notification area; DESIGN 3, Position on the taskbar).
/// </summary>
internal sealed record TaskbarState(
    bool Enabled,
    double Value,
    SpeedTier Tier,
    double Redline,
    BarDisplayMode Mode,
    bool ColoredGlyph,
    bool ShowUnit,
    bool TaskbarDark,
    bool HighContrast,
    bool ReduceMotion,
    string UnitText,
    string AccessibleText,
    string? ValueText = null,
    bool ValueMuted = false,
    BarSecondary? Secondary = null,
    BarTrafficBlock? Traffic = null,
    BarIntelligenceBlock? Intelligence = null,
    BarQuotaBlock? Quota = null,
    double Offset = 0)
{
    public static TaskbarState Initial { get; } = new(
        false, 0, SpeedTier.Idle, HeatMath.DefaultRedline, BarDisplayMode.GlyphAndValue, true, true, true, false, false,
        "tok/s", "Agent Speed");

    /// <summary>True when a change affects the widget's size or layout (not just its content).</summary>
    public bool LayoutDiffers(TaskbarState other) =>
        Mode != other.Mode || ShowUnit != other.ShowUnit || UnitText != other.UnitText || Enabled != other.Enabled
        || (Secondary is null) != (other.Secondary is null) || Secondary?.Live.Unit != other.Secondary?.Live.Unit
        || Secondary?.Today.Unit != other.Secondary?.Today.Unit || Secondary?.Live.Label != other.Secondary?.Live.Label
        || (Traffic is null) != (other.Traffic is null) || Traffic?.Bits != other.Traffic?.Bits
        || (Intelligence is null) != (other.Intelligence is null) || Intelligence?.LanguageCode != other.Intelligence?.LanguageCode
        || (Quota is null) != (other.Quota is null) || Quota?.LanguageCode != other.Quota?.LanguageCode;
}

internal enum TaskbarPresence
{
    Hidden,
    Embedded,
    Floating,
    Unavailable,
}
