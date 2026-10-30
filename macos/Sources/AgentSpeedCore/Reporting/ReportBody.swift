import Foundation

/// `client` of every body (REPORTING 6.3).
public struct ReportClientInfo: Equatable, Codable, Sendable {
    public var version: String
    public var platform: String
    public var osMajor: Int

    public init(version: String, platform: String = "macOS", osMajor: Int) {
        self.version = Self.isVersion(version) ? version : "0.0.0"
        self.platform = platform
        self.osMajor = min(max(osMajor, 10), 99)
    }

    /// This Mac: the app version and the macOS major version.
    public static func current(version: String) -> ReportClientInfo {
        ReportClientInfo(version: version, osMajor: ProcessInfo.processInfo.operatingSystemVersion.majorVersion)
    }

    var json: ReportJSON {
        .object(["app": .string("AgentSpeed"), "version": .string(version), "platform": .string(platform),
                 "osMajor": .int(osMajor)])
    }

    /// `MAJOR.MINOR.PATCH[-PRERELEASE]`, at most 32 characters.
    static func isVersion(_ text: String) -> Bool {
        guard text.utf8.count <= 32 else { return false }
        let parts = text.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
        let numbers = parts[0].split(separator: ".", omittingEmptySubsequences: false)
        guard numbers.count == 3, numbers.allSatisfy({ !$0.isEmpty && $0.allSatisfy(\.isASCIIDigit) }) else { return false }
        guard parts.count == 2 else { return true }
        return !parts[1].isEmpty && parts[1].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "." || $0 == "-") }
    }
}

private extension Character {
    var isASCIIDigit: Bool { isASCII && isNumber }
}

/// Who and when a report is (REPORTING 6.3).
public struct ReportEnvelope: Equatable, Sendable {
    public var client: ReportClientInfo
    public var deviceId: String
    public var username: String
    public var runId: String
    public var seq: Int
    /// Unix ms of the build on the corrected clock (METRICS 11.6).
    public var sentAtMs: Int64
    public var windowMs: Int64
    public var activity: ReportScheduler.Activity
    public var titleLevel: Int
    /// Only for the WebSocket transport (METRICS 11.9); HTTP bodies never carry it.
    public var realtime: ReportRealtime?

    public init(client: ReportClientInfo, deviceId: String, username: String, runId: String, seq: Int, sentAtMs: Int64,
                windowMs: Int64, activity: ReportScheduler.Activity, titleLevel: Int, realtime: ReportRealtime? = nil) {
        self.client = client
        self.deviceId = deviceId
        self.username = username
        self.runId = runId
        self.seq = seq
        self.sentAtMs = sentAtMs
        self.windowMs = windowMs
        self.activity = activity
        self.titleLevel = titleLevel
        self.realtime = realtime
    }
}

/// What a report may contain and how large it may be (REPORTING 3.2, 6.1, 6.8).
public struct ReportOptions: Equatable, Sendable {
    /// The optional sections both the service lists and the user's switches allow.
    public var sections: Set<ReportSection>
    public var maxModels: Int
    public var bodyLimit: Int
    public var metric: SpeedMetric
    public var redline: Double
    public var priceVersion: Int
    public var extensions: ReportExtensions
    /// `maxMessageBytes` of the WebSocket transport: the report message must fit as well (METRICS 11.9).
    public var messageLimit: Int?

    public init(sections: Set<ReportSection>, maxModels: Int = 16, bodyLimit: Int = 16_384, metric: SpeedMetric = .output,
                redline: Double = 2000, priceVersion: Int = 1, extensions: ReportExtensions = .none,
                messageLimit: Int? = nil) {
        self.sections = sections
        self.maxModels = maxModels
        self.bodyLimit = bodyLimit
        self.metric = metric
        self.redline = redline
        self.priceVersion = priceVersion
        self.extensions = extensions
        self.messageLimit = messageLimit
    }

