import Foundation
@testable import AgentSpeedCore

/// Remote hosts in a monitor session without ssh: events go through a `RemoteFeed` into a `TranscriptSession` with no
/// local roots (REMOTE 7).
enum RemoteLive {
    static let a = "8C0D6B0A-1111-4222-8333-00000000000A"
    static let b = "8C0D6B0A-1111-4222-8333-00000000000B"
    static let day: Int64 = 1_800_000_000_000
    static let now = day + 3_600_000

    static func agentId(_ host: String, _ name: String = "a") -> String {
        host == RemoteHost.localId ? "/local/\(name).jsonl" : "ssh://\(host)//home/u/.codex/sessions/\(name).jsonl"
    }

    static func record(_ key: String, host: String, agent: String = "a", source: SourceKind = .codex,
                       start: Int64, end: Int64, output: Int, cost: Int64? = nil, isFinal: Bool = true) -> ResponseRecord {
        ResponseRecord(key: key, source: source, agentId: agentId(host, agent), model: "gpt-5.5", startMs: start, endMs: end,
                       usage: TokenUsage(input: 100, output: output), isFinal: isFinal, costMicroUsd: cost, host: host)
    }

    static func agent(_ host: String, _ name: String = "a", source: SourceKind = .codex, phase: AgentPhase = .idle,
                      inFlightStartMs: Int64? = nil, lastLineMs: Int64 = now - 1000) -> RemoteAgent {
        RemoteAgent(hostId: host, id: agentId(host, name), relayId: "/home/u/.codex/sessions/\(name).jsonl", source: source,
                    project: "proj", cwdPath: "/home/u/proj", model: "gpt-5.5", isSubagent: false, phase: phase,
                    inFlightStartMs: inFlightStartMs, lastLineMs: lastLineMs, modes: .empty, sessionModes: .empty,
                    configuredModes: .empty, actions: [])
    }

    static func response(_ record: ResponseRecord, completion: Bool = true) -> RelayMessage {
        .response(RemoteResponse(hostId: record.host, record: record, isCompletion: completion, project: "proj"))
    }

    static func message(_ host: String, _ connection: Int, _ message: RelayMessage, seeded: Bool) -> RemoteHostEvent {
        RemoteHostEvent(hostId: host, connection: connection, kind: .message(message, seeded: seeded))
    }

    static func status(_ host: String, _ connection: Int, _ status: RemoteStatus) -> RemoteHostEvent {
        RemoteHostEvent(hostId: host, connection: connection, kind: .status(RemoteHostStatus(status: status)))
    }

    static let hello = RelayMessage.hello(RelayHello(protocolVersion: 1, version: "1.7.0", arch: "x86_64", nowMs: now))

    static func report(_ sources: [(SourceKind, Bool, Int)]) -> RelayMessage {
        .status(RemoteSourceReport(
            sources: sources.map { RemoteSourceState(source: $0.0, available: $0.1, sessions: $0.2, unsupported: false,
                                                     configuredModes: .empty) },
            malformed: 0, unreadable: 0, partialHistory: false))
    }

    /// A missing-roots session for codex and Claude Code, its clock pinned to `now`.
    static func session(_ directory: TempDirectory, sources: Set<SourceKind> = [.codex, .claudeCode]) -> TranscriptSession {
        let configuration = MonitorConfiguration(enabledSources: sources, codexRoot: directory.file("missing-codex"),
                                                 claudeRoot: directory.file("missing-claude"))
        return TranscriptSession(configuration: configuration, clock: { now })
    }

    static func feed() -> RemoteFeed {
        RemoteFeed(now: { Date(timeIntervalSince1970: TimeInterval(now) / 1000) },
                   todayStart: { _ in Date(timeIntervalSince1970: TimeInterval(day) / 1000) })
    }

    /// The connection's lines of a whole seeding: hello, the seeded lines, ready.
    static func seeding(_ host: String, connection: Int, _ lines: [RelayMessage]) -> [RemoteHostEvent] {
        [status(host, connection, .connecting), message(host, connection, hello, seeded: true),
         status(host, connection, .seeding(fraction: 0))]
            + lines.map { message(host, connection, $0, seeded: true) }
            + [message(host, connection, .ready, seeded: true), status(host, connection, .connected)]
    }
}

/// Applies the feed's changes to a session at once (the monitor would hop to its queue).
final class SessionSink: RemoteFeedSink, @unchecked Sendable {
    let session: TranscriptSession
    private(set) var batches = 0

    init(_ session: TranscriptSession) {
        self.session = session
    }

    func remoteFeed(_ changes: [RemoteFeedChange]) {
        batches += 1
        session.applyRemote(changes)
    }

    func advance(_ nowMs: Int64 = RemoteLive.now, subsets: Set<SubsetKey> = []) -> (output: EngineOutput, events: [SpeedEvent]) {
        session.advance(nowMs: nowMs, todayStartMs: RemoteLive.day, emitsEvents: true, subsets: subsets)
    }

    /// The first tick after seeding, which establishes today's peak without events (as the monitor does); the events
    /// returned are the pending ones of the applied lines.
    func establish(_ nowMs: Int64 = RemoteLive.now) -> (output: EngineOutput, events: [SpeedEvent]) {
        session.advance(nowMs: nowMs, todayStartMs: RemoteLive.day, emitsEvents: false)
    }

    static func completions(_ events: [SpeedEvent]) -> [ResponseSummary] {
        events.compactMap { event in
            if case .responseCompleted(let summary) = event { return summary }
            return nil
        }
    }

    func snapshot(_ output: EngineOutput) -> SpeedSnapshot {
        SnapshotBuilder.running(output, context: SnapshotContext(session: session, todayStartMs: RemoteLive.day))
    }
}
