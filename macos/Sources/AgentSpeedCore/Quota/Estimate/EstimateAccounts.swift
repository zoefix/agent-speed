import CryptoKit
import Foundation

/// The constants of QUOTAS 10.
public enum QuotaEstimateConstants {
    public static let minuteMs: Int64 = 60_000
    public static let hourMs: Int64 = 3_600_000
    public static let dayMs: Int64 = 86_400_000
    static let lagMs: Int64 = 2 * minuteMs
    public static let keepMs: Int64 = 8 * dayMs
    static let hiPenalty = 0.25
    static let fullStep = 20.0
    static let minWeight = 0.05
    static let segmentsPerInstance = 64
    static let instancesPerClass = 64
    static let incrementStep = 10.0
    static let instanceJoinMs: Int64 = 2 * minuteMs
    static let fingerprintMs: Int64 = minuteMs
    static let hourRoundedSlackMs: Int64 = hourMs
    public static let sendEveryMs: Int64 = 60_000
    static let precisionFloor = 0.02
    static let highWidth = 0.08
    static let lineMatchMs: Int64 = 5_000
    static let observationsPerInstance = 256
    public static let maxEntries = 16
    static let storeVersion = 1
}

/// Account keys, plan strings and the account-merge rules (QUOTAS 10.1, 10.2).
public enum QuotaAccounts {
    static let personalClaudePlans: Set<String> = ["claude_free", "claude_pro", "claude_max", "claude_ultra"]

    /// `first 16 hex digits of HMAC-SHA256(installSalt, "<vendor>:<id>")`.
    public static func key(salt: Data, vendor: String, id: String) -> String {
        let mac = HMAC<SHA256>.authenticationCode(for: Data("\(vendor):\(id)".utf8), using: SymmetricKey(data: salt))
        return mac.prefix(8).map { String(format: "%02x", $0) }.joined()
    }

    /// Whether Claude Desktop spends on the CLI's account: the organization (compared by its `claudeDesktop` key)
    /// matches, the plan is personal and the windows' fingerprint confirms it. Every merge decision goes through here.
    public static func desktopSharesCli(desktopOrg: String?, cliOrg: String?, cliOrgType: String?,
                                        fingerprint: Bool?) -> Bool {
        guard let desktopOrg, !desktopOrg.isEmpty, desktopOrg == cliOrg, let cliOrgType else { return false }
        return personalClaudePlans.contains(cliOrgType) && fingerprint == true
    }

    /// Equal keys on two computers are one account unless the latest comparable pair contradicts it.
    public static func keysShare(fingerprint: Bool?) -> Bool {
        fingerprint != false
    }

    /// A plan or tier is kept only when it is 1–64 characters of `[a-z0-9_.-]`.
    public static func cleanPlan(_ value: String?) -> String? {
        guard let value, (1...64).contains(value.utf8.count) else { return nil }
        let allowed = value.utf8.allSatisfy { byte in
            (0x61...0x7A).contains(byte) || (0x30...0x39).contains(byte) || byte == 0x5F || byte == 0x2E || byte == 0x2D
        }
        return allowed ? value : nil
    }

    /// The vendor of a source's plan, for cap classes.
    static func vendor(of source: String) -> String {
        switch source {
        case "codex": "openai"
        case "claudeCode", "claudeDesktop": "anthropic"
        default: source
        }
    }
}

/// One observed window for the fingerprint: when, which window, its reset and its length.
public struct QuotaFingerprintPoint: Hashable, Sendable {
    public var t: Int64
    public var window: String
    public var resetsMs: Int64
    public var minutes: Int

    public init(t: Int64, window: String, resetsMs: Int64, minutes: Int) {
        self.t = t
        self.window = window
        self.resetsMs = resetsMs
        self.minutes = minutes
    }
}

/// The fingerprint of two sources (QUOTAS 10.1): whether their windows say they are one account at a moment.
///
/// Points are grouped by window and reset, so a query costs the number of comparable groups, not of pairs: within
/// two groups the best pair by a moment is made of each group's latest time by then.
public struct QuotaAccountFingerprint: Sendable {
    private struct Group {
        var window: String
        var resetsMs: Int64
        var minutes: Int
        /// Sorted.
        var times: [Int64]
    }

