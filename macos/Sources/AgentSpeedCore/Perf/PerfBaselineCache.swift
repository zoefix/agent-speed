import Foundation

/// The baselines of one local day (METRICS 6.11 *Baseline*), computed once per source and model from the stored
/// records; a new day or new records start over.
struct PerfBaselineCache {
    private struct Key: Hashable {
        var source: SourceKind
        var model: OrdinalKey
    }

    private var day: String?
    private var values: [Key: Double?] = [:]

    mutating func baseline(_ records: [PerfDayRecord], source: SourceKind, model: String, today: String) -> Double? {
        if day != today {
            day = today
            values = [:]
        }
        let key = Key(source: source, model: OrdinalKey(model))
        if let cached = values[key] { return cached }
        let value = PerfHistory.baseline(records, source: source, model: model, today: today)
        values[key] = value
        return value
    }
}
