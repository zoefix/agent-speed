import Foundation

extension QuotaEstimateSpend {
    /// The sources with a quota: only their spend enters the ledger.
    public static let sources: Set<SourceKind> = [.codex, .claudeCode, .claudeDesktop, .grokBuild, .devin, .antigravity,
                                                  .kimi, .gemini, .droid, .zcode, .qoder, .qoderCli, .workbuddy]

    /// One response as the ledger counts it, with the cost it was priced at; nil for a source without a quota.
    init?(_ record: ResponseRecord, cost: Int64?, models: QuotaModelIds) {
        guard Self.sources.contains(record.source) else { return nil }
        let claude = record.source == .claudeCode || record.source == .claudeDesktop
        self.init(source: record.source.rawValue, host: record.host == RemoteHost.localId ? nil : record.host,
                  model: models.id(model: record.model, provider: record.provider), startMs: record.startMs,
                  endMs: record.endMs, costMicroUsd: cost, tokens: Int64(record.usage.total),
                  requests: Int64(record.requestCount), file: record.agentId, provider: record.provider,
                  cloudRoute: claude && models.isCloudRoute(record.model))
    }
}

/// Price-table model ids and the cloud step of METRICS 9.2, remembered per model while one table is current.
public final class QuotaModelIds: @unchecked Sendable {
    private let lock = NSLock()
    private var version: Int?
    private var ids: [String: String] = [:]
    private var cloud: [String: Bool] = [:]
    private let table: () -> PriceTable

    public init(pricing: PriceBook) {
        table = { pricing.current }
    }

    init(table: PriceTable) {
        self.table = { table }
    }

    /// The entry's id when the table knows the model (priced or free), else the model as logged.
    func id(model: String?, provider: String?) -> String {
        guard let model, !model.isEmpty else { return ReportModelIds.unknown }
        let key = "\(provider ?? "")\u{1F}\(model)"
        return cached(\.ids, key) {
            let id = $0.reportModelId(model: model, provider: provider)
            return id == ReportModelIds.unknown || id == ReportModelIds.local ? model.lowercased() : id
        }
    }

    /// A Claude model id in a Bedrock or Vertex form.
    func isCloudRoute(_ model: String?) -> Bool {
        guard let model, !model.isEmpty else { return false }
        return cached(\.cloud, model) { $0.resolver.isCloudForm(model) }
    }

    private func cached<T>(_ path: ReferenceWritableKeyPath<QuotaModelIds, [String: T]>, _ key: String,
                           _ make: (PriceTable) -> T) -> T {
        let current = table()
        return lock.withLock {
            if version != current.version {
                version = current.version
                ids.removeAll()
                cloud.removeAll()
            }
            if let hit = self[keyPath: path][key] { return hit }
            let value = make(current)
            self[keyPath: path][key] = value
            return value
        }
    }
}

extension ModelResolver {
    /// The cloud step changed the key: the model id is in a Bedrock or Vertex form.
    func isCloudForm(_ model: String) -> Bool {
        let steps = candidates(model)
        guard let cloud = steps.firstIndex(where: { $0.step == "cloud" }), cloud > 0 else { return false }
        return steps[cloud].key != steps[cloud - 1].key
    }
}

/// One analysed file's share of the ledger (QUOTAS 10.3): its spend within 8 days per minute, and its Codex token
/// count lines with the response each reports.
public struct QuotaEstimateFile: Equatable, Sendable {
    /// Bumped when the contributions change; files of another version are analysed again when recent.
    public static let version: Int64 = 1

    public var spend: [QuotaEstimateSpend]
    public var lines: [QuotaCodexLine]

    public init(spend: [QuotaEstimateSpend] = [], lines: [QuotaCodexLine] = []) {
        self.spend = spend
        self.lines = lines
    }

    public var isEmpty: Bool { spend.isEmpty && lines.isEmpty }

