import CryptoKit
import Foundation

/// The Linux core's day aggregate encoding (REMOTE 5.4, `history_store.cpp` `encode`), field for field. Costs are
/// nil when the relay wrote `null` (contributions stored before costs existed).
public struct RemoteDayAggregate: Equatable, Sendable {
    public struct Model: Equatable, Sendable {
        public var requests: Int64
        public var output: Int64
        public var measuredOutput: Int64
        public var measuredMs: Int64
        public var costMicroUsd: Int64?
        public var unpricedRequests: Int64?
    }

    /// `[model, provider, fast, noDate, count]` (METRICS 9.10).
    public struct Unpriced: Equatable, Sendable {
        public var model: String
        public var provider: String?
        public var fast: Bool
        public var noDate: Bool
        public var count: Int64
    }

    /// `[model, kind, mode, evidence, provider, requests, output, measuredOutput, measuredMs]`.
    public struct Mode: Equatable, Sendable {
        public var model: String
        public var kind: ExecutionModeKind
        public var mode: String
        public var evidence: String
        public var provider: String?
        public var requests: Int64
        public var output: Int64
        public var measuredOutput: Int64
        public var measuredMs: Int64
    }

    public var requests: Int64
    public var input: Int64
    public var cachedInput: Int64
    public var cacheWriteInput: Int64
    public var output: Int64
    public var reasoning: Int64
    public var measuredOutput: Int64
    public var measuredMs: Int64
    public var unfinalized: Int64
    public var agents: Int64
    public var costMicroUsd: Int64?
    public var unpricedRequests: Int64?
    /// By raw model (`""` = unknown).
    public var models: [String: Model]
    public var unpriced: [Unpriced]
    public var modes: [Mode]
}

/// A `historyDay` line. `aggregateJSON` is the aggregate's exact text on the line: the app stores it (`remote_days.data`)
/// and the digest covers it.
public struct RemoteHistoryDay: Equatable, Sendable {
    public var day: String
    public var source: SourceKind
    public var aggregate: RemoteDayAggregate
    public var aggregateJSON: String
    /// 16 lowercase hex digits.
    public var digest: String

    /// The key of the `history` request's `digests` (REMOTE 5.3).
    public var digestKey: String { RemoteHistoryDigest.key(day: day, source: source) }
}

/// A `historyPerf` line (METRICS 6.11 *Records*); `recordJSON` is the record's exact text (`remote_perf.data`).
public struct RemoteHistoryPerf: Equatable, Sendable {
    public var day: String
    public var source: SourceKind
    /// The raw model; `""` when unknown.
    public var model: String
    public var record: PerfRecord
    public var recordJSON: String

    public var key: PerfRecordKey { PerfRecordKey(day: day, source: source, model: model) }
}

/// The digest of REMOTE 5.4: the first 16 hex digits of SHA-256 over the aggregate's JSON text followed by the JSON
/// texts of that day and source's performance records in model order (UTF-8 bytes).
public enum RemoteHistoryDigest {
    public static func key(day: String, source: SourceKind) -> String {
        day + "/" + source.rawValue
    }

    public static func digest(aggregateJSON: String, perf: [(model: String, json: String)]) -> String {
        var hasher = SHA256()
        hasher.update(data: Data(aggregateJSON.utf8))
        for record in perf.sorted(by: { $0.model.utf8.lexicographicallyPrecedes($1.model.utf8) }) {
            hasher.update(data: Data(record.json.utf8))
        }
        return hasher.finalize().prefix(8).map { String(format: "%02x", $0) }.joined()
    }
}
