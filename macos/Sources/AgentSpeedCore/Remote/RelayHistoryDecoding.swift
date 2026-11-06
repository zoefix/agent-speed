import Foundation

extension RelayLineDecoder {
    static let historyListLimit = 4096

    func historyDay(_ f: RemoteFields, line: Data) throws(RemoteLineError) -> RemoteHistoryDay {
        let aggregate = try Self.aggregate(try f.object("aggregate"))
        guard let text = Self.rawMember("aggregate", of: line) else { throw RemoteLineError(.wrongType, "aggregate") }
        return RemoteHistoryDay(day: try f.day("day"), source: try f.source("source"), aggregate: aggregate,
                                aggregateJSON: text, digest: try f.hex("digest", count: 16))
    }

    func historyPerf(_ f: RemoteFields, line: Data) throws(RemoteLineError) -> RemoteHistoryPerf {
        let record = try Self.perfRecord(try f.object("record"))
        guard let text = Self.rawMember("record", of: line) else { throw RemoteLineError(.wrongType, "record") }
        return RemoteHistoryPerf(day: try f.day("day"), source: try f.source("source"),
                                 model: try f.string("model", maxBytes: RemoteLimits.modelBytes, .model),
                                 record: record, recordJSON: text)
    }

    /// The day aggregate encoding of the Linux core (`history_store.cpp`). Mode rows of a kind this version does not
    /// know are skipped, as the Linux decoder does.
    static func aggregate(_ f: RemoteFields) throws(RemoteLineError) -> RemoteDayAggregate {
        var models: [String: RemoteDayAggregate.Model] = [:]
        let modelMembers = try f.objectMembers("models")
        guard modelMembers.count <= historyListLimit else { throw RemoteLineError(.tooMany, "aggregate.models") }
        for (model, value) in modelMembers {
            guard model.utf8.count <= RemoteLimits.modelBytes, RemoteText.model.allows(model) else {
                throw RemoteLineError(.badCharacter, "aggregate.models")
            }
            let row = try Row(value, count: 6, "aggregate.models")
            models[model] = RemoteDayAggregate.Model(
                requests: try row.count(0), output: try row.count(1), measuredOutput: try row.count(2),
                measuredMs: try row.count(3), costMicroUsd: try row.optionalInteger(4), unpricedRequests: try row.optionalCount(5)
            )
        }
        var unpriced: [RemoteDayAggregate.Unpriced] = []
        for item in try f.array("unpriced", limit: historyListLimit) {
            let row = try Row(item, count: 5, "aggregate.unpriced")
            unpriced.append(RemoteDayAggregate.Unpriced(
                model: try row.string(0, maxBytes: RemoteLimits.modelBytes, .model),
                provider: try row.optionalString(1, maxBytes: RemoteLimits.providerBytes, .model),
                fast: try row.bool(2), noDate: try row.bool(3), count: try row.count(4)
            ))
        }
        var modes: [RemoteDayAggregate.Mode] = []
        for item in try f.array("modes", limit: historyListLimit) {
            let row = try Row(item, count: 9, "aggregate.modes")
            let model = try row.string(0, maxBytes: RemoteLimits.modelBytes, .model)
            let kind = try row.string(1, maxBytes: 32, .identifier)
            let mode = try row.string(2, maxBytes: 64, .identifier)
            let evidence = try row.string(3, maxBytes: 64, .identifier)
            let provider = try row.optionalString(4, maxBytes: 32, .identifier)
            let counts = (try row.count(5), try row.count(6), try row.count(7), try row.count(8))
            guard let modeKind = ExecutionModeKind(rawValue: kind) else { continue }
            guard ExecutionModeFact.isIdentifier(mode, limit: 64), ExecutionModeFact.isIdentifier(evidence, limit: 64),
                  provider.map({ ExecutionModeFact.isIdentifier($0, limit: 32) }) ?? true else {
                throw RemoteLineError(.badCharacter, "aggregate.modes")
            }
            modes.append(RemoteDayAggregate.Mode(model: model, kind: modeKind, mode: mode, evidence: evidence,
                                                 provider: provider, requests: counts.0, output: counts.1,
                                                 measuredOutput: counts.2, measuredMs: counts.3))
        }
        return RemoteDayAggregate(
            requests: try total(f, "requests"), input: try total(f, "input"), cachedInput: try total(f, "cachedInput"),
            cacheWriteInput: try total(f, "cacheWriteInput"), output: try total(f, "output"),
            reasoning: try total(f, "reasoning"), measuredOutput: try total(f, "measuredOutput"),
            measuredMs: try total(f, "measuredMs"), unfinalized: try total(f, "unfinalized"), agents: try total(f, "agents"),
            costMicroUsd: try f.optionalInteger("costMicroUsd"),
            unpricedRequests: try optionalTotal(f, "unpricedRequests"),
            models: models, unpriced: unpriced, modes: modes
        )
    }

