using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core.Monitoring;

/// <summary>
/// The values an agent's own facts do not hold (EXECUTION-MODES, Current values): session-current facts in another program's
/// database and values in config files, checked at startup and at every rediscovery.
/// </summary>
internal sealed partial class MonitorCore
{
    private readonly ConfiguredValues _configured = new();
    private readonly Dictionary<string, SessionStore> _workBuddySessions = new(PathComparer);

    private ExecutionModes ConfiguredOf(TrackedFile tracked) =>
        _configured.Of(tracked.Parser.Source, tracked.Root.Path, tracked.Parser.AgentId, tracked.Parser.Model);

    /// <summary>Checks the files; every agent a change touches is pushed again. True when a value changed.</summary>
    private bool RefreshAgentContext()
    {
        var changed = _configured.Refresh();
        changed |= RefreshWorkBuddySessions();
        foreach (var tracked in _files.Values)
        {
            PushAgent(tracked);
        }

        return changed;
    }

    private bool RefreshWorkBuddySessions()
    {
        var read = _files.Values.Where(f => WorkBuddyPaths.IsWorkBuddy(f.Parser.Source) && f.Parser.LastLineMs is not null).ToList();
        var changed = false;
        foreach (var group in read.GroupBy(f => f.Root.Path, PathComparer))
        {
            if (!_workBuddySessions.TryGetValue(group.Key, out var store))
            {
                _workBuddySessions[group.Key] = store = WorkBuddyPaths.Sessions(group.Key);
            }

            changed |= store.Refresh([.. group.Select(f => WorkBuddyPaths.SessionId(f.Parser.AgentId))]);
            foreach (var tracked in group)
            {
                tracked.Parser.SessionModes = store.Of(WorkBuddyPaths.SessionId(tracked.Parser.AgentId));
            }
        }

        return changed;
    }
}
