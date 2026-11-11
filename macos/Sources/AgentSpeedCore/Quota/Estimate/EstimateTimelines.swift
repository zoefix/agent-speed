import Foundation

/// A source on this computer (`host` nil) or on a remote host.
public struct QuotaEstimateSource: Hashable, Sendable {
    public var source: String
    public var host: String?

    public init(source: String, host: String? = nil) {
        self.source = source
        self.host = host
    }
}

/// The key timelines of QUOTAS 10.1: per source, the key changes seen, with Desktop's keys derived from its
/// organization's timeline and the CLI's.
struct QuotaTimelines {
    /// `codex`, `claudeCode`, the derived `claudeDesktop` and any stored source.
    var local: [String: [QuotaKeyEntry]]
    /// Desktop's own organization keys.
    var desktopOrgs: [QuotaKeyEntry]
    /// The CLI entries' organizations.
    var orgKeys: [QuotaKeyEntry: String]

    init(inputs: QuotaEstimateInputs) {
        var timelines: [String: [QuotaKeyEntry]] = ["codex": inputs.codexKeys]
        var orgKeys: [QuotaKeyEntry: String] = [:]
        for profile in inputs.claudeProfiles.stableSorted(by: { $0.entry.at < $1.entry.at }) {
            timelines["claudeCode", default: []].append(profile.entry)
            orgKeys[profile.entry] = profile.orgKey
        }
        for stored in inputs.stored.keys {
            let entry = QuotaKeyEntry(at: stored.at, key: stored.key, plan: stored.plan, tier: stored.tier)
            timelines[stored.source, default: []].append(entry)
            if stored.source == "claudeCode", let org = stored.orgKey, orgKeys[entry] == nil { orgKeys[entry] = org }
        }
        for snapshot in inputs.snapshots where snapshot.source == "claudeDesktop" && snapshot.host == nil {
            guard let org = snapshot.orgKey else { continue }
            timelines["claudeDesktop", default: []].append(QuotaKeyEntry(at: snapshot.observedMs, key: org))
        }
        timelines = timelines.mapValues { QuotaKeyEntry.ordered($0) }
        if timelines["claudeCode"] == nil { timelines["claudeCode"] = [] }
        self.orgKeys = orgKeys
        desktopOrgs = timelines.removeValue(forKey: "claudeDesktop") ?? []
        local = timelines
        local["claudeDesktop"] = Self.desktop(orgs: desktopOrgs, cli: local["claudeCode"] ?? [], orgKeys: orgKeys,
                                              inputs: inputs)
    }

    /// Every fresh and stored observation of a local Claude source, with the account a stored one names.
    static func points(_ source: String, inputs: QuotaEstimateInputs) -> [(QuotaFingerprintPoint, String?)] {
        var out: [(QuotaFingerprintPoint, String?)] = []
        for snapshot in inputs.snapshots where snapshot.source == source && snapshot.host == nil {
            out += snapshot.points.map { ($0, nil) }
        }
        for o in inputs.stored.observations where o.source == source && o.minutes > 0 {
            out.append((QuotaFingerprintPoint(t: o.t, window: "\(o.kind):\(o.name ?? ""):\(o.minutes)", resetsMs: o.R,
                                              minutes: o.minutes), o.account))
        }
        return out
    }

    /// An entry at each entry of Desktop's organization timeline and of the CLI's: the CLI's key while the
    /// organization matches, the plan is personal and the fingerprint confirms it, else Desktop's own key.
    private static func desktop(orgs: [QuotaKeyEntry], cli: [QuotaKeyEntry], orgKeys: [QuotaKeyEntry: String],
                                inputs: QuotaEstimateInputs) -> [QuotaKeyEntry] {
        guard !orgs.isEmpty else { return [] }
        let desk = points("claudeDesktop", inputs: inputs).map(\.0)
        var cliByKey: [String: [QuotaFingerprintPoint]] = [:]
        for (point, account) in points("claudeCode", inputs: inputs) {
            guard let key = account ?? QuotaKeyEntry.inForce(cli, at: point.t)?.key else { continue }
            cliByKey[key, default: []].append(point)
        }
        var prints: [String: QuotaAccountFingerprint] = [:]
        var out: [QuotaKeyEntry] = []
        for at in Set(orgs.map(\.at) + cli.map(\.at)).sorted() {
            let own = QuotaKeyEntry.inForce(orgs, at: at)!.key
            if let c = QuotaKeyEntry.inForce(cli, at: at) {
                let print = prints[c.key] ?? QuotaAccountFingerprint(desk, cliByKey[c.key] ?? [])
                prints[c.key] = print
                if QuotaAccounts.desktopSharesCli(desktopOrg: own, cliOrg: orgKeys[c], cliOrgType: c.plan,
                                                  fingerprint: print.decision(at: at)) {
                    out.append(QuotaKeyEntry(at: at, key: c.key, plan: c.plan, tier: c.tier))
                    continue
                }
            }
            out.append(QuotaKeyEntry(at: at, key: own))
        }
        return out
    }

    /// A remote host's sources (REMOTE 11.1): the relay's key when it gives one, kept apart from an equal local key
    /// that the fingerprint contradicts; older relays' sources are accounts of their own.
    static func host(_ source: QuotaEstimateSource, snapshots: [QuotaEstimateSnapshot],
                     local: [QuotaObservation], salt: Data, install: String) -> [QuotaKeyEntry] {
        let host = source.host ?? ""
        let entries = QuotaKeyEntry.ordered(snapshots.compactMap { s in
            s.account.map { QuotaKeyEntry(at: s.observedMs, key: $0, plan: QuotaAccounts.cleanPlan(s.plan),
                                          tier: QuotaAccounts.cleanPlan(s.tier)) }
        })
        guard !entries.isEmpty else {
            return [QuotaKeyEntry(at: 0, key: ownKey(source, salt: salt, install: install))]
        }
        let hostPoints = snapshots.flatMap(\.points)
        var prints: [String: QuotaAccountFingerprint?] = [:]
        return entries.map { entry in
            let print: QuotaAccountFingerprint? = prints[entry.key] ?? {
                let points = local.filter { $0.account == entry.key }.map {
                    QuotaFingerprintPoint(t: $0.t, window: $0.windowID, resetsMs: $0.resetsMs, minutes: $0.minutes)
                }
                return points.isEmpty ? nil : QuotaAccountFingerprint(hostPoints, points)
            }()
            prints[entry.key] = print
            guard let print, !QuotaAccounts.keysShare(fingerprint: print.decision(at: entry.at)) else { return entry }
            var apart = entry
            apart.key = QuotaAccounts.key(salt: salt, vendor: "host", id: "\(host):\(source.source):\(entry.key)")
            return apart
        }
    }

    /// The account of a host source that names none, or of a source without ids: the pair (this computer or the
    /// host, source), keyed with the install id so that it stays apart between computers.
    static func ownKey(_ source: QuotaEstimateSource, salt: Data, install: String) -> String {
        QuotaAccounts.pairKey(salt: salt, install: install, machine: source.host ?? RemoteHost.localId, source: source.source)
    }
}