    /// METRICS 6.11 *Records*.
    static func perfRecord(_ f: RemoteFields) throws(RemoteLineError) -> PerfRecord {
        let bins = try f.array("latencyBins", limit: PerfLatency.bucketCount)
        guard bins.count == PerfLatency.bucketCount else { throw RemoteLineError(.outOfRange, "record.latencyBins") }
        var record = PerfRecord()
        for (index, bin) in bins.enumerated() {
            let value = try RemoteFields.integer(bin, "record.latencyBins")
            guard value >= 0 else { throw RemoteLineError(.outOfRange, "record.latencyBins") }
            record.latencyBins[index] = Int(value)
        }
        record.n = Int(try total(f, "n"))
        record.outputSum = try total(f, "outputSum")
        record.durationMsSum = try total(f, "durationMsSum")
        record.reasonN = Int(try total(f, "reasonN"))
        record.reasonOutput = try total(f, "reasonOutput")
        record.reasonDurationMsSum = try total(f, "reasonDurationMsSum")
        record.cacheN = Int(try total(f, "cacheN"))
        record.cacheInput = try total(f, "cacheInput")
        record.cacheCached = try total(f, "cacheCached")
        record.toolN = Int(try total(f, "toolN"))
        record.toolLnSum = try f.number("toolLnSum")
        record.toolFailed = Int(try total(f, "toolFailed"))
        record.activeMs = try total(f, "activeMs")
        return record
    }

    /// A non-negative integer within 2^53.
    private static func total(_ f: RemoteFields, _ key: String) throws(RemoteLineError) -> Int64 {
        Int64(try f.count(key))
    }

    private static func optionalTotal(_ f: RemoteFields, _ key: String) throws(RemoteLineError) -> Int64? {
        guard let value = try f.optionalInteger(key) else { return nil }
        guard value >= 0 else { throw RemoteLineError(.outOfRange, key) }
        return value
    }

    /// The exact JSON text of a top-level member of `line` (the last one when repeated, as the parser keeps).
    static func rawMember(_ name: StaticString, of line: Data) -> String? {
        line.withUnsafeBytes { bytes -> String? in
            var scanner = JSONScanner(bytes)
            var found: String?
            do {
                try scanner.beginObject()
                while let key = try scanner.nextMember() {
                    if scanner.key(key, is: name) {
                        found = try scanner.withStoredText { String(decoding: $0, as: UTF8.self) }
                    } else {
                        try scanner.skipValue()
                    }
                }
            } catch {
                return nil
            }
            return found
        }
    }
}

/// A fixed-length JSON array row of the aggregate encoding.
private struct Row {
    let items: [JSONValue]
    let name: String

    init(_ value: JSONValue, count: Int, _ name: String) throws(RemoteLineError) {
        guard let items = value.array else { throw RemoteLineError(.wrongType, name) }
        guard items.count == count else { throw RemoteLineError(.outOfRange, name) }
        self.items = items
        self.name = name
    }

    func count(_ index: Int) throws(RemoteLineError) -> Int64 {
        let value = try RemoteFields.integer(items[index], name)
        guard value >= 0 else { throw RemoteLineError(.outOfRange, name) }
        return value
    }

    func optionalCount(_ index: Int) throws(RemoteLineError) -> Int64? {
        items[index].isNull ? nil : try count(index)
    }

    func optionalInteger(_ index: Int) throws(RemoteLineError) -> Int64? {
        items[index].isNull ? nil : try RemoteFields.integer(items[index], name)
    }

    func bool(_ index: Int) throws(RemoteLineError) -> Bool {
        guard let value = items[index].bool else { throw RemoteLineError(.wrongType, name) }
        return value
    }

    func string(_ index: Int, maxBytes: Int, _ rule: RemoteText) throws(RemoteLineError) -> String {
        try RemoteFields.checked(items[index], name, maxBytes: maxBytes, rule)
    }

    func optionalString(_ index: Int, maxBytes: Int, _ rule: RemoteText) throws(RemoteLineError) -> String? {
        items[index].isNull ? nil : try string(index, maxBytes: maxBytes, rule)
    }
}
