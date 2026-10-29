import Foundation

/// One counted completion as the engine publishes it (METRICS 11.10 *Counting*): a measured response's or a tool
/// call's numbers under the resolved id of its model and its agent's source, with the watch epoch it counted in.
public struct ReportPerfCompletion: Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        /// `startMs` as logged (the latency's start); the duration uses the 500 ms rule.
        case response(startMs: Int64, endMs: Int64, firstMs: Int64?, output: Int, reasoning: Int, input: Int,
                      cachedInput: Int)
        case call(callMs: Int64, resultMs: Int64, failed: Bool?)
    }

    /// Position in the engine's log, from 1.
    public var index: Int
    public var epoch: Int
    public var agentId: String
    public var modelId: String
    public var source: SourceKind
    public var kind: Kind

    public init(index: Int, epoch: Int, agentId: String, modelId: String, source: SourceKind, kind: Kind) {
        self.index = index
        self.epoch = epoch
        self.agentId = agentId
        self.modelId = modelId
        self.source = source
        self.kind = kind
    }

    /// The interval the online union rule covers.
    var interval: (start: Int64, end: Int64) {
        switch kind {
        case .response(let start, let end, _, _, _, _, _): (min(start, end - ResponseTiming.minDurationMs), end)
        case .call(let call, let result, _): (call, result)
        }
    }

    /// What it adds to a statistics row, activeMs aside.
    var row: PerfRecord {
        var row = PerfRecord()
        switch kind {
        case let .response(start, end, first, output, reasoning, input, cached):
            let record = ResponseRecord(key: "", source: source, agentId: agentId, model: nil, startMs: start, endMs: end,
                                        usage: TokenUsage(input: input, cachedInput: cached, output: output,
                                                          reasoning: reasoning),
                                        firstMs: first)
            if let timed = TimedResponse(record) { row.addResponse(timed, source: source) }
        case let .call(call, result, failed):
            row.addCall(durationMs: result - call, failed: failed)
        }
        return row
    }
}

/// The engine's recent counted completions. Snapshots carry the latest ones, so a reader that missed a snapshot still
/// takes each completion exactly once (by `index`); `id` tells one engine's log from the next one's.
public struct ReportPerfCompletionLog: Equatable, Sendable {
    public static let capacity = 4096

    public let id: UInt64
    public private(set) var entries: [ReportPerfCompletion] = []
    /// Completions appended so far: the last entry's index.
    public private(set) var count = 0

    public init(id: UInt64 = UInt64.random(in: 1...UInt64.max)) {
        self.id = id
    }

    public mutating func append(epoch: Int, agentId: String, modelId: String, source: SourceKind,
                                kind: ReportPerfCompletion.Kind) {
        count += 1
        entries.append(ReportPerfCompletion(index: count, epoch: epoch, agentId: agentId, modelId: modelId,
                                            source: source, kind: kind))
        if entries.count > Self.capacity { entries.removeFirst(entries.count - Self.capacity) }
    }

    /// The entries after `index`, oldest first.
    public func entries(after index: Int) -> ArraySlice<ReportPerfCompletion> {
        guard let first = entries.firstIndex(where: { $0.index > index }) else { return [] }
        return entries[first...]
    }
}

/// Statistics rows per resolved model id and source (METRICS 11.10 *Statistics*).
public typealias ReportPerfStats = [String: [SourceKind: PerfRecord]]

/// The statistics ledger of METRICS 11.10: the rows counted since the previous build, with the online union rule for
/// activeMs (one `coveredUntil` per agent and resolved id, kept until a relaunch). Never persisted.
public struct ReportPerfLedger: Equatable, Sendable {
    public static let maxResponses = 100_000
    public static let maxCalls = 100_000

    public private(set) var rows: ReportPerfStats = [:]
    private var covered: [CoverKey: Int64] = [:]
    public private(set) var responses = 0
    public private(set) var calls = 0

    private struct CoverKey: Hashable, Sendable {
        var agent: String
        var model: String
    }

    public init() {}

    public var isEmpty: Bool { rows.isEmpty }

