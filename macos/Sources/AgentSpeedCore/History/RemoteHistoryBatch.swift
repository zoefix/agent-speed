import Foundation

/// The bounds of one host's stored rows (REMOTE 8.3 *Bounds*).
struct RemoteHistoryLimits: Equatable, Sendable {
    var days = 100_000
    var perf = 1_000_000
}

/// A host's `historyDay` lines, each with the `historyPerf` lines right after it (REMOTE 8.2), received since the
/// host's last `historyDone`. Kept in memory until it commits, except that a large batch moves its complete days to
/// the store's staging tables; a broken connection drops it (REMOTE 8.3).
struct RemoteHistoryBatch: Equatable {
    struct Perf: Equatable {
        var model: String
        var json: String
        var record: PerfRecord

        var byteCount: Int { model.utf8.count + json.utf8.count }
    }

    struct Day: Equatable {
        var day: String
        var source: SourceKind
        var aggregate: RemoteDayAggregate
        var aggregateJSON: String
        var digest: String
        /// By model, each model once (a repeated line replaces the earlier one).
        var perf: [Perf] = []

        var key: UsageKey { UsageKey(day: day, source: source) }

        var byteCount: Int {
            aggregateJSON.utf8.count + perf.reduce(0) { $0 + $1.byteCount }
        }
    }

    /// Records accepted per day and source (the relay's list limit).
    static let perfLimit = RelayLineDecoder.historyListLimit

    let host: String
    let connection: Int
    /// In arrival order; a day and source sent again replaces its earlier entry.
    private(set) var days: [Day] = []
    private var keys: Set<UsageKey> = []
    /// The JSON text held, for the memory bound.
    private(set) var byteCount = 0
    /// The days moved to the staging tables, with their record counts.
    private(set) var staged: [UsageKey: Int] = [:]
    /// The distinct days of the batch, staged ones included, and the records they keep (a day sent again replaces
    /// its earlier version and its records).
    private(set) var dayCount = 0
    private(set) var perfCount = 0

    init(host: String, connection: Int) {
        self.host = host
        self.connection = connection
    }

    mutating func add(_ line: RemoteHistoryDay) {
        let day = Day(day: line.day, source: line.source, aggregate: line.aggregate, aggregateJSON: line.aggregateJSON,
                      digest: line.digest)
        if !keys.insert(day.key).inserted, let index = days.firstIndex(where: { $0.key == day.key }) {
            byteCount -= days[index].byteCount
            perfCount -= days[index].perf.count
            days.remove(at: index)
        } else if let records = staged[day.key] {
            perfCount -= records
        } else {
            dayCount += 1
        }
        days.append(day)
        byteCount += day.byteCount
    }

    /// A record belongs to the day sent right before it; any other is ignored.
    mutating func add(_ line: RemoteHistoryPerf) {
        guard let last = days.indices.last, days[last].day == line.day, days[last].source == line.source else { return }
        let perf = Perf(model: line.model, json: line.recordJSON, record: line.record)
        if let index = days[last].perf.firstIndex(where: { $0.model == perf.model }) {
            byteCount -= days[last].perf[index].byteCount
            days[last].perf[index] = perf
        } else if days[last].perf.count < Self.perfLimit {
            days[last].perf.append(perf)
            perfCount += 1
        } else {
            return
        }
        byteCount += perf.byteCount
    }

    /// Removes and returns every day but the last, which may still get records; they count as staged.
    mutating func takeCompleted() -> [Day] {
        guard days.count > 1 else { return [] }
        let completed = Array(days.dropLast())
        days.removeFirst(completed.count)
        keys = Set(days.map(\.key))
        byteCount = days.reduce(0) { $0 + $1.byteCount }
        for day in completed { staged[day.key] = day.perf.count }
        return completed
    }

    var isStaged: Bool { !staged.isEmpty }

    func exceeds(_ limits: RemoteHistoryLimits) -> Bool {
        dayCount > limits.days || perfCount > limits.perf
    }
}

