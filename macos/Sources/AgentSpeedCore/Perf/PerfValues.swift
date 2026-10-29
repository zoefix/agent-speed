import Foundation

/// The form of a value (METRICS 6.9 *Values*).
public enum PerfValueForm: String, Sendable, CaseIterable {
    case live, resting, none

    /// live > resting > none.
    var rank: Int {
        switch self {
        case .live: 2
        case .resting: 1
        case .none: 0
        }
    }
}

/// Rounded half away from zero (`int(value + 0.5)`): the addition happens in `Double` and the sum is truncated toward
/// zero. Non-finite or out-of-range values saturate.
public func perfRoundHalfAway(_ value: Double) -> Int {
    value >= 0 ? perfTruncated(value + 0.5) : -perfTruncated(-value + 0.5)
}

/// Python `int(float)`: truncation toward zero, saturated instead of raising.
func perfTruncated(_ value: Double) -> Int {
    guard value.isFinite else { return value > 0 ? Int.max : value < 0 ? Int.min : 0 }
    if value >= 9.223372036854775807e18 { return Int.max }
    if value <= -9.223372036854775808e18 { return Int.min }
    return Int(value)
}

/// The value of a set, a board group or one agent: the highest live score, else the highest resting score, else none
/// (METRICS 6.9 *Sets and groups*). `agentId` names the agent that supplied it; `provisional` follows that agent.
public struct PerfSetValue: Equatable, Sendable {
    public var form: PerfValueForm
    public var score: Double?
    public var agentId: String?
    /// The supplying agent's `lastLineMs`, kept so values merge.
    public var lastLineMs: Int64?
    /// The supplying agent's score rests on fewer than 10 timed requests.
    public var provisional: Bool

    public init(form: PerfValueForm, score: Double?, agentId: String?, lastLineMs: Int64?, provisional: Bool = false) {
        self.form = score == nil ? .none : form
        self.score = form == .none ? nil : score
        self.agentId = self.form == .none ? nil : agentId
        self.lastLineMs = self.form == .none ? nil : lastLineMs
        self.provisional = self.form == .none ? false : provisional
    }

    public static let empty = PerfSetValue(form: .none, score: nil, agentId: nil, lastLineMs: nil)

    /// As shown: rounded half away from zero; nil for none.
    public var shown: Int? { score.map(perfRoundHalfAway) }

    /// Ties on the score go to the latest `lastLineMs` (missing counts as 0), then to the smallest id (UTF-16
    /// ordinal); on a complete tie the first member wins.
    public static func highest(_ members: [PerfSetValue]) -> PerfSetValue {
        members.reduce(.empty) { $0.merged(with: $1) }
    }

    /// The value of the union of two disjoint sets: associative, so per-source values combine in any grouping.
    public mutating func merge(_ other: PerfSetValue) {
        self = merged(with: other)
    }

    public func merged(with other: PerfSetValue) -> PerfSetValue {
        other.supersedes(self) ? other : self
    }

    private func supersedes(_ other: PerfSetValue) -> Bool {
        if form.rank != other.form.rank { return form.rank > other.form.rank }
        guard form != .none, let score, let otherScore = other.score else { return false }
        if score != otherScore { return score > otherScore }
        let latest = lastLineMs ?? 0
        let otherLatest = other.lastLineMs ?? 0
        if latest != otherLatest { return latest > otherLatest }
        guard let agentId, let otherId = other.agentId else { return false }
        return AgentParents.ordinal(agentId, precedes: otherId)
    }
}

/// Board groups (DESIGN 2c, METRICS 6.9 *Sets and groups*): every root of a list (no parent, or a parent that is not
/// listed) with all its descendants, the root first and then depth first in list order.
public enum PerfGroups {
    public static func boardGroups(_ agents: [(id: String, parentId: String?)]) -> [(root: String, members: [String])] {
        let ids = Set(agents.map { OrdinalKey($0.id) })
        var children: [OrdinalKey: [String]] = [:]
        var roots: [String] = []
        for agent in agents {
            if let parent = agent.parentId, ids.contains(OrdinalKey(parent)), OrdinalKey(parent) != OrdinalKey(agent.id) {
                children[OrdinalKey(parent), default: []].append(agent.id)
            } else {
                roots.append(agent.id)
            }
        }
        return roots.map { root in
            var members: [String] = []
            var stack = [root]
            while let id = stack.popLast() {
                members.append(id)
                stack.append(contentsOf: (children[OrdinalKey(id)] ?? []).reversed())
            }
            return (root, members)
        }
    }
}

/// A string compared and hashed by its code points, as Python compares `str` (Swift's `String` equality follows
/// canonical equivalence instead).
struct OrdinalKey: Hashable, Sendable {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    static func == (lhs: OrdinalKey, rhs: OrdinalKey) -> Bool {
        lhs.text.utf8.elementsEqual(rhs.text.utf8)
    }

    func hash(into hasher: inout Hasher) {
        var copy = text
        copy.withUTF8 { hasher.combine(bytes: UnsafeRawBufferPointer($0)) }
        hasher.combine(0xFF as UInt8)
    }
}
