import Foundation

/// Codex accounts from the token count lines (QUOTAS 10.1 *Codex history*): the response each line reports, the
/// chains of window instances, their keys and plans.
struct QuotaCodexAccounts {
    struct Line {
        var line: QuotaCodexLine
        var tLo: Int64
        var tags: [String]
        var account: String
        var plan: String?
    }

    /// By `(file, ts)`; lines without a usable window are left out.
    private(set) var lines: [Line] = []
    /// The line each Codex response of this computer belongs to, by its index in the spend list.
    private(set) var lineOfSpend: [Int: Int] = [:]

    init(lines input: [QuotaCodexLine], spend: [QuotaEstimateSpend], keys: [QuotaKeyEntry], salt: Data) {
        let sorted = input.filter { !$0.windows.isEmpty }.stableSorted { ($0.file, $0.ts) < ($1.file, $1.ts) }
        var clusters: [String: Set<Int64>] = [:]
        for line in sorted {
            for w in line.windows { clusters[Self.windowID(w), default: []].insert(w.resetsAtMs!) }
        }
        var tagOf: [String: [Int64: String]] = [:]
        for (window, resets) in clusters {
            var first: Int64?
            for r in resets.sorted() {
                if first == nil || r - first! > QuotaEstimateConstants.instanceJoinMs { first = r }
                tagOf[window, default: [:]][r] = "\(window)@\(first!)"
            }
        }
        var chains = QuotaChains()
        var built: [Line] = sorted.map { line in
            let tags = line.windows.map { tagOf[Self.windowID($0)]![$0.resetsAtMs!]! }
            chains.link(tags)
            return Line(line: line, tLo: line.tLo ?? line.ts, tags: tags, account: "", plan: nil)
        }
        let index = Dictionary(built.enumerated().map { ($0.element.line.id, $0.offset) }, uniquingKeysWith: { a, _ in a })

        // Responses the history already paired keep their line; the others take the next line in their file.
        var used: Set<Int> = Set(built.indices.filter { built[$0].line.tLo != nil })
        var lineOfSpend: [Int: Int] = [:]
        var byFile: [String: [Int]] = [:]
        for (i, s) in spend.enumerated() where s.source == "codex" && s.host == nil {
            if let id = s.codexLine {
                if let at = index[id] { lineOfSpend[i] = at }
            } else if let file = s.file {
                byFile[file, default: []].append(i)
            }
        }
        for file in byFile.keys { byFile[file] = byFile[file]!.stableSorted { spend[$0].endMs < spend[$1].endMs } }
        var taken: Set<Int> = []
        for (at, line) in built.enumerated() where !used.contains(at) {
            guard let candidates = byFile[line.line.file] else { continue }
            let ts = line.line.ts
            var low = 0, high = candidates.count
            while low < high {
                let mid = (low + high) / 2
                if spend[candidates[mid]].endMs < ts - QuotaEstimateConstants.lineMatchMs { low = mid + 1 } else { high = mid }
            }
            var k = low
            while k < candidates.count, spend[candidates[k]].endMs <= ts {
                let i = candidates[k]
                if !taken.contains(i) {
                    taken.insert(i)
                    used.insert(at)
                    lineOfSpend[i] = at
                    built[at].tLo = min(ts, spend[i].startMs ?? ts)
                    break
                }
                k += 1
            }
        }
        self.lineOfSpend = lineOfSpend

        // A chain observed while auth.json gave a key takes the key in force at its newest line.
        let byTime = Array(built.indices).stableSorted { built[$0].line.ts < built[$1].line.ts }
        var keyed: [String: String] = [:]
        if let first = keys.first {
            for at in byTime where built[at].line.ts >= first.at {
                if let entry = QuotaKeyEntry.inForce(keys, at: built[at].line.ts) {
                    keyed[chains.find(built[at].tags[0])] = entry.key
                }
            }
        }
        var smallest: [String: String] = [:]
        for tags in tagOf.values {
            for tag in tags.values {
                let root = chains.find(tag)
                if let current = smallest[root], current <= tag { continue }
                smallest[root] = tag
            }
        }
        for at in built.indices {
            let root = chains.find(built[at].tags[0])
            built[at].account = keyed[root] ?? QuotaAccounts.key(salt: salt, vendor: "codex-chain", id: smallest[root]!)
        }

        // A null plan_type takes the account's nearest earlier plan, else its first later one.
        var plans: [String: [(Int64, String)]] = [:]
        for at in byTime {
            let plan = QuotaAccounts.cleanPlan(built[at].line.plan)
            built[at].plan = plan
            if let plan { plans[built[at].account, default: []].append((built[at].line.ts, plan)) }
        }
        for at in built.indices where built[at].plan == nil {
            let known = plans[built[at].account] ?? []
            built[at].plan = known.last { $0.0 <= built[at].line.ts }?.1 ?? known.first?.1
        }
        lines = built
    }

    static func windowID(_ w: QuotaWindow) -> String {
        QuotaEstimateWindow.id(kind: w.kind, name: w.name, minutes: w.minutes ?? 0)
    }
}
