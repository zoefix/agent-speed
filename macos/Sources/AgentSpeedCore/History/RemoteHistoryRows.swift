import Foundation

/// The stored days of the remote hosts (REMOTE 8.3) in the form the history publishes: each day as a `DailyUsage`
/// (without its peak), its digest, its performance records, and the hosts' names. A row whose text no longer decodes
/// is left out with its digest, so the next sync sends it again.
struct RemoteHistoryRows {
    struct Key: Hashable {
        var host: String
        var day: String
        var source: SourceKind
    }

    private struct Day {
        var usage: DailyUsage
        var digest: String
    }

    /// Host id → its last name, for every host with a stored name.
    private(set) var names: [String: String] = [:]
    /// Host id → the zone its rows were computed in (REMOTE 8.3 *Time zone*); absent for rows stored before zones.
    private(set) var zones: [String: String] = [:]
    private var days: [Key: Day] = [:]
    private var perf: [Key: [String: PerfRecord]] = [:]

    init() {}

    init(database: HistoryDatabase) throws {
        for (id, name) in try database.remoteHostNames() {
            if let name { names[id] = name }
        }
        zones = try database.remoteHostZones()
        for row in try database.remoteDayRows() {
            guard let source = SourceKind(rawValue: row.source),
                  let aggregate = RemoteHistoryMerge.aggregate(json: row.data) else { continue }
            days[Key(host: row.host, day: row.day, source: source)] = Day(
                usage: aggregate.usage(day: row.day, source: source, host: row.host), digest: row.digest)
        }
        for row in try database.remotePerfRows() {
            guard let source = SourceKind(rawValue: row.source), let record = RemoteHistoryMerge.record(json: row.data) else { continue }
            perf[Key(host: row.host, day: row.day, source: source), default: [:]][row.model] = record
        }
    }

    var isEmpty: Bool { days.isEmpty && perf.isEmpty }

    /// `"<day>/<source>"` → digest of every stored day of `host` (REMOTE 5.3 `history`).
    func digests(host: String) -> [String: String] {
        var digests: [String: String] = [:]
        for (key, day) in days where key.host == host {
            digests[RemoteHistoryDigest.key(day: key.day, source: key.source)] = day.digest
        }
        return digests
    }

    /// The days of `sources`, each with the day's live peak (all hosts and sources, METRICS 8).
    func usage(sources: Set<SourceKind>, peak: (String) -> Double?) -> [DailyUsage] {
        days.compactMap { key, day in
            guard sources.contains(key.source) else { return nil }
            var usage = day.usage
            usage.peak = peak(key.day)
            return usage
        }
    }

    /// The records of `sources`, by host, then day, source and model.
    func perfRecords(sources: Set<SourceKind>) -> [PerfDayRecord] {
        var records: [(host: String, record: PerfDayRecord)] = []
        for (key, models) in perf where sources.contains(key.source) {
            for (model, record) in models {
                records.append((key.host, PerfDayRecord(key: PerfRecordKey(day: key.day, source: key.source, model: model),
                                                        record: record)))
            }
        }
        return records
            .sorted { $0.host != $1.host ? $0.host < $1.host : PerfRecordKey.precedes($0.record.key, $1.record.key) }
            .map(\.record)
    }

    /// The committed days of a batch: each replaces its row and its records; with `replace`, the host's other rows go.
    mutating func store(_ batch: [RemoteHistoryBatch.Day], host: String, name: String?, zone: String, replace: Bool = false) {
        if replace {
            days = days.filter { $0.key.host != host }
            perf = perf.filter { $0.key.host != host }
        }
        guard !batch.isEmpty || hosts.contains(host) else {
            names[host] = nil
            zones[host] = nil
            return
        }
        if let name { names[host] = name }
        zones[host] = zone
        for day in batch {
            let key = Key(host: host, day: day.day, source: day.source)
            days[key] = Day(usage: day.aggregate.usage(day: day.day, source: day.source, host: host), digest: day.digest)
            var records: [String: PerfRecord] = [:]
            for item in day.perf { records[item.model] = item.record }
            perf[key] = records.isEmpty ? nil : records
        }
    }

    /// The hosts with stored rows.
    var hosts: Set<String> {
        Set(days.keys.map(\.host)).union(perf.keys.map(\.host))
    }

    mutating func rename(host: String, to name: String) {
        names[host] = name
    }

    mutating func remove(host: String) {
        names[host] = nil
        zones[host] = nil
        days = days.filter { $0.key.host != host }
        perf = perf.filter { $0.key.host != host }
    }
}
