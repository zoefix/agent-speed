import Foundation

/// What an agent's recent tool calls say about its work (METRICS 6.10): whether it goes in circles and whether its
/// actions keep finding something new. Descriptive only; never part of a score or a report.
public struct WorkSignals: Equatable, Sendable {
    static let recentMs: Int64 = 600_000
    static let freshMs: Int64 = 120_000
    static let loopRepeats = 3
    static let loopBlockSizes = 1...3
    static let noveltyMinimum = 8
    static let recoveryReach = 5

    /// The newest block of one to three calls repeated this many times in a row, one of them failing.
    public var looping: Int?
    /// The same without a failure (polling, waiting); nil while looping.
    public var repeating: Int?
    /// Distinct calls ÷ recent calls, from `noveltyMinimum` recent actions.
    public var novelty: Double?
    /// Recent actions that failed, and those followed by a success of the same tool soon after.
    public var failed = 0
    public var recovered = 0

    public static let none = WorkSignals()

    public init(looping: Int? = nil, repeating: Int? = nil, novelty: Double? = nil, failed: Int = 0, recovered: Int = 0) {
        self.looping = looping
        self.repeating = repeating
        self.novelty = novelty
        self.failed = failed
        self.recovered = recovered
    }

    /// The signals of `actions` (oldest first) at `nowMs`; actions read while seeding do not count.
    init(_ actions: [ToolAction], nowMs: Int64) {
        let recent = actions.filter { !$0.seeded && $0.callMs >= nowMs - Self.recentMs }
        if let newest = recent.last, nowMs - newest.callMs < Self.freshMs {
            for size in Self.loopBlockSizes {
                let run = Self.repeatRun(recent, blockSize: size)
                guard run.count >= Self.loopRepeats else { continue }
                if run.failed {
                    looping = max(looping ?? 0, run.count)
                } else {
                    repeating = max(repeating ?? 0, run.count)
                }
            }
            if looping != nil { repeating = nil }
        }
        let failures = recent.indices.filter { recent[$0].failed == true }
        failed = failures.count
        recovered = failures.filter { index in
            let failure = recent[index]
            return recent[(index + 1)..<min(recent.count, index + 1 + Self.recoveryReach)].contains {
                $0.call.tool == failure.call.tool && $0.failed == false && $0.callMs - failure.callMs <= Self.recentMs
            }
        }.count
        if recent.count >= Self.noveltyMinimum {
            novelty = Double(Set(recent.map(\.call)).count) / Double(recent.count)
        }
    }

    /// How many times the newest block of `blockSize` actions repeats back to back, and whether any action of the run
    /// failed.
    private static func repeatRun(_ actions: [ToolAction], blockSize size: Int) -> (count: Int, failed: Bool) {
        guard actions.count >= size else { return (0, false) }
        let block = actions.suffix(size).map(\.call)
        var start = actions.count - size
        var count = 1
        while start >= size, actions[(start - size)..<start].map(\.call) == block {
            count += 1
            start -= size
        }
        return (count, actions[start...].contains { $0.failed == true })
    }
}