    /// The `perfScore` extension is active (METRICS 11.10): advertised, with `models` sent by consent.
    public var perfScoreActive: Bool { extensions.perfScore && sections.contains(.models) && maxModels >= 1 }

    /// Only the retired `taskScore` is advertised: the report carries the thinking speed in its shape.
    public var legacyTaskScoreActive: Bool {
        !extensions.perfScore && extensions.taskScore && sections.contains(.models) && maxModels >= 1
    }

    /// Which score objects a report carries.
    var scoreMode: ReportScoreMode {
        perfScoreActive ? .perfScore : legacyTaskScoreActive ? .legacyTaskScore : .none
    }

    /// The body is small enough to send: within `bodyLimit` and, over WebSocket, its message within `messageLimit`.
    func fits(_ body: Data) -> Bool {
        guard body.count <= bodyLimit else { return false }
        guard let messageLimit else { return true }
        return ReportStreamMessage.messageBytes(body: body) <= messageLimit
    }
}

/// Which score objects a report carries (METRICS 11.10 *Active*, *Legacy*).
enum ReportScoreMode: Sendable {
    case none
    case perfScore
    /// The retired `taskScore`: the thinking speed only.
    case legacyTaskScore
}

/// The report body of REPORTING 6 from a window, the tick's values and the options (METRICS 11.2, 11.7).
public enum ReportBody {
    public static let maxTokS = 1e9
    public static let maxCount = 100_000
    public static let maxWindowMs: Int64 = 900_000

    /// The body as sent: within `bodyLimit`, dropping the per-model breakdowns, then `models`, then `sources` when it
    /// would not fit; the top-level `perfScore` (or legacy `taskScore`) always stays (METRICS 11.10 *Size*).
    public static func data(_ envelope: ReportEnvelope, _ content: ReportWindowContent, _ sample: ReportSample,
                            _ options: ReportOptions) -> Data {
        fitted(envelope, content, sample, options).data
    }

    /// The document and its bytes after the size rule (REPORTING 6.8).
    public static func fitted(_ envelope: ReportEnvelope, _ content: ReportWindowContent, _ sample: ReportSample,
                              _ options: ReportOptions) -> (json: ReportJSON, data: Data) {
        var options = options
        let scores = options.scoreMode
        var document = json(envelope, content, sample, options, scores: scores)
        var data = document.compactData
        if !options.fits(data), options.extensions.modelHarnessRequests || options.extensions.executionModes {
            options.extensions.modelHarnessRequests = false
            options.extensions.executionModes = false
            document = json(envelope, content, sample, options, scores: scores)
            data = document.compactData
        }
        for section in [ReportSection.models, .sources] where !options.fits(data) {
            options.sections.remove(section)
            document = json(envelope, content, sample, options, scores: scores)
            data = document.compactData
        }
        return (document, data)
    }

    public static func json(_ envelope: ReportEnvelope, _ content: ReportWindowContent, _ sample: ReportSample,
                            _ options: ReportOptions) -> ReportJSON {
        json(envelope, content, sample, options, scores: options.scoreMode)
    }

