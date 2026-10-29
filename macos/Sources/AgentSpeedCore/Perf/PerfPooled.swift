import Foundation

/// The 6.9 formula over the sums of a set of records of one source (METRICS 6.11 *Pooled score*): the days of a source
/// and model, a range, the baseline, or reported statistics rows.
public struct PerfPooled: Equatable, Sendable {
    /// A pooled set has a score only from this many timed requests.
    public static let minimumResponses = 20

    public var n: Int
    public var tools: Int
    public var activeMs: Int64
    /// Unrounded; nil below `minimumResponses`.
    public var score: Double?
    public var components: [PerfComponent]

    public var shown: Int? { score.map(perfRoundHalfAway) }

    public init(_ sums: PerfRecord, source: SourceKind) {
        let minimum = PerfFormula.minimumSamples
        var measures: [PerfComponentId: (measure: Double?, samples: Int)] = [:]
        measures[.T] = (sums.n >= minimum ? Double(sums.outputSum) / (Double(sums.durationMsSum) / 1000) : nil, sums.n)
        measures[.L] = Self.latency(sums.latencyBins)
        measures[.R] = (sums.reasonN >= minimum
                        ? Double(sums.reasonOutput) / (Double(sums.reasonDurationMsSum) / 1000) : nil, sums.reasonN)
        measures[.H] = (sums.cacheN >= minimum ? Double(sums.cacheCached) / Double(sums.cacheInput) : nil, sums.cacheN)
        let k = sums.toolN
        if k >= minimum {
            measures[.N] = (sums.activeMs > 0 ? Double(k) / (Double(sums.activeMs) / 60_000) : nil, k)
            measures[.U] = (Foundation.exp(sums.toolLnSum / Double(k)), k)
            measures[.S] = (1 - Double(sums.toolFailed) / Double(k), k)
        } else {
            measures[.N] = (nil, k)
            measures[.U] = (nil, k)
            measures[.S] = (nil, k)
        }
        let (components, score) = PerfFormula.combine(source: source, measures: measures)
        n = sums.n
        tools = k
        activeMs = sums.activeMs
        self.score = sums.n >= Self.minimumResponses ? score : nil
        self.components = components
    }

    public init(_ records: some Sequence<PerfRecord>, source: SourceKind) {
        var sums = PerfRecord()
        for record in records { sums.add(record) }
        self.init(sums, source: source)
    }

    public func component(_ id: PerfComponentId) -> PerfComponent? { components.first { $0.id == id } }

    /// The geometric centre of the bucket that holds the `⌈count / 2⌉`-th latency, from 3 latencies.
    static func latency(_ bins: [Int]) -> (measure: Double?, samples: Int) {
        let count = bins.reduce(0, +)
        guard count >= PerfFormula.minimumSamples else { return (nil, count) }
        let target = (count + 1) / 2
        var seen = 0
        for (k, value) in bins.enumerated() {
            seen += value
            if seen >= target { return (PerfLatency.centre(k), count) }
        }
        return (nil, count)
    }
}

/// The baseline behind 比平常 and the History window's 性能表現 (METRICS 6.11 *Baseline*, *History window*).
public enum PerfHistory {
    public static let baselineDays = 14

    /// The pooled score of the records of `source` and the raw `model` whose day lies in `[today − 14, today − 1]`;
    /// nil below 20 timed requests.
    public static func baseline(_ records: [PerfDayRecord], source: SourceKind, model: String, today: String) -> Double? {
        guard let first = PerfDay.adding(-baselineDays, to: today), let last = PerfDay.adding(-1, to: today) else { return nil }
        let chosen = records.filter {
            $0.source == source && $0.model.utf8.elementsEqual(model.utf8) && $0.day >= first && $0.day <= last
        }
        return PerfPooled(chosen.map(\.record), source: source).score
    }

    /// One row of 性能表現: the pooled values of a source and model over the range and its daily trend.
    public struct Row: Equatable, Sendable {
        public var source: SourceKind
        public var model: String
        public var pooled: PerfPooled
        /// One point per day of the range: that day's pooled score, nil (a gap) below 20 timed requests.
        public var trend: [(day: String, score: Double?)]