/// The sync rules of REMOTE 8.2 and 8.3 over the hosts' events, without storage: when to answer with the digests,
/// what to stage, commit or discard, and which batch to drop.
struct RemoteHistorySyncState {
    enum Action: Equatable {
        case none
        /// Answer `historyDue` with the host's stored digests.
        case request(host: String)
        /// Move these complete days of a large batch to the staging tables; `first`: the batch's first ones (staged
        /// rows left by a dropped batch go first).
        case stage(host: String, days: [RemoteHistoryBatch.Day], first: Bool)
        /// `historyDone` of `connection`: commit the staged days (when `staged`) and these.
        case commit(host: String, connection: Int, days: [RemoteHistoryBatch.Day], staged: Bool)
        /// The batch exceeds the bounds: it is discarded with its staged rows, and the rest of it ignored.
        case overflow(host: String)
        /// `historyDone` of a batch that exceeded the bounds.
        case tooLarge(host: String)
    }

    let limits: RemoteHistoryLimits
    /// Above this much JSON text, a batch stages its complete days early so a host's memory stays bounded.
    let flushBytes: Int
    private(set) var batches: [String: RemoteHistoryBatch] = [:]
    /// Hosts whose batch exceeded the bounds, with that connection, until its `historyDone`.
    private(set) var overflowed: [String: Int] = [:]
    /// Hosts whose last status is `connected`, with that connection.
    private(set) var connected: [String: Int] = [:]

    init(limits: RemoteHistoryLimits = RemoteHistoryLimits(), flushBytes: Int = 32 << 20) {
        self.limits = limits
        self.flushBytes = flushBytes
    }

    mutating func apply(_ event: RemoteHostEvent) -> Action {
        let host = event.hostId
        if let batch = batches[host], batch.connection != event.connection { batches[host] = nil }
        if let connection = overflowed[host], connection != event.connection { overflowed[host] = nil }
        switch event.kind {
        case .status(let status):
            switch status.status {
            case .connected:
                connected[host] = event.connection
            case .seeding:
                connected[host] = nil
            default:
                connected[host] = nil
                batches[host] = nil
                overflowed[host] = nil
            }
            return .none
        case .historyDue:
            return .request(host: host)
        case .message(let message, _):
            return apply(message, host: host, connection: event.connection)
        }
    }

    /// Forgets a host's batch (its history was deleted).
    mutating func drop(host: String) {
        batches[host] = nil
        overflowed[host] = nil
    }

    /// Forgets every batch (*Clear history*); the connection states stay.
    mutating func dropBatches() {
        batches.removeAll()
        overflowed.removeAll()
    }

    private mutating func apply(_ message: RelayMessage, host: String, connection: Int) -> Action {
        switch message {
        case .historyDay(let day):
            guard overflowed[host] == nil else { return .none }
            // Taken out of the dictionary, so it changes in place.
            var batch = batches.removeValue(forKey: host) ?? RemoteHistoryBatch(host: host, connection: connection)
            batch.add(day)
            return keep(&batch, host: host, connection: connection)
        case .historyPerf(let perf):
            guard overflowed[host] == nil, var batch = batches.removeValue(forKey: host) else { return .none }
            batch.add(perf)
            return keep(&batch, host: host, connection: connection)
        case .historyDone:
            if overflowed.removeValue(forKey: host) != nil { return .tooLarge(host: host) }
            let batch = batches.removeValue(forKey: host)
            return .commit(host: host, connection: connection, days: batch?.days ?? [], staged: batch?.isStaged ?? false)
        case .error(let error) where error.code == .historyFailed:
            batches[host] = nil
            overflowed[host] = nil
            return .none
        default:
            return .none
        }
    }

    /// Keeps the batch, staging its complete days when it holds too much text; a batch beyond the bounds overflows.
    private mutating func keep(_ batch: inout RemoteHistoryBatch, host: String, connection: Int) -> Action {
        if batch.exceeds(limits) {
            overflowed[host] = connection
            return .overflow(host: host)
        }
        let first = !batch.isStaged
        let early = batch.byteCount > flushBytes ? batch.takeCompleted() : []
        batches[host] = batch
        return early.isEmpty ? .none : .stage(host: host, days: early, first: first)
    }
}
