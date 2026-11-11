import Foundation

/// One estimate run over the inputs at `nowMs` (QUOTAS 10.1–10.6): accounts, the ledger, observations, instances
/// and the estimate of every cap class. Immutable once made; its ledger answers sums from any thread.
public final class QuotaEstimateBuild: @unchecked Sendable {
    public let nowMs: Int64
    let salt: Data
    let priceVersion: Int
    let inputs: QuotaEstimateInputs
    let timelines: QuotaTimelines
    /// Resolved host timelines.
    let hostTimelines: [QuotaEstimateSource: [QuotaKeyEntry]]
    let ledger: QuotaEstimateLedger
    /// The account each spend input went to; nil when it is no subscription spend.
    public let spendAccounts: [String?]
    let observations: [QuotaObservation]
    let codex: QuotaCodexAccounts
    let instances: [QuotaBuiltInstance]
    public let estimates: [QuotaCapClass: QuotaCapEstimate]
    /// The newest instance of every account and window, in report order.
    let current: [QuotaEntryBase]
    /// The account of Codex's newest token count line.
    let newestCodexAccount: String?

    public init(_ inputs: QuotaEstimateInputs, salt: Data, nowMs: Int64) {
        self.nowMs = nowMs
        self.salt = salt
        priceVersion = inputs.priceVersion
        self.inputs = inputs
        let timelines = QuotaTimelines(inputs: inputs)
        self.timelines = timelines
        let codex = QuotaCodexAccounts(lines: inputs.codexLines, spend: inputs.spend,
                                       keys: timelines.local["codex"] ?? [], salt: salt)
        self.codex = codex
        newestCodexAccount = codex.lines.max { $0.line.ts < $1.line.ts }?.account

        var accounts = [String?](repeating: nil, count: inputs.spend.count)
        for (i, s) in inputs.spend.enumerated() where s.host == nil {
            guard s.isSubscription else { continue }
            switch s.source {
            case "codex":
                accounts[i] = codex.lineOfSpend[i].map { codex.lines[$0].account }
            case "claudeCode", "claudeDesktop":
                accounts[i] = QuotaKeyEntry.inForce(timelines.local[s.source] ?? [], at: s.endMs)?.key
            default:
                accounts[i] = QuotaTimelines.ownKey(QuotaEstimateSource(source: s.source), salt: salt)
            }
        }

        var observations: [QuotaObservation] = []
        for line in codex.lines {
            for w in line.line.windows {
                observations.append(QuotaObservation(
                    account: line.account, source: "codex", kind: w.kind, name: w.name, minutes: w.minutes ?? 0,
                    t: line.line.ts, tLo: line.tLo, p: w.usedPercent, resetsMs: w.resetsAtMs!, plan: line.plan))
            }
        }
        let claudeStarts = Self.claudeCodeStarts(inputs.spend, accounts)
        for s in inputs.snapshots where s.host == nil {
            let entry: QuotaKeyEntry
            switch s.source {
            case "codex", "claudeCode", "claudeDesktop":
                guard let found = QuotaKeyEntry.inForce(timelines.local[s.source] ?? [], at: s.observedMs) else { continue }
                entry = found
            default:
                entry = QuotaKeyEntry(at: 0, key: QuotaTimelines.ownKey(QuotaEstimateSource(source: s.source), salt: salt))
            }
            let tLo = s.source == "claudeCode" ? claudeStarts.earliest(account: entry.key, before: s.observedMs) : s.observedMs
            observations += Self.observations(of: s, entry: entry, tLo: tLo)
        }

        // Remote hosts: their keys, then their spend and values.
        var hostSnapshots: [QuotaEstimateSource: [QuotaEstimateSnapshot]] = [:]
        for s in inputs.snapshots where s.host != nil {
            hostSnapshots[QuotaEstimateSource(source: s.source, host: s.host), default: []].append(s)
        }
        var hostSources = Set(hostSnapshots.keys)
        for s in inputs.spend where s.host != nil { hostSources.insert(QuotaEstimateSource(source: s.source, host: s.host)) }
        let localForHosts = observations + inputs.stored.observations.compactMap(\.observation)
        var hostTimelines: [QuotaEstimateSource: [QuotaKeyEntry]] = [:]
        for source in hostSources {
            hostTimelines[source] = QuotaTimelines.host(source, snapshots: hostSnapshots[source] ?? [],
                                                        local: localForHosts, salt: salt)
        }
        self.hostTimelines = hostTimelines
        for (i, s) in inputs.spend.enumerated() where s.host != nil && s.isSubscription {
            accounts[i] = QuotaKeyEntry.inForce(hostTimelines[QuotaEstimateSource(source: s.source, host: s.host)] ?? [],
                                                at: s.endMs)?.key
        }
        for s in inputs.snapshots where s.host != nil {
            guard let entry = QuotaKeyEntry.inForce(hostTimelines[QuotaEstimateSource(source: s.source, host: s.host)] ?? [],
                                                    at: s.observedMs) else { continue }
            observations += Self.observations(of: s, entry: entry, tLo: s.observedMs)
        }

        // Stored values count once; a stored Desktop value takes the key Desktop spends on at its time.
        var seen = Set(observations.map(\.identity))
        let desktop = timelines.local["claudeDesktop"] ?? []
        for stored in inputs.stored.observations {
            guard var o = stored.observation else { continue }
            if o.source == "claudeDesktop", let entry = QuotaKeyEntry.inForce(desktop, at: o.t) {
                o.account = entry.key
                o.plan = entry.plan
                o.tier = entry.tier
            }
            if seen.insert(o.identity).inserted { observations.append(o) }
        }

        let ledger = QuotaEstimateLedger()
        for (i, s) in inputs.spend.enumerated() {
            guard let account = accounts[i] else { continue }
            ledger.add(account: account, source: s.source, model: s.model, endMs: s.endMs, cost: s.costMicroUsd,
                       requests: s.requests, tokens: s.tokens)
        }
        self.ledger = ledger
        spendAccounts = accounts
        self.observations = observations

        let instances = Self.instances(observations, ledger: ledger, nowMs: nowMs)
        self.instances = instances
        let estimates = Self.estimates(instances, stored: inputs.stored.instances, nowMs: nowMs)
        self.estimates = estimates
        current = Self.current(instances, estimates: estimates, spend: inputs.spend, accounts: accounts,
                               observations: observations, nowMs: nowMs)
        // The sums lines and reports read are ready before the build leaves this queue.
        for base in current { _ = ledger.outer([base.account], base.modelName, 0, 0) }
    }