    /// `scores`: the extension was active before the size rule, so the top-level object stays without `models`.
    static func json(_ envelope: ReportEnvelope, _ content: ReportWindowContent, _ sample: ReportSample,
                     _ options: ReportOptions, scores: ReportScoreMode) -> ReportJSON {
        let total = content.usage.total
        let agents = sample.facts.agents.values.reduce(into: ReportAgentCounts()) {
            $0.generating += $1.generating
            $0.tools += $1.tools
            $0.active += $1.active
        }
        let value = sample.value(for: options.metric)
        var members: [ReportJSON.Member] = [
            .init("schema", .int(1)),
            .init("client", envelope.client.json),
            .init("deviceId", .string(envelope.deviceId)),
            .init("username", .string(envelope.username)),
            .init("run", .object(["id": .string(envelope.runId), "seq": .int(envelope.seq)])),
            .init("sentAt", .string(ReportInstant.format(unixMs: envelope.sentAtMs))),
            .init("windowMs", .int(min(max(envelope.windowMs, 0), maxWindowMs))),
            .init("activity", .string(envelope.activity.rawValue)),
            .init("live", .object([
                "outputTokS": tokS(sample.live.output), "totalTokS": tokS(sample.live.total),
                "estimatedShare": share(sample.estimatedShare), "agents": counts(agents),
            ])),
            .init("peak", .object(["outputTokS": tokS(content.peak.output), "totalTokS": tokS(content.peak.total)])),
            .init("delta", delta(total)),
            .init("title", .object([
                "level": .int(min(max(envelope.titleLevel, 0), ReportTitleTracker.maxLevel)),
                "metric": .string(options.metric.rawValue),
                "redline": .decimal(options.redline, decimals: 1),
                "heat": share(ReportTitleTracker.heat(value: value, redline: options.redline)),
            ])),
        ]
        let sections = options.sections
        if sections.contains(.cost) {
            members.append(.init("cost", .object([
                "liveMicroUsdPerSecond": perSecond(sample.liveCost.microUsdPerSecond),
                "peakMicroUsdPerSecond": perSecond(max(content.peakCost, sample.liveCost.microUsdPerSecond)),
                "knownShare": share(sample.liveCost.knownShare(liveOutput: sample.live.output)),
                "deltaMicroUsd": .int(total.costMicroUsd),
                "unpricedRequests": .int(total.unpricedRequests),
                "priceVersion": .int(max(1, options.priceVersion)),
            ])))
        }
        if sections.contains(.traffic) {
            let bytes = content.agentBytes.values.reduce(TrafficBytes.zero, +)
            members.append(.init("traffic", traffic(
                status: sample.traffic.status, includesTools: sample.traffic.includesTools, rate: sample.traffic.rate,
                peak: content.peakRate, bytes: bytes)))
        }
        if sections.contains(.sources) {
            members.append(.init("sources", .array(sourceEntries(content, sample, sections: sections))))
        }
        if sections.contains(.models), options.maxModels > 0 {
            let entries = modelEntries(content, sample, maxModels: options.maxModels, scores: scores)
            members.append(.init("models", .array(entries.map {
                modelJSON($0, withCost: sections.contains(.cost), extensions: options.extensions, scores: scores)
            })))
        }
        let perf = sample.facts.perf
        switch scores {
        case .perfScore:
            members.append(.init("perfScore", ReportPerfScoreValue(perf.value, thinking: perf.thinking).json))
        case .legacyTaskScore:
            members.append(.init("taskScore", ReportPerfScoreValue.legacy(thinking: perf.thinking).json))
        case .none:
            break
        }
        if let realtime = envelope.realtime {
            let mixed = sections.contains(.traffic) && sample.traffic.status == .ok
            members.append(.init("realtime", realtime.json(mixed: mixed, sentAtMs: envelope.sentAtMs)))
        }
        return .object(members)
    }

    // MARK: - Sections

    private static func traffic(status: ReportTrafficSample.Status, includesTools: Bool, rate: TrafficRate?,
                                peak: TrafficRate?, bytes: TrafficBytes) -> ReportJSON {
        guard status == .ok else { return .object(["status": .string(status.rawValue)]) }
        let top: TrafficRate? = switch (peak, rate) {
        case let (peak?, rate?): TrafficRate(rx: max(peak.rx, rate.rx), tx: max(peak.tx, rate.tx))
        case let (peak, rate): peak ?? rate
        }
        return .object([
            .init("status", .string("ok")),
            .init("includesTools", .bool(includesTools)),
            .init("rxBytesPerSecond", .optionalInt(rate.map { bytesPerSecond($0.rx) })),
            .init("txBytesPerSecond", .optionalInt(rate.map { bytesPerSecond($0.tx) })),
            .init("peakRxBytesPerSecond", .optionalInt(top.map { bytesPerSecond($0.rx) })),
            .init("peakTxBytesPerSecond", .optionalInt(top.map { bytesPerSecond($0.tx) })),
            .init("rxBytes", .int(max(0, bytes.rx))),
            .init("txBytes", .int(max(0, bytes.tx))),
        ])
    }

