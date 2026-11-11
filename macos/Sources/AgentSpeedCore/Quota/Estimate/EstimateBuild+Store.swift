import Foundation

extension QuotaEstimateBuild {
    /// The file to write (QUOTAS 10.4): the last 8 days of observations thinned per instance, the closed instances'
    /// intervals (64 per class, newest kept) and the key timelines, Desktop's as its own organization keys.
    public func store() -> QuotaEstimateStore {
        let keep = nowMs - QuotaEstimateConstants.keepMs
        var groups: [String: [QuotaObservation]] = [:]
        for o in observations where o.t >= keep {
            let slot = (Double(o.resetsMs) / Double(QuotaEstimateConstants.instanceJoinMs)).rounded(.toNearestOrEven)
            let key = QuotaPythonText.tuple([.text(o.account), .text(o.kind.rawValue), .text(o.name), .int(Int64(o.minutes)),
                                             .int(Int64(slot)), .text(o.plan), .text(o.tier)])
            groups[key, default: []].append(o)
        }
        var stored: [QuotaEstimateStore.Observation] = []
        for key in groups.keys.sorted() {
            stored += QuotaEstimateStore.thin(groups[key]!).map(QuotaEstimateStore.Observation.init)
        }

        struct Have: Hashable {
            var list: [String?]
            var account: String
            var resetsMs: Int64
        }
        var closed: [String: [QuotaEstimateStore.Instance]] = [:]
        var have: Set<Have> = []
        for x in instances where x.closed {
            guard let b = x.bounds else { continue }
            closed[x.capClass.sortKey, default: []].append(QuotaEstimateStore.Instance(
                class: x.capClass.list, account: x.account, R: x.instance.resetsMs, lo: b.lo,
                hi: b.hi.isFinite ? b.hi : nil, pMax: b.pMax, tLast: b.tLast))
            have.insert(Have(list: x.capClass.list, account: x.account, resetsMs: x.instance.resetsMs))
        }
        for s in inputs.stored.instances where !have.contains(Have(list: s.class, account: s.account, resetsMs: s.R)) {
            let key = QuotaCapClass(list: s.class)?.sortKey ?? QuotaPythonText.tuple(s.class.map { .text($0) })
            closed[key, default: []].append(s)
        }
        var instances: [QuotaEstimateStore.Instance] = []
        for key in closed.keys.sorted() {
            instances += closed[key]!.stableSorted { $0.tLast < $1.tLast }.suffix(QuotaEstimateConstants.instancesPerClass)
        }

        var lists = timelines.local
        lists["claudeDesktop"] = timelines.desktopOrgs
        var keys: [QuotaEstimateStore.Key] = []
        for source in lists.keys.sorted() {
            let entries = lists[source]!
            let older = entries.lastIndex { $0.at < keep }
            for (i, e) in entries.enumerated() where e.at >= keep || i == older {
                keys.append(QuotaEstimateStore.Key(source: source, at: e.at, key: e.key, plan: e.plan, tier: e.tier,
                                                   orgKey: source == "claudeCode" ? timelines.orgKeys[e] : nil))
            }
        }
        return QuotaEstimateStore(observations: stored, instances: instances, keys: keys)
    }
}
