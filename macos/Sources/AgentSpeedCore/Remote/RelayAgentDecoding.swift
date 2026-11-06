import Foundation

extension RelayLineDecoder {
    /// The agent object of REMOTE 5.4.
    func agent(_ f: RemoteFields) throws(RemoteLineError) -> RemoteAgent {
        let relayId = try relayAgentId(f, "id")
        let phase = try f.string("phase", maxBytes: 16)
        guard let agentPhase = AgentPhase(rawValue: phase) else { throw RemoteLineError(.outOfRange, "a.phase") }
        var actions: [ToolAction] = []
        for (index, item) in try f.array("actions", limit: RemoteLimits.actionsPerAgent).enumerated() {
            actions.append(try Self.action(try RemoteFields(item, path: "a.actions.\(index)")))
        }
        return RemoteAgent(
            hostId: hostId,
            id: prefix + relayId,
            relayId: relayId,
            source: try f.source("source"),
            project: try f.optionalString("project", maxBytes: RemoteLimits.projectBytes),
            cwdPath: try f.optionalString("cwd", maxBytes: RemoteLimits.cwdBytes),
            model: try f.modelString("model", maxBytes: RemoteLimits.modelBytes),
            isSubagent: try f.bool("subagent"),
            phase: agentPhase,
            inFlightStartMs: try f.optionalTime("inFlightStartMs"),
            lastLineMs: try f.optionalTime("lastLineMs"),
            modes: try Self.modes(f, "modes"),
            sessionModes: try Self.modes(f, "sessionModes"),
            configuredModes: try Self.modes(f, "configuredModes"),
            actions: actions
        )
    }

    /// `{tool, digest, callMs, resultMs, failed, model, seeded}` (METRICS 6.10 *Storage*).
    static func action(_ f: RemoteFields) throws(RemoteLineError) -> ToolAction {
        let tool = try f.string("tool", maxBytes: RemoteLimits.toolBytes)
        let hex = try f.hex("digest", count: 16)
        guard let digest = ToolDigest(hex: hex) else { throw RemoteLineError(.outOfRange, "digest") }
        return ToolAction(
            call: ToolCall(tool: tool, digest: digest),
            callMs: try f.time("callMs"),
            resultMs: try f.optionalTime("resultMs"),
            failed: try f.optionalBool("failed"),
            model: try f.modelString("model", maxBytes: RemoteLimits.modelBytes),
            seeded: try f.bool("seeded")
        )
    }

    /// A list of at most 20 mode facts, each valid by EXECUTION-MODES (the rules of `ExecutionModeFact`'s decoding).
    static func modes(_ f: RemoteFields, _ key: String) throws(RemoteLineError) -> ExecutionModes {
        var facts: [ExecutionModeFact] = []
        for (index, item) in try f.array(key, limit: RemoteLimits.factsPerModeList).enumerated() {
            facts.append(try fact(try RemoteFields(item, path: "\(key).\(index)")))
        }
        return ExecutionModes(facts)
    }

    static func fact(_ f: RemoteFields) throws(RemoteLineError) -> ExecutionModeFact {
        let kind = ExecutionModeKind(rawValue: try f.string("kind", maxBytes: 32))
        let evidence = ExecutionModeEvidence(rawValue: try f.string("evidence", maxBytes: 32))
        let scope = ExecutionModeScope(rawValue: try f.string("scope", maxBytes: 32))
        let value = try f.string("value", maxBytes: 64)
        let raw = try f.string("rawValue", maxBytes: 64)
        let provider = try f.optionalString("provider", maxBytes: 32)
        guard let kind, let evidence, let scope,
              ExecutionModeFact.isIdentifier(value, limit: 64),
              let fact = ExecutionModeFact(kind: kind, rawValue: raw, evidence: evidence, provider: provider, scope: scope)
        else { throw RemoteLineError(.outOfRange, "fact") }
        return fact.withCanonicalValue(value)
    }
}

extension RemoteAgent {
    /// The engine's agent for this state; the project falls back to the relay id's folder name (display only).
    func engineAgent(parentId: String?) -> EngineAgent {
        var agent = EngineAgent(
            id: id,
            source: source,
            project: project ?? EngineAgent.fallbackProject(for: relayId),
            model: model,
            isSubagent: isSubagent,
            phase: phase,
            inFlightStartMs: inFlightStartMs,
            lastLineMs: lastLineMs,
            parentId: parentId,
            cwdPath: cwdPath,
            modes: modes
        )
        agent.host = hostId
        agent.sessionModes = sessionModes
        agent.configuredModes = configuredModes
        agent.actions = actions
        return agent
    }
}