    /// One entry per source with activity in the window, in source order (REPORTING 6.6.3, METRICS 11.7).
    private static func sourceEntries(_ content: ReportWindowContent, _ sample: ReportSample,
                                      sections: Set<ReportSection>) -> [ReportJSON] {
        var usage: [SourceKind: ReportUsage] = [:]
        for (key, value) in content.usage.byModel { usage[key.source, default: .zero] += value }
        let trafficOK = sections.contains(.traffic) && sample.traffic.status == .ok
        var entries: [ReportJSON] = []
        for kind in SourceKind.allCases {
            let source = sample.sources[kind] ?? ReportSample.Source()
            let agents = sample.facts.agents[kind] ?? .zero
            let delta = usage[kind] ?? .zero
            let bytes = content.agentBytes[kind] ?? .zero
            let rate = trafficOK ? sample.traffic.sourceRates[kind] ?? (sample.traffic.rate == nil ? nil : .zero) : nil
            // Activity (REPORTING 6.6.3): a number of the entry as sent, other than `agents.active` and the shares,
            // is above 0 — a source whose only increment is a late price included, so the cost sums add up.
            var values: [Double] = [
                rounded(source.live.output, 1), rounded(source.live.total, 1), Double(agents.generating),
                Double(agents.tools), Double(delta.requests), Double(delta.output), Double(delta.input),
                Double(delta.cachedInput), Double(delta.cacheWriteInput), Double(delta.reasoning),
                Double(delta.measuredOutput), Double(delta.measuredMs),
            ]
            if sections.contains(.cost) {
                values += [rounded(source.liveCost.microUsdPerSecond, 0), Double(delta.costMicroUsd),
                           Double(delta.unpricedRequests)]
            }
            if trafficOK {
                values += [rounded(rate?.rx ?? 0, 0), rounded(rate?.tx ?? 0, 0), Double(bytes.rx), Double(bytes.tx)]
            }
            guard values.contains(where: { $0 > 0 }) else { continue }
            let estimated = source.live.output > 0 ? source.liveEstimated.output / source.live.output : 0
            var members: [ReportJSON.Member] = [
                .init("source", .string(kind.rawValue)),
                .init("live", .object(["outputTokS": tokS(source.live.output), "totalTokS": tokS(source.live.total),
                                       "estimatedShare": share(estimated)])),
                .init("agents", counts(agents)),
                .init("delta", self.delta(delta)),
            ]
            if sections.contains(.cost) {
                members.append(.init("cost", .object([
                    "liveMicroUsdPerSecond": perSecond(source.liveCost.microUsdPerSecond),
                    "knownShare": share(source.liveCost.knownShare(liveOutput: source.live.output)),
                    "deltaMicroUsd": .int(delta.costMicroUsd),
                    "unpricedRequests": .int(delta.unpricedRequests),
                ])))
            }
            if trafficOK {
                members.append(.init("traffic", .object([
                    "rxBytesPerSecond": .optionalInt(rate.map { bytesPerSecond($0.rx) }),
                    "txBytesPerSecond": .optionalInt(rate.map { bytesPerSecond($0.tx) }),
                    "rxBytes": .int(bytes.rx), "txBytes": .int(bytes.tx),
                ])))
            }
            entries.append(.object(members))
        }
        return entries
    }

