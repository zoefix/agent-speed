using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Boards.Live;

/// <summary>A board's score card: what it shows, and the agent it is for (a new agent under the same row crossfades).</summary>
public sealed record BoardScoreCard(string AgentId, PerfCardSubject Subject);

/// <summary>
/// A board's score cards (DESIGN 2c, Tooltip and score card; DESIGN 12, <i>Score card</i>) while its <c>intelligence</c> readout
/// is on: a group row's is the card of the member whose score the row shows (a group without one: its root's), a sub row's its
/// sub-agent's, and the hero's 性能 opens the supplying agent's card with <c>perf.card.top</c> from two scored agents (nothing
/// while none). Their facts are the row's former tooltip lines that the card's header does not already carry (project, source
/// and model): the agent's recorded effort and speed, then the row's speed. No card names a cost.
/// </summary>
public static class BoardScoreCards
{
    /// <summary>The hover key of the hero's 性能, beside the row keys (agent ids, <c>group#more</c>, <c>#rest</c>).</summary>
    public const string HeroKey = "#score";

    /// <summary>The card a hover key opens on <paramref name="tick"/>; null with <c>intelligence</c> off or when nothing shows it.</summary>
    public static BoardScoreCard? For(BoardTick tick, string key, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(key);
        if (!tick.Readouts.Intelligence)
        {
            return null;
        }

        if (string.Equals(key, HeroKey, StringComparison.Ordinal))
        {
            return Hero(tick, loc);
        }

        foreach (var entry in tick.Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return Row(entry, tick.Metric, loc);
            }
        }

        return null;
    }

    /// <summary>A group or sub row's card; null for the more lines.</summary>
    public static BoardScoreCard? Row(BoardEntry entry, SpeedMetric metric, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(loc);
        switch (entry.Kind)
        {
            case BoardEntryKind.Group:
                var group = entry.Group;
                var supplier = (entry.Readout?.Score ?? PerfSetReading.Group(group.Root, group.Subs)).Agent;
                var shown = supplier ?? group.Root;
                return new BoardScoreCard(shown.Id, PerfCardSubject.Of(group.Root, Facts(shown, BoardTooltip.SpeedLine(group, loc), loc),
                    supplier, group.Subs.Count));
            case BoardEntryKind.Sub when entry.Agent is { } agent:
                return new BoardScoreCard(agent.Id, PerfCardSubject.Of(agent, Facts(agent, SpeedLine(agent, metric, loc), loc)));
            default:
                return null;
        }
    }

    /// <summary>The hero's card: the agent that supplies the board's 性能, its eyebrow naming N; null while none.</summary>
    public static BoardScoreCard? Hero(BoardTick tick, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(loc);
        var score = tick.Gauge.Score;
        if (!score.HasScore || score.Agent is not { } agent)
        {
            return null;
        }

        return new BoardScoreCard(agent.Id, PerfCardSubject.Of(agent, Facts(agent, SpeedLine(agent, tick.Metric, loc), loc),
            topCount: score.Count));
    }

    /// <summary>
    /// A group row's extra content for screen readers (DESIGN 12, <i>Accessibility</i>: Windows puts it in the row's help text):
    /// its card's full description while <c>intelligence</c> is on, otherwise empty.
    /// </summary>
    public static string Description(BoardTick tick, BoardEntry entry, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(entry);
        return tick.Readouts.Intelligence && Row(entry, tick.Metric, loc) is { } card ? PerfCard.Build(card.Subject, loc).Description
            : string.Empty;
    }

    /// <summary>The recorded effort and speed lines of section 4 (none when neither is recorded), then the row's speed.</summary>
    public static IReadOnlyList<string> Facts(AgentStatus agent, string speed, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(loc);
        var capsule = EffortCapsule.Of(agent);
        return capsule.IsKnown ? [.. capsule.TooltipLines(loc), speed] : [speed];
    }

    private static string SpeedLine(AgentStatus agent, SpeedMetric metric, Localizer loc) =>
        loc.Format("win.boards.tip.speed", SpeedFormat.Speed(BoardGrouping.LiveOf(agent, metric)));
}