    /// A file's final responses and token count lines: Codex responses take the line that reports them (at most 5 s
    /// later), the others none; what is no subscription spend is left out; the rest is summed per minute.
    static func make(records: some Sequence<ResponseRecord>, lines: [QuotaCodexLine], pricing: PriceBook,
                     models: QuotaModelIds, labeler: inout DayLabeler, nowMs: Int64) -> QuotaEstimateFile {
        let keep = nowMs - QuotaEstimateConstants.keepMs
        var spend: [QuotaEstimateSpend] = []
        for record in records where record.endMs >= keep && DayLabeler.hasDay(record.endMs) {
            let cost = pricing.cost(of: record, day: labeler.label(forMs: record.endMs))
            if let item = QuotaEstimateSpend(record, cost: cost, models: models) { spend.append(item) }
        }
        var paired = lines.filter { !$0.windows.isEmpty }.stableSorted { $0.ts < $1.ts }
        if !paired.isEmpty {
            let order = spend.indices.filter { spend[$0].source == "codex" }.stableSorted { spend[$0].endMs < spend[$1].endMs }
            var taken: Set<Int> = []
            for at in paired.indices {
                let ts = paired[at].ts
                var low = 0, high = order.count
                while low < high {
                    let mid = (low + high) / 2
                    if spend[order[mid]].endMs < ts - QuotaEstimateConstants.lineMatchMs { low = mid + 1 } else { high = mid }
                }
                while low < order.count, spend[order[low]].endMs <= ts, taken.contains(order[low]) { low += 1 }
                guard low < order.count, spend[order[low]].endMs <= ts else { continue }
                let i = order[low]
                taken.insert(i)
                spend[i].codexLine = paired[at].id
                paired[at].tLo = min(ts, spend[i].startMs ?? ts)
            }
        }
        spend = spend.filter { $0.isSubscription && ($0.source != "codex" || $0.codexLine != nil) }
        return QuotaEstimateFile(spend: Self.perMinute(spend), lines: paired.filter { $0.ts >= keep })
    }

    /// Sums responses that share source, model, minute, line and pricing.
    static func perMinute(_ spend: [QuotaEstimateSpend]) -> [QuotaEstimateSpend] {
        struct Key: Hashable {
            var source: String
            var model: String
            var minute: Int64
            var line: QuotaCodexLine.ID?
            var priced: Bool
        }
        var order: [Key] = []
        var sums: [Key: QuotaEstimateSpend] = [:]
        for s in spend {
            let key = Key(source: s.source, model: s.model, minute: QuotaEstimateLedger.minute(of: s.endMs),
                          line: s.codexLine, priced: s.costMicroUsd != nil)
            guard var sum = sums[key] else {
                order.append(key)
                var first = s
                first.provider = nil
                first.cloudRoute = false
                sums[key] = first
                continue
            }
            sum.costMicroUsd = sum.costMicroUsd.map { $0 + (s.costMicroUsd ?? 0) }
            sum.tokens += s.tokens
            sum.requests += s.requests
            sum.endMs = max(sum.endMs, s.endMs)
            if let start = s.startMs { sum.startMs = min(sum.startMs ?? start, start) }
            sums[key] = sum
        }
        return order.map { sums[$0]! }
    }
}

/// The history's share of the ledger at the end of a complete analysis: every file's, and the moment before which
/// it holds the spend; the live engine's responses hold what ended later.
public struct QuotaEstimateHistory: Sendable {
    public var files: [String: QuotaEstimateFile]
    /// A minute boundary: spend that ended before it comes from the history.
    public var cutMs: Int64

    public init(files: [String: QuotaEstimateFile], cutMs: Int64) {
        self.files = files
        self.cutMs = cutMs
    }

    /// Two minutes before the analysis started, on a minute boundary.
    public static func cut(analysisStartMs: Int64) -> Int64 {
        QuotaEstimateLedger.minute(of: analysisStartMs - QuotaEstimateConstants.lagMs)
    }
}
