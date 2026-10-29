import CryptoKit
import Foundation

/// The first 16 hex digits of SHA-256 over a tool call's input as the file stores it (METRICS 6.10).
struct ToolDigest: Hashable, Sendable {
    let value: UInt64

    init(_ text: UnsafeRawBufferPointer) {
        var hasher = SHA256()
        hasher.update(bufferPointer: text)
        value = hasher.finalize().withUnsafeBytes { $0.prefix(8).reduce(0) { $0 << 8 | UInt64($1) } }
    }

    init?(hex: String) {
        guard hex.utf8.count == 16, let value = UInt64(hex, radix: 16) else { return nil }
        self.value = value
    }

    var hex: String {
        let digits = String(value, radix: 16)
        return String(repeating: "0", count: 16 - digits.count) + digits
    }

    /// An absent or `null` input hashes as empty text.
    static let empty = ToolDigest(UnsafeRawBufferPointer(start: nil, count: 0))
}

/// What a tool call did, without its input: the tool and the digest of the input.
struct ToolCall: Hashable, Sendable {
    var tool: String
    var digest: ToolDigest
}

/// One action of an agent (METRICS 6.10 *Storage*). Only these fields are kept, never a tool's input or output.
public struct ToolAction: Equatable, Sendable {
    var call: ToolCall
    public var callMs: Int64
    /// The time of the call's first result; nil until one is read.
    public var resultMs: Int64?
    /// nil while the outcome is unknown, which is not a failure.
    public var failed: Bool?
    /// The model of the response that issued the call.
    public var model: String?
    /// Read while seeding: the score uses it, the work signals do not.
    public var seeded: Bool

    init(call: ToolCall, callMs: Int64, resultMs: Int64? = nil, failed: Bool? = nil, model: String? = nil,
         seeded: Bool = false) {
        self.call = call
        self.callMs = callMs
        self.resultMs = resultMs
        self.failed = failed
        self.model = model
        self.seeded = seeded
    }

    public var tool: String { call.tool }
}

/// A call whose first result was read (METRICS 6.10, 6.11 *Completion order*, 11.10 *Counting*).
public struct ToolCompletion: Equatable, Sendable {
    public var callMs: Int64
    public var resultMs: Int64
    public var failed: Bool?
    public var model: String?
    public var seeded: Bool

    public init(callMs: Int64, resultMs: Int64, failed: Bool?, model: String?, seeded: Bool) {
        self.callMs = callMs
        self.resultMs = resultMs
        self.failed = failed
        self.model = model
        self.seeded = seeded
    }
}

/// What one line says about tools; a result finds its call by id.
enum ToolEvent: Equatable, Sendable {
    case call(id: String, ToolCall)
    case result(id: String, failed: Bool?)

    var isCall: Bool {
        if case .call = self { return true }
        return false
    }
}

/// The actions of one file (METRICS 6.10): the last `capacity` in file order, each call id once, and only the first
/// result of a call. Results of calls that left the stored list still complete; the history analysis, which reads
/// whole files, keeps only the completions.
struct ActionLog: Equatable, Sendable {
    static let capacity = 64
    /// Calls still waiting for a result, kept at most (the oldest go first).
    static let openLimit = 4096

    private(set) var actions: [ToolAction] = []
    /// False in the history analysis: no stored list, only completions.
    var storesActions = true
    /// Lines read while seeding add seeded actions.
    var marksSeeded = false
    /// The file position of `actions[0]`.
    private var firstOrdinal = 0
    /// Every call id of the file (hashed).
    private var knownIds: Set<UInt64> = []
    private var open: [UInt64: OpenCall] = [:]
    /// Calls whose first result was read, in order, until taken; `seeded` tells a result read while seeding.
    private var completions: [ToolCompletion] = []

    private struct OpenCall: Equatable, Sendable {
        var ordinal: Int
        var callMs: Int64
        var model: String?
    }

    /// The calls of an accepted line, with the model of the response that issued them.
    mutating func recordCalls(_ events: [ToolEvent], at timestampMs: Int64, model: String?) {
        for case .call(let id, let call) in events {
            let hash = Self.hash(id)
            guard knownIds.insert(hash).inserted else { continue }
            let ordinal = firstOrdinal + actions.count
            open[hash] = OpenCall(ordinal: ordinal, callMs: timestampMs, model: model)
            guard storesActions else {
                firstOrdinal += 1
                continue
            }
            actions.append(ToolAction(call: call, callMs: timestampMs, model: model, seeded: marksSeeded))
            if actions.count > Self.capacity {
                actions.removeFirst()
                firstOrdinal += 1
            }
            if open.count > Self.openLimit, let oldest = open.min(by: { $0.value.ordinal < $1.value.ordinal }) {
                open[oldest.key] = nil
            }
        }
    }

    /// The results of a line; a result whose id names no open call is ignored.
    mutating func recordResults(_ events: [ToolEvent], at timestampMs: Int64) {
        for case .result(let id, let failed) in events {
            guard let call = open.removeValue(forKey: Self.hash(id)) else { continue }
            let index = call.ordinal - firstOrdinal
            if index >= 0, index < actions.count {
                actions[index].resultMs = timestampMs
                actions[index].failed = failed
            }
            completions.append(ToolCompletion(callMs: call.callMs, resultMs: timestampMs, failed: failed,
                                              model: call.model, seeded: marksSeeded))
        }
    }

    /// Both, for a line whose calls count (calls and results never share a line).
    mutating func record(_ events: [ToolEvent], at timestampMs: Int64, model: String?) {
        guard !events.isEmpty else { return }
        recordCalls(events, at: timestampMs, model: model)
        recordResults(events, at: timestampMs)
    }

    /// The completions since the last call, oldest first.
    mutating func takeCompletions() -> [ToolCompletion] {
        guard !completions.isEmpty else { return [] }
        defer { completions.removeAll(keepingCapacity: true) }
        return completions
    }

    /// FNV-1a over the id's UTF-8 bytes.
    private static func hash(_ id: String) -> UInt64 {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in id.utf8 {
            hash ^= UInt64(byte)
            hash = hash &* 0x0000_0100_0000_01b3
        }
        return hash
    }
}

extension JSONScanner {
    /// The digest of a tool input: a string's text, any other value's raw JSON text; `null` hashes as absent.
    mutating func readToolDigest() throws(JSONScanError) -> ToolDigest {
        if try readNull() { return .empty }
        return try withStoredText(ToolDigest.init)
    }
}
