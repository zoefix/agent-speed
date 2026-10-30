import Foundation

/// Fact kinds, plus `speed`: a breakdown derived from the facts (EXECUTION-MODES, Reasoning effort and speed) that
/// is never a fact's own kind.
public enum ExecutionModeKind: String, CaseIterable, Codable, Sendable {
    case serviceTier, accelerator, modelVariant, routing, workflow, effort, speed

    /// The kinds of report extension version 1; version 2 adds `effort`. `speed` stays local.
    public static let reported: [ExecutionModeKind] = [.serviceTier, .accelerator, .modelVariant, .routing, .workflow]

    /// The kinds a report carries under the negotiated extension `version` (EXECUTION-MODES, *Report extension*).
    public static func reported(version: Int) -> [ExecutionModeKind] {
        switch version {
        case 1: reported
        case 2: reported + [.effort]
        default: []
        }
    }

    public var isFactKind: Bool { self != .speed }
}

public enum ExecutionModeEvidence: String, Codable, Sendable {
    case configured, selected, recorded, served

    /// What a response or an agent's own facts may be attributed from: a configured value never counts.
    static let attributable: Set<ExecutionModeEvidence> = [.selected, .recorded, .served]

    var rank: Int {
        switch self { case .configured: 0; case .selected: 1; case .recorded: 2; case .served: 3 }
    }
}

public enum ExecutionModeScope: String, Codable, Sendable {
    case configuration, session, turn, request, response

    var rank: Int {
        switch self { case .configuration: 0; case .session: 1; case .turn: 2; case .request: 3; case .response: 4 }
    }
}

public struct ExecutionModeFact: Equatable, Hashable, Codable, Sendable {
    public let kind: ExecutionModeKind
    public private(set) var value: String
    public let rawValue: String
    public let evidence: ExecutionModeEvidence
    public let provider: String?
    public let scope: ExecutionModeScope

    public init?(kind: ExecutionModeKind, rawValue: String, evidence: ExecutionModeEvidence,
                 provider: String? = nil, scope: ExecutionModeScope, openAI: Bool = false) {
        guard kind.isFactKind, Self.isIdentifier(rawValue, limit: 64),
              provider.map({ Self.isIdentifier($0, limit: 32) }) ?? true else { return nil }
        self.kind = kind
        self.rawValue = rawValue
        self.evidence = evidence
        self.provider = provider
        self.scope = scope
        let raw = rawValue.lowercased()
        if kind == .serviceTier, openAI || ["openai", "openai-codex", "azure"].contains(provider?.lowercased() ?? "") {
            value = raw == "default" ? "standard" : raw == "priority" ? "fast" : raw
        } else {
            value = raw
        }
    }

    public static func isIdentifier(_ text: String, limit: Int) -> Bool {
        let bytes = Array(text.utf8)
        func alphanumeric(_ b: UInt8) -> Bool { (48...57).contains(b) || (65...90).contains(b) || (97...122).contains(b) }
        guard !bytes.isEmpty, bytes.count <= limit, alphanumeric(bytes[0]) else { return false }
        return bytes.allSatisfy { alphanumeric($0) || [45, 46, 58, 95].contains($0) }
    }

    func withCanonicalValue(_ value: String) -> Self {
        var copy = self
        if Self.isIdentifier(value, limit: 64) { copy.value = value }
        return copy
    }

    public var isResolved: Bool { !["auto", "inherit", "inherited", "none", "unknown"].contains(value) }

    private enum CodingKeys: String, CodingKey { case kind, value, rawValue, evidence, provider, scope }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let kind = try c.decode(ExecutionModeKind.self, forKey: .kind)
        let value = try c.decode(String.self, forKey: .value)
        let raw = try c.decode(String.self, forKey: .rawValue)
        let evidence = try c.decode(ExecutionModeEvidence.self, forKey: .evidence)
        let provider = try c.decodeIfPresent(String.self, forKey: .provider)
        let scope = try c.decode(ExecutionModeScope.self, forKey: .scope)
        guard let fact = Self(kind: kind, rawValue: raw, evidence: evidence, provider: provider, scope: scope),
              Self.isIdentifier(value, limit: 64) else {
            throw DecodingError.dataCorruptedError(forKey: .rawValue, in: c, debugDescription: "Invalid mode identifier")
        }
        self = fact
        self.value = value
    }
}

