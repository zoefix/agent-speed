import AgentSpeedCore
import AgentSpeedOrbModel
import CoreGraphics
import Foundation

/// What one engine tick tells a board (DESIGN 2c Content). The live feed (the app's boards controller, from the
/// snapshot) and the demo feed make it; a style's `BoardContentBuilding` draws it. Fields are only ever added. An equal
/// tick shows the same content, so it is not laid out again.
struct BoardTick: Equatable {
    /// `live_A` in the board's metric (METRICS 6.8) and its text (METRICS 7).
    var value: Double
    var valueText: String
    /// The metric of `value` and of every row's value (a sub-agent's is `live.value(for: metric)`).
    var metric: SpeedMetric
    var redline: Double
    var heat: Double
    var overdrive: Double
    /// The speed title in the board's title theme (DESIGN 1), localized.
    var title: String
    var generating: Int
    var tools: Int
    /// `live_A` once per second, oldest first, at most 60.
    var history: [Double]
    /// Ranked groups, first place first (DESIGN 2c Ranking stability), and the planned rows showing them.
    var groups: [BoardGroup]
    var rows: [BoardPlannedRow]
    /// The passes of this tick.
    var events: [BoardRankEvent]
    /// A new `peakToday_A` was reached on this tick.
    var newPeak: Bool
    /// Footer values, already formatted, without their labels (the styles set them in `boards.today` and
    /// `boards.peak`): today's output, today's cost (nil when unknown, section 9), `peakToday_A`.
    var todayText: String
    var costText: String?
    var peakText: String
    /// `peakToday_A` (neon marks it on its LED bar); 0 when unknown.
    var peak: Double = 0
    /// Today's cost is `cost.subscription.tag` (every source excluded, while 費用 is on): drawn quietly.
    var costIsTag = false
    /// The live metrics (DESIGN 2c Live metrics): switches and the hero's values.
    var metrics = BoardMetricsTick()
}

/// A board's content for one tick, with the anchors the effects need (board points, y down).
struct BoardContentLayout {
    var items: [BoardContentItem]
    /// Group id → its row frame and the center of its rank, for overtake and new-leader effects.
    var groupRows: [String: (row: CGRect, rank: CGPoint)]
    /// The hero number, for punches, embers and new peaks.
    var number: CGRect
    /// Sub-agent id → its row frame (hover cards, accessibility).
    var subRows: [String: CGRect] = [:]
    /// The hero's 性能 (glass: the dial; neon: the digits and meter; timing: its row of the box), where its score card
    /// opens; nil while 性能 is off.
    var scoreArea: CGRect?

    /// The items to show; their one-shot accents come with the first call only, so a layout applied again (a board
    /// shown again, a hover) replays nothing.
    mutating func takeItems() -> [BoardContentItem] {
        let taken = items
        for index in items.indices where !items[index].accents.isEmpty { items[index].accents = [] }
        return taken
    }
}

/// Draws a style's hero, rows and footer (DESIGN 2c Styles; prototypes in spec/prototypes/board).
@MainActor
protocol BoardContentBuilding: AnyObject {
    func layout(_ tick: BoardTick) -> BoardContentLayout
    /// When a fixed cell of the sub-gauge narrows back (a later layout differs even if the tick does not).
    var wakeAt: TimeInterval? { get }
    /// Reduce Motion: 性能 crossfades instead of counting up (DESIGN 12 *Motion*).
    var reduceMotion: Bool { get set }
    /// While the hero's 性能 counts up: when it lands. Until then the board lays itself out on every frame of the roll
    /// class (DESIGN 2c *Frame policy*), each layout showing the integer of its instant.
    func climbsUntil(after now: TimeInterval) -> TimeInterval?
}

extension Array where Element == BoardContentItem {
    /// Puts the items in the hero's layer (DESIGN 2c Live metrics: a switch change crossfades the hero as a whole).
    func inHero() -> [BoardContentItem] {
        map { item in
            var item = item
            item.group = .hero
            return item
        }
    }
}