    private struct ModelEntry {
        var id: String
        var sources: Set<SourceKind> = []
        var usage = ReportUsage()
        var generating = 0
        var usageBySource: [SourceKind: ReportUsage] = [:]
        var modes: [ReportModeGroup: ReportUsage] = [:]
        /// The performance score's set value, unrounded thinking and statistics rows (METRICS 11.10).
        var perfValue = PerfSetValue.empty
        var thinking = 0.0
        var perfRows: [SourceKind: PerfRecord] = [:]
        /// Listed only for the performance score: zero usage, so no mode rows (METRICS 11.10 *Other extensions*).
        var perfOnly = false
    }

    /// One entry per model with any increment in the window (a late price included) or a generating agent, by output
    /// then id, `other` last (REPORTING 6.6.4, METRICS 11.3, 11.7); with `perfScore`, also every model an agent is live
    /// with and every model with statistics rows (METRICS 11.10 *Membership*). The legacy `taskScore` adds no entry.
    private static func modelEntries(_ content: ReportWindowContent, _ sample: ReportSample, maxModels: Int,
                                     scores: ReportScoreMode) -> [ModelEntry] {
        var byId: [String: ModelEntry] = [:]
        for (key, value) in content.usage.byModel {
            byId[key.modelId, default: ModelEntry(id: key.modelId)].usage += value
            byId[key.modelId]?.sources.insert(key.source)
            byId[key.modelId]?.usageBySource[key.source, default: .zero] += value
        }
        for (key, value) in content.usage.byMode {
            let group = ReportModeGroup(source: key.model.source, attribution: key.attribution)
            byId[key.model.modelId]?.modes[group, default: .zero] += value
        }
        for (key, count) in sample.facts.generatingModels where count > 0 {
            byId[key.modelId, default: ModelEntry(id: key.modelId)].generating += count
            byId[key.modelId]?.sources.insert(key.source)
        }
        let perf = sample.facts.perf
        if scores == .perfScore {
            for (id, model) in perf.models where !model.liveSources.isEmpty {
                byId[id, default: ModelEntry(id: id, perfOnly: true)].sources.formUnion(model.liveSources)
            }
            for (id, rows) in content.perfStats where !rows.isEmpty {
                byId[id, default: ModelEntry(id: id, perfOnly: true)].sources.formUnion(rows.keys)
                byId[id]?.perfRows = rows
            }
        }
        if scores != .none {
            for id in byId.keys {
                guard let model = perf.models[id] else { continue }
                byId[id]?.perfValue = model.value
                byId[id]?.thinking = model.thinking
            }
        }
        var entries = byId.values.sorted { lhs, rhs in
            lhs.usage.output != rhs.usage.output ? lhs.usage.output > rhs.usage.output : lhs.id < rhs.id
        }
        if entries.count > maxModels {
            var other = ModelEntry(id: ReportModelIds.other)
            other.perfOnly = entries[(maxModels - 1)...].allSatisfy(\.perfOnly)
            for entry in entries[(maxModels - 1)...] {
                other.usage += entry.usage
                other.generating += entry.generating
                other.sources.formUnion(entry.sources)
                for (source, usage) in entry.usageBySource { other.usageBySource[source, default: .zero] += usage }
                for (key, usage) in entry.modes { other.modes[key, default: .zero] += usage }
                other.perfValue.merge(entry.perfValue)
                other.thinking += entry.thinking
                for (source, row) in entry.perfRows { other.perfRows[source, default: PerfRecord()].add(row) }
            }
            entries = Array(entries.prefix(maxModels - 1)) + [other]
        }
        return entries
    }