    private static func observations(of s: QuotaEstimateSnapshot, entry: QuotaKeyEntry, tLo: Int64) -> [QuotaObservation] {
        s.windows.compactMap { w in
            guard let reset = w.resetsAtMs, let minutes = w.minutes, w.usedPercent.isFinite else { return nil }
            return QuotaObservation(account: entry.key, source: s.source, kind: w.kind, name: w.name, minutes: minutes,
                                    t: s.observedMs, tLo: tLo, p: w.usedPercent, resetsMs: reset, plan: entry.plan,
                                    tier: entry.tier)
        }
    }

    /// The CLI's responses by account, for `tLo`: the start of the earliest response that started before `t` and
    /// ended at or after `t − λ`.
    private struct ClaudeStarts {
        var byAccount: [String: [(start: Int64, end: Int64)]] = [:]
        var longest: [String: Int64] = [:]

        func earliest(account: String, before t: Int64) -> Int64 {
            guard let list = byAccount[account] else { return t }
            let from = t - QuotaEstimateConstants.lagMs
            var low = 0, high = list.count
            while low < high {
                let mid = (low + high) / 2
                if list[mid].end < from { low = mid + 1 } else { high = mid }
            }
            var best = t
            let limit = t + (longest[account] ?? 0)
            var i = low
            while i < list.count, list[i].end <= limit {
                if list[i].start < t { best = min(best, list[i].start) }
                i += 1
            }
            return best
        }
    }

