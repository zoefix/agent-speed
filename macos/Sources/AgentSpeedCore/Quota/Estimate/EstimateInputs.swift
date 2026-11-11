import Foundation

/// Spend of one response, or of a file's responses ending in one minute (QUOTAS 10.3): the history keeps per-file
/// per-minute contributions.
public struct QuotaEstimateSpend: Equatable, Sendable {
    public var source: String
    /// The remote host's id; nil on this computer.
    public var host: String?
    /// The price-table model id (the logged model when unpriced).
    public var model: String
    /// The earliest start; nil while unknown.
    public var startMs: Int64?
    /// The latest end.
    public var endMs: Int64
    /// Nil when unpriced.
    public var costMicroUsd: Int64?
    /// Input plus output tokens.
    public var tokens: Int64
    public var requests: Int64
    /// The transcript, for Codex's token count lines.
    public var file: String?
    /// The token count line that reports a Codex response, when the history already paired them.
    public var codexLine: QuotaCodexLine.ID?
    /// Codex's `model_provider`.
    public var provider: String?
    /// A Claude model id in a Bedrock or Vertex form.
    public var cloudRoute: Bool

    public init(source: String, host: String? = nil, model: String, startMs: Int64?, endMs: Int64, costMicroUsd: Int64?,
                tokens: Int64 = 0, requests: Int64 = 1, file: String? = nil, codexLine: QuotaCodexLine.ID? = nil,
                provider: String? = nil, cloudRoute: Bool = false) {
        self.source = source
        self.host = host
        self.model = model
        self.startMs = startMs
        self.endMs = endMs
        self.costMicroUsd = costMicroUsd
        self.tokens = tokens
        self.requests = requests
        self.file = file
        self.codexLine = codexLine
        self.provider = provider
        self.cloudRoute = cloudRoute
    }

    /// Codex responses of another provider and Claude models in a Bedrock or Vertex form are no subscription spend.
    public var isSubscription: Bool {
        switch source {
        case "codex": provider == nil || provider == "openai"
        case "claudeCode", "claudeDesktop": !cloudRoute
        default: true
        }
    }
}

/// A Codex `token_count` line with rate limits (QUOTAS 3, 10.1, 10.4).
public struct QuotaCodexLine: Equatable, Sendable {
    public struct ID: Hashable, Sendable {
        public var file: String
        public var ts: Int64

        public init(file: String, ts: Int64) {
            self.file = file
            self.ts = ts
        }
    }

    public var file: String
    public var ts: Int64
    /// The windows with a reset.
    public var windows: [QuotaWindow]
    /// `plan_type` as written.
    public var plan: String?
    /// The start of the response the line follows, when the history paired them.
    public var tLo: Int64?

    public init(file: String, ts: Int64, windows: [QuotaWindow], plan: String?, tLo: Int64? = nil) {
        self.file = file
        self.ts = ts
        self.windows = windows.filter { $0.resetsAtMs != nil && $0.resetsAtMs != 0 && $0.minutes != nil }
        self.plan = plan
        self.tLo = tLo
    }

    public var id: ID { ID(file: file, ts: ts) }
}

/// A source's account key from a certain moment (QUOTAS 10.1 *Key timeline*).
public struct QuotaKeyEntry: Hashable, Sendable {
    public var at: Int64
    public var key: String
    public var plan: String?
    public var tier: String?

    public init(at: Int64, key: String, plan: String? = nil, tier: String? = nil) {
        self.at = at
        self.key = key
        self.plan = plan
        self.tier = tier
    }

    /// The reference's order: `at`, key, then the plan and tier as Python prints them.
    static func ordered(_ entries: some Sequence<QuotaKeyEntry>) -> [QuotaKeyEntry] {
        Set(entries).sorted { ($0.at, $0.key, $0.plan ?? "None", $0.tier ?? "None") < ($1.at, $1.key, $1.plan ?? "None", $1.tier ?? "None") }
    }

    /// The entry in force at `at`: the last one at or before it, else the first one.
    static func inForce(_ timeline: [QuotaKeyEntry], at: Int64) -> QuotaKeyEntry? {
        guard let first = timeline.first else { return nil }
        var low = 0, high = timeline.count
        while low < high {
            let mid = (low + high) / 2
            if timeline[mid].at <= at { low = mid + 1 } else { high = mid }
        }
        return low > 0 ? timeline[low - 1] : first
    }
}

/// A Claude CLI profile as `.claude.json` gives it, already keyed: ids never reach the estimator's state.
public struct QuotaClaudeProfile: Hashable, Sendable {
    public var entry: QuotaKeyEntry
    /// The organization's `claudeDesktop` key.
    public var orgKey: String

    public init(at: Int64, organization: String, account: String, organizationType: String?, tier: String?, salt: Data) {
        entry = QuotaKeyEntry(at: at, key: QuotaAccounts.key(salt: salt, vendor: "claude", id: "\(organization):\(account)"),
                              plan: QuotaAccounts.cleanPlan(organizationType), tier: QuotaAccounts.cleanPlan(tier))
        orgKey = QuotaAccounts.key(salt: salt, vendor: "claudeDesktop", id: organization.lowercased())
    }

    public init(entry: QuotaKeyEntry, orgKey: String) {
        self.entry = entry
        self.orgKey = orgKey
    }
}

/// One quota snapshot as the estimator sees it (QUOTAS 10.4 *Sources of observations*).
public struct QuotaEstimateSnapshot: Equatable, Sendable {
    public var source: String
    public var host: String?
    public var observedMs: Int64
    public var windows: [QuotaWindow]
    /// Claude Desktop's organization, as its `claudeDesktop` key.
    public var orgKey: String?
    /// A remote host's account key, plan and tier from its relay (REMOTE 11.1); nil from older relays.
    public var account: String?
    public var plan: String?
    public var tier: String?

    public init(source: String, host: String? = nil, observedMs: Int64, windows: [QuotaWindow], orgKey: String? = nil,
                account: String? = nil, plan: String? = nil, tier: String? = nil) {
        self.source = source
        self.host = host
        self.observedMs = observedMs
        self.windows = windows
        self.orgKey = orgKey
        self.account = account
        self.plan = plan
        self.tier = tier
    }

    var points: [QuotaFingerprintPoint] {
        windows.compactMap { w in
            guard let reset = w.resetsAtMs, let minutes = w.minutes else { return nil }
            return QuotaFingerprintPoint(t: observedMs, window: QuotaEstimateWindow.id(kind: w.kind, name: w.name,
                                                                                       minutes: minutes),
                                         resetsMs: reset, minutes: minutes)
        }
    }
}

/// Everything the estimate is built from.
public struct QuotaEstimateInputs: Sendable {
    public var spend: [QuotaEstimateSpend] = []
    public var codexLines: [QuotaCodexLine] = []
    public var codexKeys: [QuotaKeyEntry] = []
    public var claudeProfiles: [QuotaClaudeProfile] = []
    public var snapshots: [QuotaEstimateSnapshot] = []
    public var stored: QuotaEstimateStore = .empty
    public var priceVersion: Int = 0

    public init() {}
}

/// Where the live engine hands the quota estimate its responses (local and remote) and Codex token counts
/// (QUOTAS 10.3, 10.4). Called on the monitor's queue: implementations only queue the work.
public protocol QuotaSpendSink: AnyObject, Sendable {
    func spend(_ record: ResponseRecord)
    func codexLines(_ lines: [QuotaCodexLine])
}