    private static func modelJSON(_ entry: ModelEntry, withCost: Bool, extensions: ReportExtensions,
                                  scores: ReportScoreMode) -> ReportJSON {
        var members: [ReportJSON.Member] = [
            .init("id", .string(entry.id)),
            .init("sources", .array(SourceKind.allCases.filter(entry.sources.contains).map { .string($0.rawValue) })),
            .init("requests", .int(entry.usage.requests)),
            .init("output", .int(entry.usage.output)),
            .init("measuredOutput", .int(entry.usage.measuredOutput)),
            .init("measuredMs", .int(entry.usage.measuredMs)),
            .init("generating", .int(min(entry.generating, maxCount))),
        ]
        if withCost { members.append(.init("deltaMicroUsd", .int(entry.usage.costMicroUsd))) }
        if extensions.modelHarnessRequests, let rows = ReportModelBreakdown.harness(entry.usageBySource, total: entry.usage) {
            members.append(.init("harnessRequests", rows))
        }
        if extensions.executionModes, !entry.perfOnly,
           let rows = ReportModelBreakdown.modes(entry.usageBySource, groups: entry.modes,
                                                 version: extensions.executionModesVersion) {
            members.append(.init("modeUsage", rows))
        }
        switch scores {
        case .perfScore:
            members.append(.init("perfScore", ReportPerfScoreValue(entry.perfValue, thinking: entry.thinking).json))
            let rows = SourceKind.allCases.compactMap { source in entry.perfRows[source].map { $0.reportJSON(source: source) } }
            members.append(.init("perfStats", .array(rows)))
        case .legacyTaskScore:
            members.append(.init("taskScore", ReportPerfScoreValue.legacy(thinking: entry.thinking).json))
            members.append(.init("taskStats", .array([])))
        case .none:
            break
        }
        return .object(members)
    }

    /// The score objects the report would carry, rounded as sent, without the size rule; nil while neither extension
    /// is active (the changed rule of METRICS 11.10 *WebSocket*, *Legacy*).
    public static func perfScoreValues(_ content: ReportWindowContent, _ sample: ReportSample,
                                       _ options: ReportOptions) -> ReportPerfScoreValues? {
        let scores = options.scoreMode
        guard scores != .none else { return nil }
        let legacy = scores == .legacyTaskScore
        let perf = sample.facts.perf
        func value(_ score: PerfSetValue, _ thinking: Double) -> ReportPerfScoreValue {
            legacy ? .legacy(thinking: thinking) : ReportPerfScoreValue(score, thinking: thinking)
        }
        var entries: [String: ReportPerfScoreValue] = [:]
        for entry in modelEntries(content, sample, maxModels: options.maxModels, scores: scores) {
            entries[entry.id] = value(entry.perfValue, entry.thinking)
        }
        return ReportPerfScoreValues(top: value(perf.value, perf.thinking), entries: entries, legacy: legacy)
    }

    // MARK: - Values

    private static func delta(_ usage: ReportUsage) -> ReportJSON {
        .object([
            "requests": .int(usage.requests), "output": .int(usage.output), "input": .int(usage.input),
            "cachedInput": .int(usage.cachedInput), "cacheWriteInput": .int(usage.cacheWriteInput),
            "reasoning": .int(usage.reasoning), "measuredOutput": .int(usage.measuredOutput),
            "measuredMs": .int(usage.measuredMs),
        ])
    }

    private static func counts(_ agents: ReportAgentCounts) -> ReportJSON {
        .object(["generating": .int(min(agents.generating, maxCount)), "tools": .int(min(agents.tools, maxCount)),
                 "active": .int(min(agents.active, maxCount))])
    }

    static func rounded(_ value: Double, _ decimals: Int) -> Double {
        guard value.isFinite else { return 0 }
        let scale = pow(10, Double(decimals))
        return (value * scale).rounded(.toNearestOrAwayFromZero) / scale
    }

    private static func tokS(_ value: Double) -> ReportJSON {
        .decimal(min(max(value.isFinite ? value : 0, 0), maxTokS), decimals: 1)
    }

    private static func share(_ value: Double) -> ReportJSON {
        .decimal(min(max(value.isFinite ? value : 0, 0), 1), decimals: 3)
    }

    private static func perSecond(_ value: Double) -> ReportJSON {
        .int(Int64(rounded(min(max(0, value), 9e15), 0)))
    }

    private static func bytesPerSecond(_ value: Double) -> Int64 {
        Int64(rounded(min(max(0, value), 1e12), 0))
    }
}
