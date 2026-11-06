import Foundation

/// Everything the engine keeps per (host, source) cell (DESIGN 13): the live values of the window (METRICS 6.3, 6.9,
/// 9.5) and today's totals and cost (6.6, 9.6). Per source they add up to the engine's per-source values.
struct EngineCell: Equatable, Sendable {
    var live = Rate.zero
    var liveEstimated = Rate.zero
    var liveCost = LiveCost.zero
    var intelligence = IntelligenceSums.zero
    var today = TokenTotals()
    var todayCost = CostTally()
    /// Today's unpriced requests by raw model (`""` = unknown).
    var unpriced: [String: Int] = [:]
}

/// Today's part of a cell, kept by the day ledger as responses arrive.
struct CellDay: Equatable, Sendable {
    var today = TokenTotals()
    var cost = CostTally()
    var unpriced: [String: Int] = [:]

    mutating func apply(_ record: ResponseRecord, sign: Int) {
        today.add(record.usage, sign: sign, requestCount: record.requestCount)
        cost.add(record.costMicroUsd, sign: sign, requestCount: record.requestCount)
        guard record.costMicroUsd == nil else { return }
        let model = record.model ?? ""
        let count = (unpriced[model] ?? 0) + sign * record.requestCount
        unpriced[model] = count == 0 ? nil : count
    }

    var isEmpty: Bool { today.requests == 0 && cost == CostTally() && unpriced.isEmpty }
}

/// The live window's tokens, estimates, costs and thinking sums per cell, before dividing by the window length.
struct LiveCells: Sendable {
    private(set) var tokens: [HostSource: Rate] = [:]
    private(set) var estimated: [HostSource: Rate] = [:]
    private(set) var cost: [HostSource: LiveCost] = [:]
    private(set) var intelligence: [HostSource: IntelligenceSums] = [:]

    mutating func add(_ value: Rate, estimated isEstimate: Bool, cell: HostSource) {
        tokens[cell, default: .zero].add(value)
        if isEstimate { estimated[cell, default: .zero].add(value) }
    }

    mutating func addCost(_ value: LiveCost, cell: HostSource) {
        cost[cell, default: .zero].add(value)
    }

    mutating func addIntelligence(output: Double, reasoning: Double, cell: HostSource) {
        intelligence[cell, default: .zero].add(output: output, reasoning: reasoning, from: cell.source)
    }

    /// Per second, like the window's other values.
    func cells(seconds: Double) -> [HostSource: EngineCell] {
        var cells: [HostSource: EngineCell] = [:]
        for (cell, value) in tokens {
            cells[cell, default: EngineCell()].live = Rate(output: value.output / seconds, total: value.total / seconds)
        }
        for (cell, value) in estimated {
            cells[cell, default: EngineCell()].liveEstimated = Rate(output: value.output / seconds, total: value.total / seconds)
        }
        for (cell, value) in cost {
            cells[cell, default: EngineCell()].liveCost = LiveCost(microUsdPerSecond: value.microUsdPerSecond / seconds,
                                                                   knownOutput: value.knownOutput / seconds)
        }
        for (cell, value) in intelligence {
            cells[cell, default: EngineCell()].intelligence = value.scaled(by: 1 / seconds)
        }
        return cells
    }
}
