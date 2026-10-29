import Foundation

/// The sufficient statistics of the performance score over a set of completions (METRICS 6.11 *Records*): one day's
/// record of a source and model, a pooled set of records, or a reported statistics row (11.10).
public struct PerfRecord: Equatable, Sendable {
    public var n = 0
    public var outputSum: Int64 = 0
    public var durationMsSum: Int64 = 0
    /// 64 counts of first-content latencies (`PerfLatency`).
    public var latencyBins = [Int](repeating: 0, count: PerfLatency.bucketCount)
    public var reasonN = 0
    public var reasonOutput: Int64 = 0
    public var reasonDurationMsSum: Int64 = 0
    public var cacheN = 0
    public var cacheInput: Int64 = 0
    public var cacheCached: Int64 = 0
    public var toolN = 0
    /// Σ `ln max(resultMs − callMs, 50)`, added left to right.
    public var toolLnSum = 0.0
    public var toolFailed = 0
    public var activeMs: Int64 = 0

    public init() {}

    public static let empty = PerfRecord()

    /// Every field added; latency buckets bucket by bucket, `toolLnSum` as one double addition.
    public mutating func add(_ other: PerfRecord) {
        n += other.n
        outputSum += other.outputSum
        durationMsSum += other.durationMsSum
        for index in latencyBins.indices { latencyBins[index] += other.latencyBins[index] }
        reasonN += other.reasonN
        reasonOutput += other.reasonOutput
        reasonDurationMsSum += other.reasonDurationMsSum
        cacheN += other.cacheN
        cacheInput += other.cacheInput
        cacheCached += other.cacheCached
        toolN += other.toolN
        toolLnSum = toolLnSum + other.toolLnSum
        toolFailed += other.toolFailed
        activeMs += other.activeMs
    }

    /// What one measured response of `source` adds, activeMs aside.
    mutating func addResponse(_ response: TimedResponse, source: SourceKind) {
        let usage = response.record.usage
        n += 1
        outputSum += Int64(usage.output)
        durationMsSum += response.durationMs
        if let latency = PerfLatency.latency(startMs: response.record.startMs, firstMs: response.record.firstMs) {
            latencyBins[PerfLatency.bucket(latency)] += 1
        }
        let thought = min(usage.reasoning, usage.output)
        if source.recordsReasoningApart, thought > 0 {
            reasonN += 1
            reasonOutput += Int64(thought)
            reasonDurationMsSum += response.durationMs
        }
        if source.recordsCacheReads, usage.input > 0 {
            cacheN += 1
            cacheInput += Int64(usage.input)
            cacheCached += Int64(usage.cachedInput)
        }
    }

    /// What one tool call with a result adds, activeMs aside.
    mutating func addCall(durationMs: Int64, failed: Bool?) {
        toolN += 1
        toolLnSum = toolLnSum + Foundation.log(Double(max(durationMs, PerfEvaluation.toolFloorMs)))
        if failed == true { toolFailed += 1 }
    }

    /// The latency buckets as a JSON array of 64 integers (the store's `latency_bins`).
    var latencyBinsJSON: String {
        "[" + latencyBins.map(String.init).joined(separator: ",") + "]"
    }

    /// From a JSON array of 64 integers; zeros for anything else.
    static func bins(fromJSON text: String?) -> [Int] {
        var bins = [Int](repeating: 0, count: PerfLatency.bucketCount)
        guard let text, let values = (try? JSONParser.parse(text))?.array, values.count == bins.count else { return bins }
        for (index, value) in values.enumerated() { bins[index] = Int(value.int64 ?? 0) }
        return bins
    }
}

/// A day's record of one source and raw model (the empty string when the model is unknown).
public struct PerfRecordKey: Hashable, Sendable {
    public var day: String
    public var source: SourceKind
    public var model: String

    public init(day: String, source: SourceKind, model: String) {
        self.day = day
        self.source = source
        self.model = model
    }

    /// By day, source order, then the model's UTF-8 bytes.
    public static func precedes(_ lhs: PerfRecordKey, _ rhs: PerfRecordKey) -> Bool {
        if lhs.day != rhs.day { return lhs.day.utf8.lexicographicallyPrecedes(rhs.day.utf8) }
        if lhs.source != rhs.source { return lhs.source.order < rhs.source.order }
        return lhs.model.utf8.lexicographicallyPrecedes(rhs.model.utf8)
    }
}

/// One stored or computed record with its day, source and model.
public struct PerfDayRecord: Equatable, Sendable {
    public var key: PerfRecordKey
    public var record: PerfRecord

    public init(key: PerfRecordKey, record: PerfRecord) {
        self.key = key
        self.record = record
    }

    public var day: String { key.day }
    public var source: SourceKind { key.source }
    public var model: String { key.model }
}

extension SourceKind {
    /// The position in the source order of METRICS 1.
    public var order: Int { Self.allCases.firstIndex(of: self) ?? 0 }
}