public struct ExecutionModes: Equatable, Hashable, Codable, Sendable {
    public private(set) var facts: [ExecutionModeFact]
    public static let empty = ExecutionModes()

    public init(_ facts: [ExecutionModeFact] = []) {
        self.facts = []
        for fact in facts { insert(fact) }
    }

    public mutating func insert(_ fact: ExecutionModeFact) {
        facts.removeAll { $0.kind == fact.kind && $0.evidence == fact.evidence && $0.provider == fact.provider && $0.scope == fact.scope }
        facts.append(fact)
        if facts.count > 20 { facts.removeFirst(facts.count - 20) }
        facts.sort { Self.order($0) < Self.order($1) }
    }

    public func merging(_ other: ExecutionModes) -> ExecutionModes { ExecutionModes(facts + other.facts) }

    public func attribution(for kind: ExecutionModeKind) -> ExecutionModeAttribution {
        attribution(for: kind, among: ExecutionModeEvidence.attributable)
    }

    /// Served first, then recorded, then selected, among `evidence`; a tie that disagrees is unknown.
    func attribution(for kind: ExecutionModeKind, among evidence: Set<ExecutionModeEvidence>) -> ExecutionModeAttribution {
        guard kind.isFactKind else { return speed(among: evidence) }
        let candidates = facts.filter { $0.kind == kind && evidence.contains($0.evidence) }
        guard let best = candidates.max(by: {
            ($0.evidence.rank, $0.scope.rank) < ($1.evidence.rank, $1.scope.rank)
        }), best.isResolved else { return ExecutionModeAttribution(kind: kind) }
        let peers = candidates.filter { $0.evidence == best.evidence && $0.scope == best.scope }
        guard peers.allSatisfy({ $0.value == best.value && $0.provider == best.provider }) else {
            return ExecutionModeAttribution(kind: kind)
        }
        return ExecutionModeAttribution(kind: kind, mode: best.value, evidence: best.evidence.rawValue, provider: best.provider)
    }

    /// The service tier when resolved, else the accelerator, else a `highspeed` model variant.
    private func speed(among evidence: Set<ExecutionModeEvidence>) -> ExecutionModeAttribution {
        for kind in [ExecutionModeKind.serviceTier, .accelerator, .modelVariant] {
            let result = attribution(for: kind, among: evidence)
            if !result.isUnknown, kind != .modelVariant || result.mode == "highspeed" {
                return ExecutionModeAttribution(kind: .speed, mode: result.mode, evidence: result.evidence,
                                                provider: result.provider)
            }
        }
        return ExecutionModeAttribution(kind: .speed)
    }

    /// Known attributions of the newest report extension's kinds: the ledger freezes them all, and a report sends those
    /// of the version its service negotiated.
    public var reportedAttributions: [ExecutionModeAttribution] {
        ExecutionModeKind.reported(version: 2).map { attribution(for: $0) }.filter { !$0.isUnknown }
    }

    /// Known attributions of every local breakdown, `effort` and `speed` included.
    public var breakdownAttributions: [ExecutionModeAttribution] {
        ExecutionModeKind.allCases.map { attribution(for: $0) }.filter { !$0.isUnknown }
    }

    private static func order(_ fact: ExecutionModeFact) -> String {
        [fact.kind.rawValue, fact.evidence.rawValue, fact.provider ?? "", fact.scope.rawValue].joined(separator: "/")
    }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let facts = try c.decode([ExecutionModeFact].self, forKey: .facts)
        guard facts.count <= 20 else {
            throw DecodingError.dataCorruptedError(forKey: .facts, in: c, debugDescription: "Too many mode facts")
        }
        self.init(facts)
    }

    private enum CodingKeys: CodingKey { case facts }
}

public struct ExecutionModeAttribution: Equatable, Hashable, Codable, Sendable {
    public var kind: ExecutionModeKind
    public var mode: String
    public var evidence: String
    public var provider: String?
    public var isUnknown: Bool { mode == "unknown" }

    public init(kind: ExecutionModeKind, mode: String = "unknown", evidence: String = "unknown", provider: String? = nil) {
        self.kind = kind
        self.mode = mode
        self.evidence = evidence
        self.provider = provider
    }
}
