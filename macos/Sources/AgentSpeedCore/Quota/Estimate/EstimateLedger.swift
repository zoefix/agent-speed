import Foundation

/// The spend ledger of QUOTAS 10.3: rows per (account, source, price-table model, UTC minute of the response's end)
/// with their sums. Filled once, then read from any thread: the per-account series are cached behind a lock.
public final class QuotaEstimateLedger: @unchecked Sendable {
    struct Key: Hashable {
        var account: String
        var source: String
        var model: String
        var minute: Int64
    }

    struct Row {
        var cost: Int64 = 0
        var unpricedRequests: Int64 = 0
        var pricedTokens: Int64 = 0
        var unpricedTokens: Int64 = 0
    }

    /// The prefix sums of one set of accounts and one model filter.
    private struct Series {
        var minutes: [Int64] = []
        /// `cost[i]` is the cost of the first `i` minutes.
        var cost: [Int64] = [0]
        var unpriced: [Int64] = [0]
        var unpricedTokens: [Int64] = [0]
        /// The highest cost per token of the priced models.
        var rate: Double?
        /// The priced rows with a cost, by minute, for the model mix.
        var spent: [(minute: Int64, model: String, cost: Int64)] = []
    }

    private struct SeriesKey: Hashable {
        var accounts: [String]
        var name: String
    }

    private(set) var rows: [Key: Row] = [:]
    private let lock = NSLock()
    private var cache: [SeriesKey: Series] = [:]

    public init() {}

    var isEmpty: Bool { rows.isEmpty }

    /// Adds one response, or a minute's worth of them; a nil cost is unpriced.
    func add(account: String, source: String, model: String, endMs: Int64, cost: Int64?, requests: Int64 = 1,
             tokens: Int64 = 0) {
        let key = Key(account: account, source: source, model: model, minute: Self.minute(of: endMs))
        var row = rows[key] ?? Row()
        if let cost {
            row.cost += cost
            row.pricedTokens += tokens
        } else {
            row.unpricedRequests += requests
            row.unpricedTokens += tokens
        }
        rows[key] = row
        lock.withLock { cache.removeAll() }
    }

    func prune(nowMs: Int64) {
        rows = rows.filter { $0.key.minute >= nowMs - QuotaEstimateConstants.keepMs }
        lock.withLock { cache.removeAll() }
    }

    static func minute(of ms: Int64) -> Int64 {
        floorDiv(ms, QuotaEstimateConstants.minuteMs) * QuotaEstimateConstants.minuteMs
    }

    static func floorDiv(_ a: Int64, _ b: Int64) -> Int64 {
        let q = a / b
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q
    }

    /// A `modelWeek` window counts only the models whose id holds each word of its name as a `-`-separated token.
    static func modelMatches(_ model: String, name: String?) -> Bool {
        guard let name, !name.isEmpty else { return true }
        let tokens = Set(model.lowercased().split(separator: "-", omittingEmptySubsequences: false).map(String.init))
        return name.lowercased().split(whereSeparator: \.isWhitespace).allSatisfy { tokens.contains(String($0)) }
    }

    private func series(_ accounts: Set<String>, _ name: String?) -> Series {
        let key = SeriesKey(accounts: accounts.sorted(), name: name ?? "")
        if let cached = lock.withLock({ cache[key] }) { return cached }
        var perMinute: [Int64: (cost: Int64, unpriced: Int64, tokens: Int64)] = [:]
        var rates: [String: (cost: Int64, tokens: Int64)] = [:]
        var spent: [(minute: Int64, model: String, cost: Int64)] = []
        for (rowKey, row) in rows where accounts.contains(rowKey.account) && Self.modelMatches(rowKey.model, name: name) {
            var value = perMinute[rowKey.minute] ?? (0, 0, 0)
            value.cost += row.cost
            value.unpriced += row.unpricedRequests
            value.tokens += row.unpricedTokens
            perMinute[rowKey.minute] = value
            var rate = rates[rowKey.model] ?? (0, 0)
            rate.cost += row.cost
            rate.tokens += row.pricedTokens
            rates[rowKey.model] = rate
            if row.cost > 0 { spent.append((rowKey.minute, rowKey.model, row.cost)) }
        }
        var out = Series()
        out.minutes = perMinute.keys.sorted()
        out.cost.reserveCapacity(out.minutes.count + 1)
        for minute in out.minutes {
            let value = perMinute[minute]!
            out.cost.append(out.cost.last! + value.cost)
            out.unpriced.append(out.unpriced.last! + value.unpriced)
            out.unpricedTokens.append(out.unpricedTokens.last! + value.tokens)
        }
        out.rate = rates.values.filter { $0.tokens > 0 }.map { Double($0.cost) / Double($0.tokens) }.max()
        out.spent = spent.sorted { ($0.minute, $0.model) < ($1.minute, $1.model) }
        lock.withLock { cache[key] = out }
        return out
    }

