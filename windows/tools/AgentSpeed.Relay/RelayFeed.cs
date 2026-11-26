using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Remote;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Relay;

/// <summary>Projects the monitor's data onto the protocol's allowlist. No transcript records are serialized.</summary>
internal sealed class RelayFeed(RelaySession session, long today) : IResponseObserver
{
    private Dictionary<string, AgentInfo> _agents = new(StringComparer.Ordinal);
    private Dictionary<string, string> _parents = new(StringComparer.Ordinal);
    private SourceState[] _sources = [];
    private (long, int, bool)? _diagnostics;

    public long TodayStartMs { get; set; } = today;

    public void OnResponse(ParsedResponse response, bool isCompletion, bool seeded)
    {
        if (response.EndMs < Math.Min(TodayStartMs, RelaySession.NowMs - 30 * 60_000)
            || response.RequestCount is < 1 or > 100_000
            || Text(response.Key, RemoteLimits.KeyBytes) is not { Length: > 0 }
            || Text(response.AgentId, RemoteLimits.AgentIdBytes) is not { Length: > 0 }
            || !RemoteFields.IsTime(response.EndMs))
        {
            return;
        }

        session.Write(new
        {
            t = "response",
            r = new
            {
                key = response.Key,
                source = SourceNames.Of(response.Source),
                agent = response.AgentId,
                model = Text(response.Model, RemoteLimits.ModelBytes),
                startMs = WireTime(response.StartMs),
                endMs = response.EndMs,
                firstMs = WireTime(response.FirstMs),
                input = response.Tokens.Input,
                cachedInput = response.Tokens.CachedInput,
                cacheWriteInput = response.Tokens.CacheWriteInput,
                cacheWrite1hInput = response.Tokens.CacheWrite1hInput,
                output = response.Tokens.Output,
                reasoning = response.Tokens.Reasoning,
                requestCount = response.RequestCount,
                modes = Modes(response.Modes),
                final = response.Final,
                provider = Text(response.Provider, RemoteLimits.ProviderBytes),
                fast = response.Fast,
            },
            completion = isCompletion,
            project = Text(_agents.GetValueOrDefault(response.AgentId).Project, RemoteLimits.ProjectBytes) ?? string.Empty,
        });
    }

    public void Publish(MonitorCore monitor, long todayStartMs, bool ready = false)
    {
        var (snapshot, _) = monitor.BuildSnapshot(RelaySession.NowMs, todayStartMs, MonitorState.Running, observeLive: false);
        var next = new Dictionary<string, AgentInfo>(StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var parentBudget = RemoteLineFramer.MaxLineBytes - 32;
        foreach (var agent in snapshot.Agents.OrderByDescending(agent => agent.LastActivity).Take(RemoteLimits.Agents))
        {
            if (Text(agent.Id, RemoteLimits.AgentIdBytes) is not { Length: > 0 }
                || !monitor.EngineAgents.TryGetValue(agent.Id, out var info))
            {
                continue;
            }

            // JSON escaping uses at most six output bytes per UTF-16 code unit.
            if (Text(agent.ParentId, RemoteLimits.AgentIdBytes) is { Length: > 0 } parent && 6 * (agent.Id.Length + parent.Length) + 6 <= parentBudget)
            {
                parents.Add(agent.Id, parent);
                parentBudget -= 6 * (agent.Id.Length + parent.Length) + 6;
            }

            info = info with { Phase = agent.Phase };
            next.Add(agent.Id, info);
        }

        foreach (var id in _agents.Keys.Except(next.Keys))
        {
            session.Write(new { t = "agentRemoved", id });
        }

        foreach (var info in next.Values)
        {
            if (!_agents.TryGetValue(info.Id, out var previous) || previous != info)
            {
                WriteAgent(info);
            }
        }

        _agents = next;
        if (ready || parents.Count != _parents.Count || parents.Any(pair => _parents.GetValueOrDefault(pair.Key) != pair.Value))
        {
            session.Write(new { t = "parents", links = parents });
            _parents = parents;
        }

        if (ready)
        {
            session.Write(new { t = "ready" });
        }

        PublishStatus(monitor, snapshot);
    }

    private void WriteAgent(AgentInfo info)
    {
        session.Write(new
        {
            t = "agent",
            a = new
            {
                id = info.Id,
                source = SourceNames.Of(info.Source),
                project = Text(info.Project, RemoteLimits.ProjectBytes),
                model = Text(info.Model, RemoteLimits.ModelBytes),
                subagent = info.IsSubagent,
                phase = info.Phase switch
                {
                    AgentPhase.Generating => "generating",
                    AgentPhase.Tools => "tools",
                    _ => "idle",
                },
                inFlightStartMs = WireTime(info.InFlightStartMs),
                lastLineMs = WireTime(info.LastLineMs),
                modes = Modes(info.Modes),
                sessionModes = Modes(info.SessionModes),
                configuredModes = Modes(info.ConfiguredModes),
                actions = info.Work.Where(action => RemoteFields.IsTime(action.AtMs) && Text(action.Tool, RemoteLimits.ToolBytes) is { Length: > 0 })
                    .TakeLast(RemoteLimits.ActionsPerAgent).Select(Action),
            },
        });
    }

    private void PublishStatus(MonitorCore monitor, SpeedSnapshot snapshot)
    {
        var sources = snapshot.Sources.Where(source => source.Enabled).Select(source => new SourceState(
            source.Kind, source.Available, source.SessionCount, source.ConfiguredModes,
            snapshot.Warnings.OfType<MonitorWarning.UnsupportedFormat>().Any(warning => warning.Source == source.Kind)))
            .ToArray();
        var diagnostics = (monitor.MalformedLineCount, monitor.UnreadableFileCount, monitor.PartialHistory);
        if (_diagnostics == diagnostics && _sources.SequenceEqual(sources))
        {
            return;
        }

        _sources = sources;
        _diagnostics = diagnostics;
        session.Write(new
        {
            t = "status",
            sources = sources.Select(source => new
            {
                source = SourceNames.Of(source.Kind),
                available = source.Available,
                sessions = source.Sessions,
                unsupported = source.Unsupported,
                configuredModes = Modes(source.ConfiguredModes),
            }),
            malformed = diagnostics.MalformedLineCount,
            unreadable = diagnostics.UnreadableFileCount,
            partialHistory = diagnostics.PartialHistory,
        });
    }

    private static object Action(WorkAction action) => new
    {
        tool = action.Tool,
        digest = action.Digest.ToString("x16"),
        callMs = action.AtMs,
        resultMs = WireTime(action.ResultMs),
        failed = action.Failed,
        model = Text(action.Model, RemoteLimits.ModelBytes),
        seeded = action.Seeded,
    };

    private static long? WireTime(long? time) => time is { } value && RemoteFields.IsTime(value) ? value : null;

    private static string? Text(string? value, int maxBytes) =>
        value is not null && Encoding.UTF8.GetByteCount(value) <= maxBytes && RemoteFields.AllowsText(value, RemoteText.Model)
            ? value : null;

    private static IEnumerable<object> Modes(ExecutionModes modes) => modes.Facts.Select(fact => new
    {
        kind = ExecutionModes.Name(fact.Kind),
        value = fact.Value,
        rawValue = fact.RawValue,
        evidence = ExecutionModes.Name(fact.Evidence),
        provider = fact.Provider,
        scope = ExecutionModes.Name(fact.Scope),
    });

    private sealed record SourceState(SourceKind Kind, bool Available, int Sessions, ExecutionModes ConfiguredModes, bool Unsupported);
}
