import Foundation

/// Decodes one relay line (REMOTE 5.2) with every validation rule of REMOTE 5.6. Unknown types and unknown members
/// are ignored; anything else that does not fit makes the line invalid. Relay strings are only ever returned as
/// values: never used as a path, URL, command or format string.
struct RelayLineDecoder: Sendable {
    enum Outcome: Equatable {
        case message(RelayMessage)
        /// A type this version does not know.
        case ignored
        case invalid(RemoteLineError)
    }

    let hostId: String
    let prefix: String

    init(hostId: String) {
        self.hostId = hostId
        prefix = "ssh://\(hostId)/"
    }

    func decode(_ line: Data) -> Outcome {
        guard line.count <= RemoteLineFramer.maxLineBytes else { return .invalid(RemoteLineError(.oversized)) }
        let value: JSONValue
        do {
            value = try JSONParser.parse(line)
        } catch {
            return .invalid(RemoteLineError(.notJSON))
        }
        guard let members = value.object else { return .invalid(RemoteLineError(.notObject)) }
        let fields = RemoteFields(members)
        do {
            let type = try fields.string("t", maxBytes: 64)
            guard let message = try decode(type: type, fields, line: line) else { return .ignored }
            return .message(message)
        } catch {
            return .invalid(error)
        }
    }

    private func decode(type: String, _ f: RemoteFields, line: Data) throws(RemoteLineError) -> RelayMessage? {
        switch type {
        case "hello": return .hello(try hello(f))
        case "progress": return .progress(try f.fraction("value"))
        case "agent": return .agent(try agent(try f.object("a")))
        case "agentRemoved": return .agentRemoved(id: try agentId(f, "id"))
        case "parents": return .parents(try parents(f))
        case "response": return .response(try response(f))
        case "tool": return .tool(try tool(f))
        case "ready": return .ready
        case "status": return .status(try status(f))
        case "clock": return .clock(id: try f.integer("id"), nowMs: try f.time("now"))
        case "historyDay": return .historyDay(try historyDay(f, line: line))
        case "historyPerf": return .historyPerf(try historyPerf(f, line: line))
        case "historyDone": return .historyDone(days: try f.count("days"), analyzed: try f.count("analyzed"))
        case "historyProgress": return .historyProgress(try f.fraction("value"))
        case "error": return .error(try error(f))
        case "quota": return try quota(f)
        case "quotaRemoved": return try quotaRemoved(f)
        case "quotaBridge": return try quotaBridge(f)
        default: return nil
        }
    }

    // MARK: Small messages

    /// A relay of another protocol is still a `hello`: only its version is read, so the status can say why.
    private func hello(_ f: RemoteFields) throws(RemoteLineError) -> RelayHello {
        let version = try f.integer("protocol")
        guard version == RemoteProtocol.version else {
            return RelayHello(protocolVersion: version, version: "", arch: "", nowMs: 0)
        }
        return RelayHello(
            protocolVersion: version,
            version: try f.string("version", maxBytes: 64, .model),
            arch: try f.string("arch", maxBytes: 32, .model),
            nowMs: try f.time("now")
        )
    }

    /// A non-empty agent id of the host, prefixed (REMOTE 2.2).
    func agentId(_ f: RemoteFields, _ key: String) throws(RemoteLineError) -> String {
        prefix + (try relayAgentId(f, key))
    }

    func relayAgentId(_ f: RemoteFields, _ key: String) throws(RemoteLineError) -> String {
        let id = try f.string(key, maxBytes: RemoteLimits.agentIdBytes)
        guard !id.isEmpty else { throw RemoteLineError(.outOfRange, key) }
        return id
    }

    private func parents(_ f: RemoteFields) throws(RemoteLineError) -> [String: String] {
        let links = try f.objectMembers("links")
        guard links.count <= RemoteLimits.agents else { throw RemoteLineError(.tooMany, "links") }
        var map: [String: String] = [:]
        for (child, parent) in links {
            let parentId = try RemoteFields.checked(parent, "links.", maxBytes: RemoteLimits.agentIdBytes, .identifier)
            guard !child.isEmpty, !parentId.isEmpty, child.utf8.count <= RemoteLimits.agentIdBytes,
                  RemoteText.identifier.allows(child) else {
                throw RemoteLineError(.badCharacter, "links")
            }
            map[prefix + child] = prefix + parentId
        }
        return map
    }

    private func status(_ f: RemoteFields) throws(RemoteLineError) -> RemoteSourceReport {
        var sources: [RemoteSourceState] = []
        for (index, item) in try f.array("sources", limit: 64).enumerated() {
            let entry = try RemoteFields(item, path: "sources.\(index)")
            let key = try entry.string("source", maxBytes: 64)
            let available = try entry.bool("available")
            let sessions = try entry.count("sessions")
            let unsupported = try entry.bool("unsupported")
            let modes = try Self.modes(entry, "configuredModes")
            guard let source = SourceKind(rawValue: key) else { continue }
            sources.append(RemoteSourceState(source: source, available: available, sessions: sessions,
                                             unsupported: unsupported, configuredModes: modes))
        }
        return RemoteSourceReport(sources: sources, malformed: try f.count("malformed"),
                                  unreadable: try f.count("unreadable"), partialHistory: try f.bool("partialHistory"))
    }

    private func error(_ f: RemoteFields) throws(RemoteLineError) -> RelayError {
        let code = try f.string("code", maxBytes: 64, .model)
        let detail = try f.string("detail", maxBytes: RemoteLimits.errorDetailCharacters * 4, .model)
        guard detail.count <= RemoteLimits.errorDetailCharacters else { throw RemoteLineError(.tooLong, "detail") }
        let known: RelayError.Code? = switch code {
        case "badStart": .badStart
        case "historyFailed": .historyFailed
        case "internal": .internal
        default: nil
        }
        return RelayError(code: known ?? .other(code), detail: detail)
    }
}
