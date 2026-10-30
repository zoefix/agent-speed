using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Boards.Live;

/// <summary>
/// The text tooltip of a hovered group row while the board's <c>intelligence</c> readout is off (DESIGN 2c Interaction, after
/// 0.35 s): project and source, model, the root's effort and speed lines (as the dashboard's rows) and the group speed with its
/// sub-agents' share. It names no cost. With <c>intelligence</c> on, the row's score card takes its place
/// (<see cref="BoardScoreCards"/>).
/// </summary>
public static class BoardTooltip
{
    public const double DelaySeconds = 0.35;

    public static IReadOnlyList<string> Lines(BoardEntry entry, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Lines(entry.Group, loc);
    }

    public static IReadOnlyList<string> Lines(BoardGroup group, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(loc);
        var root = group.Root;
        var lines = new List<string>(6) { root.Project + " · " + OrbNaming.SourceName(root.Source, loc) };
        if (!string.IsNullOrWhiteSpace(root.Model))
        {
            lines.Add(root.Model);
        }

        lines.AddRange(EffortCapsule.Of(root).TooltipLines(loc));
        lines.Add(SpeedLine(group, loc));
        return lines;
    }

    /// <summary>The group speed, with its sub-agents' share when it has any.</summary>
    public static string SpeedLine(BoardGroup group, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(loc);
        return group.Subs.Count > 0
            ? loc.Format("win.boards.tip.speedSubs", SpeedFormat.Speed(group.Live), SpeedFormat.Speed(group.SubLive))
            : loc.Format("win.boards.tip.speed", SpeedFormat.Speed(group.Live));
    }
}