        public static func == (lhs: Row, rhs: Row) -> Bool {
            lhs.source == rhs.source && lhs.model == rhs.model && lhs.pooled == rhs.pooled
                && lhs.trend.map(\.day) == rhs.trend.map(\.day) && lhs.trend.map(\.score) == rhs.trend.map(\.score)
        }
    }

    /// Per `(source, model)` with records in `[first, last]` (of every source, or of `source` only), ordered by source,
    /// then timed requests descending, then the model's UTF-8 bytes.
    public static func range(_ records: [PerfDayRecord], first: String, last: String, source: SourceKind? = nil) -> [Row] {
        var groups: [GroupKey: [PerfDayRecord]] = [:]
        var order: [GroupKey] = []
        for record in records where record.day >= first && record.day <= last && (source == nil || record.source == source) {
            let key = GroupKey(source: record.source, model: OrdinalKey(record.model))
            if groups[key] == nil { order.append(key) }
            groups[key, default: []].append(record)
        }
        let days = PerfDay.days(from: first, to: last)
        var rows = order.map { key -> Row in
            let part = groups[key] ?? []
            var byDay: [String: [PerfRecord]] = [:]
            for record in part { byDay[record.day, default: []].append(record.record) }
            let trend = days.map { day in (day: day, score: PerfPooled(byDay[day] ?? [], source: key.source).score) }
            return Row(source: key.source, model: key.model.text, pooled: PerfPooled(part.map(\.record), source: key.source),
                       trend: trend)
        }
        rows.sort { lhs, rhs in
            if lhs.source != rhs.source { return lhs.source.order < rhs.source.order }
            if lhs.pooled.n != rhs.pooled.n { return lhs.pooled.n > rhs.pooled.n }
            return lhs.model.utf8.lexicographicallyPrecedes(rhs.model.utf8)
        }
        return rows
    }

    private struct GroupKey: Hashable {
        var source: SourceKind
        var model: OrdinalKey
    }
}

/// Calendar dates as `YYYY-MM-DD` labels, in the proleptic Gregorian calendar without a time zone.
public enum PerfDay {
    /// The label `count` days after `day` (before, when negative); nil for a malformed label.
    public static func adding(_ count: Int, to day: String) -> String? {
        guard let ordinal = ordinal(day) else { return nil }
        return label(ordinal + count)
    }

    /// Every day of `[first, last]`, in order (empty when `last` is before `first`).
    public static func days(from first: String, to last: String) -> [String] {
        guard let start = ordinal(first), let end = ordinal(last), start <= end else { return [] }
        return (start...end).map(label)
    }

    /// Days since 1970-01-01.
    static func ordinal(_ day: String) -> Int? {
        let parts = day.split(separator: "-")
        guard parts.count == 3, let y = Int(parts[0]), let m = Int(parts[1]), let d = Int(parts[2]),
              (1...12).contains(m), (1...31).contains(d) else { return nil }
        let year = m <= 2 ? y - 1 : y
        let era = (year >= 0 ? year : year - 399) / 400
        let yoe = year - era * 400
        let doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era * 146_097 + doe - 719_468
    }

    static func label(_ ordinal: Int) -> String {
        let z = ordinal + 719_468
        let era = (z >= 0 ? z : z - 146_096) / 146_097
        let doe = z - era * 146_097
        let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100)
        let mp = (5 * doy + 2) / 153
        let d = doy - (153 * mp + 2) / 5 + 1
        let m = mp < 10 ? mp + 3 : mp - 9
        let y = yoe + era * 400 + (m <= 2 ? 1 : 0)
        func pad(_ value: Int, _ width: Int) -> String {
            let text = String(value)
            return String(repeating: "0", count: max(0, width - text.count)) + text
        }
        return pad(y, 4) + "-" + pad(m, 2) + "-" + pad(d, 2)
    }
}
