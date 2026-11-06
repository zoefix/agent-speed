using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Remote;

internal sealed partial class RelayLineDecoder
{
    /// <summary>The agent object of REMOTE 5.4.</summary>
    public RemoteAgent DecodeAgent(RemoteFields f)
    {
        var relayId = RelayAgentId(f, "id");
        var phase = f.String("phase", 16) switch
        {
            "idle" => AgentPhase.Idle,
            "generating" => AgentPhase.Generating,
            "tools" => AgentPhase.Tools,
            _ => throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, "a.phase"),
        };
        var actions = new List<WorkAction>();
        var index = 0;
        foreach (var item in f.Array("actions", RemoteLimits.ActionsPerAgent).EnumerateArray())
        {
            actions.Add(Action(RemoteFields.Of(item, $"a.actions.{index++}")));
        }

        return new RemoteAgent(
            HostId,
            Prefix + relayId,
            relayId,
            f.Source("source"),
            f.OptionalString("project", RemoteLimits.ProjectBytes),
            f.OptionalString("cwd", RemoteLimits.CwdBytes),
            f.ModelString("model", RemoteLimits.ModelBytes),
            f.Bool("subagent"),
            phase,
            f.OptionalTime("inFlightStartMs"),
            f.OptionalTime("lastLineMs"),
            Modes(f, "modes"),
            Modes(f, "sessionModes"),
            Modes(f, "configuredModes"),
            actions);
    }

    /// <summary><c>{tool, digest, callMs, resultMs, failed, model, seeded}</c> (METRICS 6.10 <i>Storage</i>).</summary>
    public static WorkAction Action(RemoteFields f)
    {
        var tool = f.String("tool", RemoteLimits.ToolBytes);
        var digest = Convert.ToUInt64(f.Hex("digest", 16), 16);
        return new WorkAction(f.Time("callMs"), tool, digest, f.OptionalBool("failed"))
        {
            ResultMs = f.OptionalTime("resultMs"),
            Model = f.ModelString("model", RemoteLimits.ModelBytes),
            Seeded = f.Bool("seeded"),
        };
    }

    /// <summary>A list of at most 20 mode facts, each valid by EXECUTION-MODES.</summary>
    public static ExecutionModes Modes(RemoteFields f, string key)
    {
        var facts = new List<ExecutionModeFact>();
        var index = 0;
        foreach (var item in f.Array(key, RemoteLimits.FactsPerModeList).EnumerateArray())
        {
            facts.Add(Fact(RemoteFields.Of(item, $"{key}.{index++}")));
        }

        return facts.Count == 0 ? ExecutionModes.Empty : new ExecutionModes(facts);
    }

    public static ExecutionModeFact Fact(RemoteFields f)
    {
        var kind = ParseKind(f.String("kind", 32));
        var evidence = ParseEvidence(f.String("evidence", 32));
        var scope = ParseScope(f.String("scope", 32));
        var value = f.String("value", 64);
        var raw = f.String("rawValue", 64);
        var provider = f.OptionalString("provider", 32);
        if (kind is not { } k || evidence is not { } e || scope is not { } s || !ExecutionModes.IsIdentifier(value, 64)
            || ExecutionModeFact.Create(k, raw, e, provider, s) is not { } fact)
        {
            throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, "fact");
        }

        return fact with { Value = value };
    }

    public static ExecutionModeKind? ParseKind(string text) =>
        Enum.GetValues<ExecutionModeKind>().Cast<ExecutionModeKind?>().FirstOrDefault(k => ExecutionModes.Name(k!.Value) == text);

    private static ExecutionModeEvidence? ParseEvidence(string text) =>
        Enum.GetValues<ExecutionModeEvidence>().Cast<ExecutionModeEvidence?>().FirstOrDefault(e => ExecutionModes.Name(e!.Value) == text);

    private static ExecutionModeScope? ParseScope(string text) =>
        Enum.GetValues<ExecutionModeScope>().Cast<ExecutionModeScope?>().FirstOrDefault(s => ExecutionModes.Name(s!.Value) == text);

    /// <summary>A <c>response</c> line: the record of REMOTE 5.4 without a cost (the app prices it, METRICS 9.3).</summary>
    public RemoteResponse DecodeResponse(RemoteFields line)
    {
        var f = line.Object("r");
        var key = f.String("key", RemoteLimits.KeyBytes);
        if (key.Length == 0)
        {
            throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, "r.key");
        }

        var requestCount = f.Integer("requestCount");
        if (requestCount is < 1 or > 100_000)
        {
            throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, "r.requestCount");
        }

        var cacheWrite = f.Tokens("cacheWriteInput");
        var tokens = new TokenCounts(
            Input: f.Tokens("input"),
            CachedInput: f.Tokens("cachedInput"),
            CacheWriteInput: cacheWrite,
            CacheWrite1hInput: Math.Min(cacheWrite, f.Tokens("cacheWrite1hInput")),
            Output: f.Tokens("output"),
            Reasoning: f.Tokens("reasoning"));
        var record = new ParsedResponse(
            key,
            f.Source("source"),
            AgentId(f, "agent"),
            f.ModelString("model", RemoteLimits.ModelBytes),
            f.OptionalTime("startMs"),
            f.Time("endMs"),
            tokens,
            Final: f.Bool("final"),
            Provider: f.ModelString("provider", RemoteLimits.ProviderBytes),
            Fast: f.Bool("fast"),
            RequestCount: requestCount)
        {
            Modes = Modes(f, "modes"),
            FirstMs = f.OptionalTime("firstMs"),
            Host = HostId,
        };
        return new RemoteResponse(HostId, record, line.Bool("completion"), line.String("project", RemoteLimits.ProjectBytes));
    }

    /// <summary>A <c>tool</c> line (METRICS 11.10 <i>Counting</i>).</summary>
    public RemoteToolCompletion DecodeTool(RemoteFields f)
    {
        var agent = AgentId(f, "agent");
        var source = f.Source("source");
        var call = new WorkAction(f.Time("callMs"), string.Empty, 0, f.OptionalBool("failed"))
        {
            ResultMs = f.Time("resultMs"),
            Model = f.ModelString("model", RemoteLimits.ModelBytes),
            Seeded = f.Bool("seeded"),
        };
        return new RemoteToolCompletion(HostId, agent, source, call);
    }
}