    /// Counts one completion; false when the ledger already holds 100 000 of its kind (it is dropped).
    @discardableResult
    public mutating func add(_ completion: ReportPerfCompletion) -> Bool {
        switch completion.kind {
        case .response:
            guard responses < Self.maxResponses else { return false }
            responses += 1
        case .call:
            guard calls < Self.maxCalls else { return false }
            calls += 1
        }
        var row = completion.row
        let (start, end) = completion.interval
        let key = CoverKey(agent: completion.agentId, model: completion.modelId)
        let previous = covered[key]
        row.activeMs = max(0, end - (previous.map { max(start, $0) } ?? start))
        covered[key] = previous.map { max($0, end) } ?? end
        rows[completion.modelId, default: [:]][completion.source, default: PerfRecord()].add(row)
        return true
    }

    /// A build: the rows, and an empty ledger (the union keeps its place).
    public mutating func take() -> ReportPerfStats {
        defer { clear() }
        return rows
    }

    /// A break.
    public mutating func clear() {
        rows = [:]
        responses = 0
        calls = 0
    }
}

/// The reporting side of METRICS 11.10: the watch, as an epoch the monitor counts completions in; the completions taken
/// from the engine's log once each; and the statistics ledger, which only a build takes and only a break empties.
public struct ReportPerfTracker: Equatable, Sendable {
    public private(set) var ledger = ReportPerfLedger()
    /// The current watch's epoch; nil while the watch is off.
    public private(set) var epoch: Int?
    private var lastEpoch = 0
    /// What the watch is on for (the service); a change is a break followed by a new watch.
    public private(set) var watchKey: String?
    private var logId: UInt64?
    private var lastIndex = 0

    public init() {}

    /// The watch at a tick: `key` names the service it is on for, nil while it is off. Turning off, or on for another
    /// service, empties the ledger (a break). Returns true when the epoch changed, which the monitor must learn.
    @discardableResult
    public mutating func watch(_ key: String?) -> Bool {
        guard key != watchKey else { return false }
        if watchKey != nil { ledger.clear() }
        watchKey = key
        if key != nil {
            lastEpoch += 1
            epoch = lastEpoch
        } else {
            epoch = nil
        }
        return true
    }

    /// Takes the completions the log gained since the last call, those of the current watch only; true when the ledger
    /// received one (the changed rule of METRICS 11.10 *WebSocket*).
    @discardableResult
    public mutating func ingest(_ log: ReportPerfCompletionLog) -> Bool {
        if log.id != logId {
            logId = log.id
            lastIndex = 0
        }
        var received = false
        for entry in log.entries(after: lastIndex) {
            lastIndex = entry.index
            guard let epoch, entry.epoch == epoch else { continue }
            if ledger.add(entry) { received = true }
        }
        return received
    }

    /// A build with the extension active: the rows, and an empty ledger.
    public mutating func take() -> ReportPerfStats {
        ledger.take()
    }

    /// The rows the next build takes (the preview shows them without taking them).
    public var pending: ReportPerfStats { ledger.rows }
}

extension PerfRecord {
    /// A `perfStats` row as sent: sparse latency buckets in ascending order, `toolLnSum` to 6 decimals.
    func reportJSON(source: SourceKind) -> ReportJSON {
        let bins: [ReportJSON] = latencyBins.enumerated().compactMap { k, count in
            count > 0 ? .array([.int(Int64(k)), .int(Int64(count))]) : nil
        }
        return .object([
            .init("source", .string(source.rawValue)), .init("n", .int(Int64(n))), .init("outputSum", .int(outputSum)),
            .init("durationMsSum", .int(durationMsSum)), .init("latencyBins", .array(bins)),
            .init("reasonN", .int(Int64(reasonN))), .init("reasonOutput", .int(reasonOutput)),
            .init("reasonDurationMsSum", .int(reasonDurationMsSum)), .init("cacheN", .int(Int64(cacheN))),
            .init("cacheInput", .int(cacheInput)), .init("cacheCached", .int(cacheCached)),
            .init("toolN", .int(Int64(toolN))),
            .init("toolLnSum", .decimal(Self.roundedHalfAway(toolLnSum, places: 6), decimals: 6)),
            .init("toolFailed", .int(Int64(toolFailed))), .init("activeMs", .int(activeMs)),
        ])
    }

    /// `floor(value · 10^places + 0.5) / 10^places`, as the reference rounds.
    static func roundedHalfAway(_ value: Double, places: Int) -> Double {
        guard value.isFinite else { return 0 }
        let scale = Foundation.pow(10, Double(places))
        return (value * scale + 0.5).rounded(.down) / scale
    }
}