    private static func claudeCodeStarts(_ spend: [QuotaEstimateSpend], _ accounts: [String?]) -> ClaudeStarts {
        var starts = ClaudeStarts()
        for (i, s) in spend.enumerated() where s.source == "claudeCode" && s.host == nil {
            guard let account = accounts[i], let start = s.startMs else { continue }
            starts.byAccount[account, default: []].append((start, s.endMs))
            starts.longest[account] = max(starts.longest[account] ?? 0, s.endMs - start)
        }
        for account in starts.byAccount.keys { starts.byAccount[account]!.sort { $0.end < $1.end } }
        return starts
    }
}

/// An instance with its account, plan, class and interval.
struct QuotaBuiltInstance {
    var account: String
    var window: String
    var plan: String?
    var tier: String?
    var capClass: QuotaCapClass
    var instance: QuotaInstance
    var bounds: QuotaInstanceBounds?
    var modelName: String?
    var closed: Bool
}

extension QuotaEstimateBuild {
    /// Per (account, window, plan, tier), in the order the observations name them.
    static func instances(_ observations: [QuotaObservation], ledger: QuotaEstimateLedger,
                          nowMs: Int64) -> [QuotaBuiltInstance] {
        struct GroupKey: Hashable {
            var account: String
            var window: String
            var plan: String?
            var tier: String?
        }
        var order: [GroupKey] = []
        var groups: [GroupKey: [QuotaObservation]] = [:]
        for o in observations where o.t >= nowMs - QuotaEstimateConstants.keepMs {
            let key = GroupKey(account: o.account, window: o.windowID, plan: o.plan, tier: o.tier)
            if groups[key] == nil { order.append(key) }
            groups[key, default: []].append(o)
        }
        var out: [QuotaBuiltInstance] = []
        for key in order {
            let group = groups[key]!
            let first = group[0]
            let name = QuotaEstimateWindow.modelName(kind: first.kind, name: first.name)
            let vendorPlan = key.plan.map { "\(QuotaAccounts.vendor(of: first.source))/\($0)" }
            let capClass = QuotaCapClass.of(account: key.account, vendorPlan: vendorPlan, tier: key.tier, window: key.window)
            for instance in QuotaInstance.group(group) {
                let segments = QuotaSegments.segments(instance, ledger: ledger, accounts: [key.account], name: name,
                                                      nowMs: nowMs)
                out.append(QuotaBuiltInstance(
                    account: key.account, window: key.window, plan: key.plan, tier: key.tier, capClass: capClass,
                    instance: instance, bounds: QuotaSegments.bounds(segments, resetsMs: instance.resetsMs),
                    modelName: name, closed: nowMs >= instance.resetsMs))
            }
        }
        return out
    }

    /// Every class's estimate over its current and stored instances.
    static func estimates(_ instances: [QuotaBuiltInstance], stored: [QuotaEstimateStore.Instance],
                          nowMs: Int64) -> [QuotaCapClass: QuotaCapEstimate] {
        var items: [QuotaCapClass: [QuotaInstanceBounds]] = [:]
        struct Have: Hashable {
            var capClass: QuotaCapClass
            var account: String
            var resetsMs: Int64
        }
        var have: Set<Have> = []
        for x in instances {
            guard let bounds = x.bounds else { continue }
            items[x.capClass, default: []].append(bounds)
            have.insert(Have(capClass: x.capClass, account: x.account, resetsMs: x.instance.resetsMs))
        }
        for s in stored {
            guard let capClass = s.capClass, !have.contains(Have(capClass: capClass, account: s.account, resetsMs: s.R))
            else { continue }
            items[capClass, default: []].append(s.bounds)
        }
        return items.mapValues { QuotaVote.pruned($0) }.reduce(into: [:]) { out, pair in
            out[pair.key] = QuotaVote.estimate(pair.value, nowMs: nowMs,
                                               halfLifeMs: QuotaEstimateWindow.halfLifeMs(minutes: pair.key.minutes))
        }
    }
}
