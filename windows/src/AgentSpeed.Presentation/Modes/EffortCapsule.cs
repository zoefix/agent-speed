using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Modes;

/// <summary>How the meter and the word of an effort are colored (DESIGN 4, Agents).</summary>
public enum EffortTone
{
    /// <summary>Levels up to <c>xhigh</c> and tokens without a level: secondary label color.</summary>
    Neutral,

    /// <summary><c>max</c>: heat 0.82.</summary>
    Max,

    /// <summary><c>ultra</c> and <c>ultracode</c>: the heat 0.72 → 1.0 gradient, glow and shimmer.</summary>
    TopRung,

    /// <summary>A configured value, for every level: tertiary label color, no heat, glow or shimmer, since nothing confirms it.</summary>
    Configured,
}

/// <summary>
/// What one effort capsule draws (DESIGN 4, Agents): the effort's meter and word and the speed's bolts. Both tokens are
/// null when unknown; unknown is never drawn as a level or as Standard. A token only a config file holds is flagged:
/// a capsule of such tokens alone takes the configured form, and beside a real value it stays solid with that part tertiary.
/// </summary>
public sealed record EffortCapsule(string? Effort, string? Speed, bool EffortConfigured = false, bool SpeedConfigured = false)
{
    public const double Height = 18;
    public const double Padding = 6;
    public const double PartSpacing = 5;
    public const double BarWidth = 2.5;
    public const double BarSpacing = 1.5;
    public const double BarCornerRadius = 0.75;
    public const double MeterWidth = BarWidth * 5 + BarSpacing * 4;
    public const double WordFontSize = 11;

    /// <summary>The configured mark, <c>mode.configured.mark</c>, in the word's weight.</summary>
    public const double MarkFontSize = 9.5;

    /// <summary>The configured form's stroke: 0.75 DIP, dash 3, gap 2.5, round caps.</summary>
    public const double DashedStroke = 0.75;
    public const double DashLength = 3;
    public const double DashGap = 2.5;

    /// <summary>Segoe Fluent Icons Lightning at 9 DIP: one em wide.</summary>
    public const double BoltSize = 9;
    public const double BoltOverlap = 3;

    /// <summary>The rear bolt of <c>ultrafast</c>.</summary>
    public const double RearBoltOpacity = 0.6;

    /// <summary>Bar heights from the left, bottoms aligned.</summary>
    public static IReadOnlyList<double> BarHeights { get; } = [4, 5.5, 7, 8.5, 10];

    private static readonly string[] SpeedValueKeys = ["standard", "fast", "ultrafast", "highspeed"];

    public static EffortCapsule Of(ExecutionModes modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        return new(EffortAndSpeed.Effort(modes)?.Mode, EffortAndSpeed.Speed(modes)?.Mode);
    }

    /// <summary>The agent's current values (EXECUTION-MODES, Current values): session-current ones like any other, configured ones flagged.</summary>
    public static EffortCapsule Of(AgentStatus agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var effort = agent.Current(ExecutionModeKind.Effort);
        var speed = agent.Current(ExecutionModeKind.Speed);
        return new(effort.IsKnown ? effort.Mode : null, speed.IsKnown ? speed.Mode : null, effort.IsConfigured, speed.IsConfigured);
    }

    /// <summary>Lit bars 0–5; null when there is no meter (unknown effort, or a token without a level).</summary>
    public int? Level => Effort is { } effort ? EffortAndSpeed.Level(effort) : null;

    public bool IsTopRung => !EffortConfigured && Effort is { } effort && EffortAndSpeed.IsTopRung(effort);

    public EffortTone Tone => EffortConfigured ? EffortTone.Configured
        : IsTopRung ? EffortTone.TopRung : Effort == "max" ? EffortTone.Max : EffortTone.Neutral;

    /// <summary>Every known value is configured: the dashed form with its mark. A capsule with a real value stays solid.</summary>
    public bool IsConfigured => (EffortConfigured || SpeedConfigured) && (Effort is null || EffortConfigured) && (Speed is null || SpeedConfigured);

    public int Bolts => Speed is { } speed ? EffortAndSpeed.Bolts(speed) : 0;

    /// <summary>Effort or speed is known: the row carries the tooltip lines even when nothing is drawn.</summary>
    public bool IsKnown => Effort is not null || Speed is not null;

    /// <summary>Drawn for a known effort or a speed with bolts; never for unknown values or a standard speed.</summary>
    public bool IsVisible => Effort is not null || Bolts > 0;

    /// <summary>Compact capsules drop the word whole, unless the word is all the capsule would show.</summary>
    public bool ShowsWord(bool compact) => Effort is not null && (!compact || (Level is null && Bolts == 0));

    /// <summary>The mark follows the word and drops with it; the dashed stroke then carries the difference.</summary>
    public bool ShowsMark(bool compact) => IsConfigured && ShowsWord(compact);

    /// <summary>Intrinsic width; 0 when nothing is drawn. <paramref name="wordWidth"/> measures the word at 11 DIP medium.</summary>
    /// <param name="markWidth">The configured mark at 9.5 DIP medium.</param>
    public double Width(bool compact, Func<string, double> wordWidth, double markWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(wordWidth);
        var parts = 0;
        var width = 0.0;
        if (Level is not null)
        {
            width += MeterWidth;
            parts++;
        }

        if (ShowsWord(compact))
        {
            width += Math.Ceiling(wordWidth(Effort!));
            parts++;
        }

        if (ShowsMark(compact))
        {
            width += Math.Ceiling(markWidth);
            parts++;
        }

        if (Bolts > 0)
        {
            width += BoltsWidth(Bolts);
            parts++;
        }

        return parts == 0 ? 0 : Padding * 2 + width + PartSpacing * (parts - 1);
    }

    public static double BoltsWidth(int count) => count <= 0 ? 0 : BoltSize * count - BoltOverlap * (count - 1);

    /// <summary>
    /// <c>effort.tooltip</c> and <c>speed.tooltip</c> (their <c>.configured</c> forms for configured values), each value spelled
    /// as its token (<c>modes.unrecorded</c> when unknown), the top rung's explanation and one note for configured values.
    /// </summary>
    public IReadOnlyList<string> TooltipLines(Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var lines = new List<string>
        {
            loc.Format(EffortConfigured ? "effort.tooltip.configured" : "effort.tooltip", Effort ?? loc["modes.unrecorded"]),
            loc.Format(SpeedConfigured ? "speed.tooltip.configured" : "speed.tooltip", Speed is { } speed ? SpeedValue(speed, loc) : loc["modes.unrecorded"]),
        };
        if (IsTopRung)
        {
            lines.Add(loc[$"effort.{Effort}.detail"]);
        }

        if (EffortConfigured || SpeedConfigured)
        {
            lines.Add(loc["modes.configured.note"]);
        }

        return lines;
    }

    /// <summary>The end of a row's accessibility label: the effort and speed lines joined by <c>，</c> / <c>, </c>.</summary>
    public string AccessibilityText(Localizer loc) =>
        string.Join(loc.UsesLatinScript ? ", " : "，", TooltipLines(loc).Take(2));

    /// <summary>A speed token through <c>speed.value.*</c> when one exists, else the token itself.</summary>
    public static string SpeedValue(string speed, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return SpeedValueKeys.Contains(speed, StringComparer.Ordinal) ? loc["speed.value." + speed] : speed;
    }
}
