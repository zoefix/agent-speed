import Foundation

/// The plan of an account Claude Desktop spends on as its own, inferred from its windows and caps (QUOTAS 10.2
/// *Inferred plan*): Desktop names no plan, and its credentials stay unread.
public struct QuotaInferredPlan: Hashable, Sendable {
    public enum Rule: String, Sendable {
        /// A weekly window and no 5-hour window.
        case shape
        /// Its 5-hour cap against a Claude plan's on this computer.
        case ratio
    }

    public var plan: String
    public var tier: String?
    public var rule: Rule

    public init(plan: String, tier: String?, rule: Rule) {
        self.plan = plan
        self.tier = tier
        self.rule = rule
    }
}

/// The rules of QUOTAS 10.2 *Inferred plan*.
enum QuotaPlanInference {
    /// Claude's plans and their 5-hour usage relative to Pro.
    static let plans: [(plan: String, tier: String?, multiplier: Double)] = [
        ("claude_pro", nil, 1), ("claude_max", "default_claude_max_5x", 5), ("claude_max", "default_claude_max_20x", 20),
    ]
    static let tolerance = 1.5
    static let confidences: Set<QuotaEstimateConfidence> = [.high, .medium]
    static let fiveHour = QuotaEstimateWindow.id(kind: .fiveHour, name: nil, minutes: 300)
    static let vendorPrefix = "anthropic/"

    /// A 5-hour cap class of a Claude plan.
    struct Anchor {
        var plan: String
        var tier: String?
        var estimate: QuotaCapEstimate
    }

    /// Pro of any tier is 1; Max only with a known tier.
    static func multiplier(plan: String, tier: String?) -> Double? {
        if plan == "claude_pro" { return 1 }
        return plans.first { $0.plan == plan && $0.tier == tier }?.multiplier
    }

    static func usable(_ estimate: QuotaCapEstimate?) -> Bool {
        guard let estimate, confidences.contains(estimate.confidence), let cap = estimate.cap else { return false }
        return cap > 0
    }

    /// `kinds`: the windows of the account's newest snapshot; `own`: its 5-hour estimate; `anchors` sorted by plan,
    /// then tier with null first.
    static func infer(kinds: [QuotaWindowKind], own: QuotaCapEstimate?, anchors: [Anchor]) -> QuotaInferredPlan? {
        if kinds.contains(.sevenDay), !kinds.contains(.fiveHour) {
            return QuotaInferredPlan(plan: "claude_ultra", tier: nil, rule: .shape)
        }
        guard usable(own), let cap = own?.cap else { return nil }
        let limit = log(tolerance)
        var best: (distance: Double, plan: String, tier: String?)?
        for anchor in anchors {
            guard let m = multiplier(plan: anchor.plan, tier: anchor.tier), usable(anchor.estimate),
                  let anchorCap = anchor.estimate.cap else { continue }
            let scaled = m * cap / anchorCap
            for candidate in plans {
                let distance = abs(log(scaled / candidate.multiplier))
                if distance <= limit, best.map({ distance < $0.distance }) ?? true {
                    best = (distance, candidate.plan, candidate.tier)
                }
            }
        }
        return best.map { QuotaInferredPlan(plan: $0.plan, tier: $0.tier, rule: .ratio) }
    }

    /// Every 5-hour class of a Claude plan, in the reference's order.
    static func anchors(_ estimates: [QuotaCapClass: QuotaCapEstimate]) -> [Anchor] {
        estimates.compactMap { capClass, estimate -> Anchor? in
            guard case .plan(let vendorPlan, let tier, let window) = capClass, window == fiveHour,
                  vendorPlan.hasPrefix(vendorPrefix) else { return nil }
            return Anchor(plan: String(vendorPlan.dropFirst(vendorPrefix.count)), tier: tier, estimate: estimate)
        }.sorted { ($0.plan, $0.tier ?? "") < ($1.plan, $1.tier ?? "") }
    }
}

extension QuotaEstimateBuild {
    /// QUOTAS 10.2: the inferred plan of each account Claude Desktop spends on as its own, unless the CLI names that
    /// organization a non-personal one; keyed by account.
    static func inferredPlans(timelines: QuotaTimelines, snapshots: [QuotaEstimateSnapshot],
                              estimates: [QuotaCapClass: QuotaCapEstimate]) -> [String: QuotaInferredPlan] {
        let own = Set(timelines.desktopOrgs.map(\.key))
        guard !own.isEmpty else { return [:] }
        var team: Set<String> = []
        for (entry, org) in timelines.orgKeys {
            if let type = entry.plan, !QuotaAccounts.personalClaudePlans.contains(type) { team.insert(org) }
        }
        var newest: [String: QuotaEstimateSnapshot] = [:]
        for s in snapshots where s.source == "claudeDesktop" && s.host == nil {
            guard let org = s.orgKey else { continue }
            if let current = newest[org], s.observedMs < current.observedMs { continue }
            newest[org] = s
        }
        let anchors = QuotaPlanInference.anchors(estimates)
        var out: [String: QuotaInferredPlan] = [:]
        for key in own.subtracting(team) {
            let kinds = newest[key]?.windows.map(\.kind) ?? []
            let estimate = estimates[.account(key, window: QuotaPlanInference.fiveHour)]
            if let plan = QuotaPlanInference.infer(kinds: kinds, own: estimate, anchors: anchors) { out[key] = plan }
        }
        return out
    }
}
