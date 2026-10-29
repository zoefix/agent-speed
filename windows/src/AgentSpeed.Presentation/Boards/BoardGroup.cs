using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;

namespace AgentSpeed.Presentation.Boards;

/// <summary>
/// A root agent and all its listed descendants (DESIGN 2c, Ranking): what one ranking row stands for.
/// </summary>
/// <param name="Id">The root agent's id.</param>
/// <param name="Subs">Every descendant, by live value (descending), latest activity (descending), id (ordinal).</param>
/// <param name="Live">The group speed: the sum of every member's live value in the selected metric.</param>
/// <param name="SubLive">The descendants' share of <paramref name="Live"/>.</param>
/// <param name="LastActiveMs">The latest activity of any member (Unix ms).</param>
/// <param name="Generating">Members in phase generating.</param>
/// <param name="Tools">Members in phase tools.</param>
public sealed record BoardGroup(
    string Id,
    AgentStatus Root,
    IReadOnlyList<AgentStatus> Subs,
    double Live,
    double SubLive,
    long LastActiveMs,
    int Generating,
    int Tools)
{
    public BoardRankInput RankInput => new(Id, Live, LastActiveMs);

    /// <summary>The row's 性能 (METRICS 6.9, <i>Sets and groups</i>): the highest member score, live before resting, else none.</summary>
    public AgentIntelligence Intelligence { get; init; }

    /// <summary>The root's phase badge shows generating while any member generates (the prototypes' group badge).</summary>
    public AgentPhase BadgePhase => Generating > 0 ? AgentPhase.Generating : Root.Phase;
}