    private func range(_ accounts: Set<String>, _ name: String?, first: Int64, last: Int64)
        -> (cost: Int64, unpriced: Int64, tokens: Int64, rate: Double?) {
        let series = series(accounts, name)
        guard last >= first else { return (0, 0, 0, series.rate) }
        let i = Self.lowerBound(series.minutes, first)
        let j = Self.upperBound(series.minutes, last)
        return (series.cost[j] - series.cost[i], series.unpriced[j] - series.unpriced[i],
                series.unpricedTokens[j] - series.unpricedTokens[i], series.rate)
    }

    /// `C⁻`: the minutes wholly inside `[start, end]` (unpriced as 0), and their unpriced requests.
    func inner(_ accounts: Set<String>, _ name: String?, _ start: Int64, _ end: Int64) -> (cost: Int64, unpriced: Int64) {
        let minute = QuotaEstimateConstants.minuteMs
        let first = -Self.floorDiv(-start, minute) * minute
        let r = range(accounts, name, first: first, last: Self.minute(of: end) - minute)
        return (r.cost, r.unpriced)
    }

    /// The minutes touching `[start, end]` (unpriced as 0), and their unpriced requests.
    func outer(_ accounts: Set<String>, _ name: String?, _ start: Int64, _ end: Int64) -> (cost: Int64, unpriced: Int64) {
        let r = range(accounts, name, first: Self.minute(of: start), last: Self.minute(of: end))
        return (r.cost, r.unpriced)
    }

    /// `C⁺`: `outer` with each unpriced token at the highest cost per token of the priced models; infinite without one.
    func outerHigh(_ accounts: Set<String>, _ name: String?, _ start: Int64, _ end: Int64) -> Double {
        let r = range(accounts, name, first: Self.minute(of: start), last: Self.minute(of: end))
        guard r.unpriced > 0 else { return Double(r.cost) }
        guard let rate = r.rate else { return .infinity }
        return Double(r.cost) + Double(r.tokens) * rate
    }

    /// The top price-table models of a span by cost, with their shares (REPORTING-EXTENSIONS 11.2 `mix`).
    func mix(_ accounts: Set<String>, _ name: String?, _ start: Int64, _ end: Int64, top: Int = 3)
        -> [(model: String, share: Double)] {
        let series = series(accounts, name)
        let first = Self.minute(of: start), last = Self.minute(of: end)
        var totals: [String: Int64] = [:]
        var i = 0, high = series.spent.count
        while i < high {
            let mid = (i + high) / 2
            if series.spent[mid].minute < first { i = mid + 1 } else { high = mid }
        }
        while i < series.spent.count, series.spent[i].minute <= last {
            totals[series.spent[i].model, default: 0] += series.spent[i].cost
            i += 1
        }
        let whole = totals.values.reduce(0, +)
        guard whole > 0 else { return [] }
        return totals.sorted { ($1.value, $0.key) < ($0.value, $1.key) }.prefix(top).map {
            (model: $0.key, share: QuotaEstimateRounding.halfAway(Double($0.value) / Double(whole), digits: 3))
        }
    }

    static func lowerBound(_ values: [Int64], _ x: Int64) -> Int {
        var low = 0, high = values.count
        while low < high {
            let mid = (low + high) / 2
            if values[mid] < x { low = mid + 1 } else { high = mid }
        }
        return low
    }

    static func upperBound(_ values: [Int64], _ x: Int64) -> Int {
        var low = 0, high = values.count
        while low < high {
            let mid = (low + high) / 2
            if values[mid] <= x { low = mid + 1 } else { high = mid }
        }
        return low
    }
}

/// The report's rounding (REPORTING-EXTENSIONS 11.2): half away from zero; micro-USD to whole numbers.
enum QuotaEstimateRounding {
    static func halfAway(_ x: Double, digits: Int) -> Double {
        let m = pow(10, Double(digits))
        let v = (abs(x) * m + 0.5 + 1e-9).rounded(.down) / m
        return x < 0 ? -v : v
    }

    static func micro(_ x: Double?) -> Int64? {
        guard let x, x.isFinite else { return nil }
        return Int64((x + 0.5).rounded(.down))
    }
}