    private struct Pairing {
        var a: Int
        var b: Int
        var contradicts: Bool
    }

    private let a: [Group]
    private let b: [Group]
    private let pairings: [Pairing]

    public init(_ first: [QuotaFingerprintPoint], _ second: [QuotaFingerprintPoint]) {
        a = Self.groups(first)
        b = Self.groups(second)
        var pairings: [Pairing] = []
        for (i, ga) in a.enumerated() {
            for (j, gb) in b.enumerated() where gb.window == ga.window {
                let minute = QuotaEstimateConstants.minuteMs
                let overlap = min(ga.resetsMs, gb.resetsMs)
                    - max(ga.resetsMs - Int64(ga.minutes) * minute, gb.resetsMs - Int64(gb.minutes) * minute)
                guard overlap > QuotaEstimateConstants.fingerprintMs else { continue }
                let same = abs(ga.resetsMs - gb.resetsMs) <= QuotaEstimateConstants.fingerprintMs
                pairings.append(Pairing(a: i, b: j, contradicts: !same))
            }
        }
        self.pairings = pairings
    }

    private static func groups(_ points: [QuotaFingerprintPoint]) -> [Group] {
        var index: [String: Int] = [:]
        var out: [Group] = []
        for point in points {
            let id = "\(point.window)|\(point.resetsMs)|\(point.minutes)"
            if let at = index[id] {
                out[at].times.append(point.t)
            } else {
                index[id] = out.count
                out.append(Group(window: point.window, resetsMs: point.resetsMs, minutes: point.minutes, times: [point.t]))
            }
        }
        for i in out.indices { out[i].times.sort() }
        return out
    }

    /// True confirms, false contradicts, nil without any comparable pair. The latest pair observed by `at` decides
    /// (the earlier time next, a contradiction first on a tie); with none by then the first pair observed after it.
    public func decision(at: Int64) -> Bool? {
        guard !pairings.isEmpty else { return nil }
        var best: (later: Int64, earlier: Int64, contradicts: Bool)?
        for pairing in pairings {
            guard let ta = Self.latest(a[pairing.a].times, upTo: at), let tb = Self.latest(b[pairing.b].times, upTo: at)
            else { continue }
            let candidate = (later: max(ta, tb), earlier: min(ta, tb), contradicts: pairing.contradicts)
            if let current = best {
                if (candidate.later, candidate.earlier, candidate.contradicts ? 1 : 0)
                    > (current.later, current.earlier, current.contradicts ? 1 : 0) {
                    best = candidate
                }
            } else {
                best = candidate
            }
        }
        if let best { return !best.contradicts }
        var first: (later: Int64, earlier: Int64, contradicts: Bool)?
        for pairing in pairings {
            let ta = a[pairing.a].times[0], tb = b[pairing.b].times[0]
            let candidate = (later: max(ta, tb), earlier: min(ta, tb), contradicts: pairing.contradicts)
            if let current = first {
                if (candidate.later, candidate.earlier, candidate.contradicts ? 0 : 1)
                    < (current.later, current.earlier, current.contradicts ? 0 : 1) {
                    first = candidate
                }
            } else {
                first = candidate
            }
        }
        return first.map { !$0.contradicts }
    }

    private static func latest(_ times: [Int64], upTo at: Int64) -> Int64? {
        var low = 0, high = times.count
        while low < high {
            let mid = (low + high) / 2
            if times[mid] <= at { low = mid + 1 } else { high = mid }
        }
        return low > 0 ? times[low - 1] : nil
    }
}

/// Codex history accounts: window instances named by one token count line belong to one account (union-find).
struct QuotaChains {
    private var parent: [String: String] = [:]

    mutating func find(_ x: String) -> String {
        if parent[x] == nil { parent[x] = x }
        var x = x
        while let up = parent[x], up != x {
            let grand = parent[up] ?? up
            parent[x] = grand
            x = grand
        }
        return x
    }

    @discardableResult
    mutating func link(_ instances: [String]) -> String {
        guard !instances.isEmpty else { return "" }
        let roots = instances.map { find($0) }
        for root in roots.dropFirst() {
            let a = find(root), b = find(roots[0])
            parent[a] = b
        }
        return find(roots[0])
    }
}
