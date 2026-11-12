using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// What the agents of a <c>character</c> orb are doing, for her moods (CHARACTERS 5, <i>Behavior</i>, <i>Inputs</i>): the
/// phase of the orb's set, when the request still waiting for its first output started, when the newest response ended,
/// and <c>trouble</c> events. Updated every engine tick from the orb's agents (METRICS 6.8, set A).
/// </summary>
public sealed class CharacterActivity
{
    internal const int Remembered = 512;

    private readonly Dictionary<string, DateTimeOffset> _failures = new(StringComparer.Ordinal);
    private Dictionary<string, bool> _looping = new(StringComparer.Ordinal);
    private bool _primed;

    public CharacterActivity(DateTimeOffset start)
    {
        IdleSince = start;
    }

    /// <summary>
    /// <see cref="AgentPhase.Generating"/> when an agent of the set is generating, else <see cref="AgentPhase.Tools"/> when
    /// one waits for a tool, else <see cref="AgentPhase.Idle"/> (the agents' phases already follow the 300 s staleness of
    /// METRICS 6.7).
    /// </summary>
    public AgentPhase Phase { get; private set; } = AgentPhase.Idle;

    /// <summary>
    /// While generating and none of the generating agents has output since its in-flight request started: the earliest
    /// such start; null otherwise.
    /// </summary>
    public DateTimeOffset? WaitingSince { get; private set; }

    /// <summary>The newest end of a response of the set; the app's start while there is none.</summary>
    public DateTimeOffset IdleSince { get; private set; }

    /// <summary>
    /// Counts <c>trouble</c>: an agent starts Looping (METRICS 6.10), or a tool result read live fails right after the
    /// previous result of that agent failed too.
    /// </summary>
    public int Troubles { get; private set; }

    /// <summary>Takes one engine tick's agents of the set; true when a <c>trouble</c> event fired.</summary>
    public bool Update(IReadOnlyList<AgentStatus> agents, DateTimeOffset? lastResponseEnd = null)
    {
        ArgumentNullException.ThrowIfNull(agents);
        Phase = agents.Any(a => a.Phase == AgentPhase.Generating) ? AgentPhase.Generating
            : agents.Any(a => a.Phase == AgentPhase.Tools) ? AgentPhase.Tools
            : AgentPhase.Idle;
        WaitingSince = Phase == AgentPhase.Generating ? Waiting(agents) : null;
        foreach (var agent in agents)
        {
            if (agent.LastResponseEnd is { } end)
            {
                NoteResponseEnd(end);
            }
        }

        if (lastResponseEnd is { } newest)
        {
            NoteResponseEnd(newest);
        }

        var fired = false;
        var looping = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            var loops = agent.Work.IsLooping;
            if (_primed && loops && !_looping.GetValueOrDefault(agent.Id))
            {
                fired = true;
            }

            looping[agent.Id] = loops;
            // The failure already met stays remembered, so an agent that leaves the list and returns never repeats it.
            if (agent.RepeatFailureAt is { } failure && !(_failures.TryGetValue(agent.Id, out var known) && known == failure))
            {
                fired |= _primed;
                _failures[agent.Id] = failure;
            }
        }

        _looping = looping;
        if (_failures.Count > Remembered)
        {
            var present = agents.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _failures.Keys.Where(id => !present.Contains(id)).ToList())
            {
                _failures.Remove(id);
            }
        }

        _primed = true;
        if (fired)
        {
            Troubles = unchecked(Troubles + 1);
        }

        return fired;
    }

    /// <summary>A response of the set ended (a response-completed event, or a newer end on a tick).</summary>
    public void NoteResponseEnd(DateTimeOffset end)
    {
        if (end > IdleSince)
        {
            IdleSince = end;
        }
    }

    private static DateTimeOffset? Waiting(IReadOnlyList<AgentStatus> agents)
    {
        DateTimeOffset? earliest = null;
        foreach (var agent in agents)
        {
            if (agent.Phase != AgentPhase.Generating)
            {
                continue;
            }

            if (agent.OutputSinceInFlight)
            {
                return null;
            }

            if (agent.InFlightSince is { } since && (earliest is null || since < earliest))
            {
                earliest = since;
            }
        }

        return earliest;
    }
}
