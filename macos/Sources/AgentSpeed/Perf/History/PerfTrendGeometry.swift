import AgentSpeedCore
import CoreGraphics
import Foundation

/// The trend chart's log scale and plot (DESIGN 12, *Trend*): 250 at the bottom, `max(4000, 250 · 2^⌈log2(max / 250)⌉)`
/// at the top, one slot per day.
struct PerfTrendGeometry: Equatable, Sendable {
    static let bottomValue = 250.0
    static let insets = (left: CGFloat(36), right: CGFloat(8), top: CGFloat(14), bottom: CGFloat(18))

    let size: CGSize
    let days: Int
    let topValue: Double

    init(size: CGSize, days: Int, maxScore: Double?) {
        self.size = size
        self.days = max(days, 1)
        topValue = Self.topValue(maxScore)
    }

    static func topValue(_ maxScore: Double?) -> Double {
        guard let maxScore, maxScore > 4000, maxScore.isFinite else { return 4000 }
        return bottomValue * Foundation.pow(2, Foundation.ceil(Foundation.log2(maxScore / bottomValue)))
    }

    /// Gridlines at every power-of-two step from 500.
    var gridValues: [Double] {
        var values: [Double] = []
        var value = 500.0
        while value <= topValue + 0.5 {
            values.append(value)
            value *= 2
        }
        return values
    }

    var plot: CGRect {
        let insets = Self.insets
        return CGRect(x: insets.left, y: insets.top, width: max(size.width - insets.left - insets.right, 1),
                      height: max(size.height - insets.top - insets.bottom, 1))
    }

    var slot: CGFloat { plot.width / CGFloat(days) }

    func x(_ index: Int) -> CGFloat { plot.minX + (CGFloat(index) + 0.5) * slot }

    /// A score's height, clamped to the plot.
    func y(_ value: Double) -> CGFloat {
        let span = Foundation.log2(topValue / Self.bottomValue)
        let fraction = value > 0 ? Foundation.log2(value / Self.bottomValue) / span : 0
        return plot.maxY - CGFloat(min(max(fraction, 0), 1)) * plot.height
    }

    /// The day under a horizontal position inside the plot.
    func index(atX x: CGFloat) -> Int? {
        guard x >= plot.minX - slot / 2, x <= plot.maxX + slot / 2 else { return nil }
        return min(max(Int(((x - plot.minX) / slot).rounded(.down)), 0), days - 1)
    }

    /// Runs of consecutive days with a score; a run of one is a dot only.
    static func runs(_ scores: [Double?]) -> [Range<Int>] {
        var runs: [Range<Int>] = []
        var start: Int?
        for (index, score) in scores.enumerated() {
            if score != nil {
                if start == nil { start = index }
            } else if let first = start {
                runs.append(first..<index)
                start = nil
            }
        }
        if let first = start { runs.append(first..<scores.count) }
        return runs
    }
}
