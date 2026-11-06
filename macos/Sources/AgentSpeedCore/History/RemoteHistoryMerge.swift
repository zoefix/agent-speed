import Foundation

extension RemoteDayAggregate {
    /// The aggregate as the local analyzer holds one stored contribution (`HistoryDatabase.loadEntries`): the same
    /// totals, models, costs (nil when either part is null), mode usage and unpriced facts.
    var dayAggregate: DayAggregate {
        var aggregate = DayAggregate()
        aggregate.totals = TokenTotals(
            requests: Int(requests), input: Int(input), cachedInput: Int(cachedInput), cacheWriteInput: Int(cacheWriteInput),
            output: Int(output), reasoning: Int(reasoning))
        aggregate.measuredOutput = Int(measuredOutput)
        aggregate.measuredMs = measuredMs
        aggregate.unfinalized = Int(unfinalized)
        aggregate.agents = Int(agents)
        aggregate.cost = Self.cost(costMicroUsd, unpricedRequests)
        for (model, tally) in models {
            aggregate.models[model] = ModelTally(
                requests: Int(tally.requests), output: Int(tally.output), measuredOutput: Int(tally.measuredOutput),
                measuredMs: tally.measuredMs, cost: Self.cost(tally.costMicroUsd, tally.unpricedRequests))
        }
        for mode in modes where Self.storedEvidence.contains(mode.evidence) {
            let attribution = ExecutionModeAttribution(kind: mode.kind, mode: mode.mode, evidence: mode.evidence,
                                                       provider: mode.provider)
            let usage = ExecutionModeUsage(model: mode.model, attribution: attribution, requests: Int(mode.requests),
                                           output: Int(mode.output), measuredOutput: Int(mode.measuredOutput),
                                           measuredMs: mode.measuredMs)
            let key = ExecutionModeUsageKey(model: mode.model, attribution: attribution)
            if aggregate.modes[key] == nil { aggregate.modes[key] = usage } else { aggregate.modes[key]?.add(usage) }
        }
        for fact in unpriced where fact.count > 0 {
            let key = UnpricedFactKey(model: fact.model, provider: fact.provider, fast: fact.fast, noDate: fact.noDate)
            aggregate.unpricedFacts[key] = (aggregate.unpricedFacts[key] ?? 0).saturatingAdd(Int(fact.count))
        }
        return aggregate
    }

    /// One host's row of the daily history (REMOTE 8.3). It has no price version: the relay priced it with the
    /// table it was given, whose version the line does not carry.
    func usage(day: String, source: SourceKind, host: String, peak: Double? = nil) -> DailyUsage {
        var usage = dayAggregate.usage(day: day, source: source, peak: peak, priceVersion: nil)
        usage.host = host
        return usage
    }

    /// The evidence values `contribution_modes` keeps (`HistoryDatabase.loadModes`).
    private static let storedEvidence: Set<String> = ["served", "recorded", "selected"]

    private static func cost(_ micro: Int64?, _ unpriced: Int64?) -> CostTally? {
        guard let micro, let unpriced else { return nil }
        return CostTally(costMicroUsd: micro, unpricedRequests: Int(unpriced))
    }
}

/// Joins the hosts' rows to the local history (REMOTE 8.3). Without remote rows, the local values pass unchanged.
enum RemoteHistoryMerge {
    /// Local and remote days sorted by day, source, then host (this computer first, then by id).
    static func days(local: [DailyUsage], remote: [DailyUsage]) -> [DailyUsage] {
        guard !remote.isEmpty else { return local }
        return (local + remote).sorted { lhs, rhs in
            if lhs.day != rhs.day { return lhs.day < rhs.day }
            if lhs.source != rhs.source { return lhs.source.rawValue < rhs.source.rawValue }
            if lhs.isLocal != rhs.isLocal { return lhs.isLocal }
            return lhs.host < rhs.host
        }
    }

    /// Every host's records pooled per day, source and model (METRICS 6.11: the baseline compares a model with itself
    /// across the user's machines); local records are added first.
    static func perf(local: [PerfDayRecord], remote: [PerfDayRecord]) -> [PerfDayRecord] {
        guard !remote.isEmpty else { return local }
        var sums: [PerfRecordKey: PerfRecord] = [:]
        for item in local + remote { sums[item.key, default: PerfRecord()].add(item.record) }
        return sums.map { PerfDayRecord(key: $0.key, record: $0.value) }.sorted { PerfRecordKey.precedes($0.key, $1.key) }
    }

    /// The stored text of an aggregate (`remote_days.data`), with the relay line's validation.
    static func aggregate(json: String) -> RemoteDayAggregate? {
        guard let members = (try? JSONParser.parse(Data(json.utf8)))?.object else { return nil }
        return try? RelayLineDecoder.aggregate(RemoteFields(members))
    }

    /// The stored text of a performance record (`remote_perf.data`).
    static func record(json: String) -> PerfRecord? {
        guard let members = (try? JSONParser.parse(Data(json.utf8)))?.object else { return nil }
        return try? RelayLineDecoder.perfRecord(RemoteFields(members))
    }
}
