import AgentSpeedCore
import CoreGraphics
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 12, *Placement and timing*: where the card goes and when it shows, switches and hides.
@Suite("Performance score card window")
@MainActor
struct PerfCardWindowTests {
    // MARK: Placement

    static let screen = CGRect(x: 0, y: 0, width: 1512, height: 944)
    static let size = CGSize(width: 320, height: 520)

    @Test func theDashboardCardSitsLeftOfAMenuBarPopover() {
        // The popover hangs from a status item on the right: the card goes toward the centre, 8 pt from it.
        let popover = CGRect(x: 1120, y: 300, width: 360, height: 620)
        let row = CGRect(x: 1136, y: 760, width: 328, height: 36)
        let result = PerfCardPlacement.place(size: Self.size, anchor: .init(row: row, container: popover,
                                                                                 visibleFrame: Self.screen, side: .towardScreenCenter))
        #expect(result.frame.maxX == popover.minX - 8)
        #expect(result.frame.maxY == row.maxY)
        #expect(result.direction == -1)
        // The slide starts 2 pt toward the row.
        #expect(result.start.minX == result.frame.minX + 2)
    }

    @Test func theCardStaysEightPointsInsideTheScreen() {
        let popover = CGRect(x: 1120, y: 300, width: 360, height: 620)
        // A row low on the screen: the card's top would leave room under it for only part of it.
        let low = CGRect(x: 1136, y: 330, width: 328, height: 36)
        let result = PerfCardPlacement.place(size: Self.size, anchor: .init(row: low, container: popover,
                                                                                 visibleFrame: Self.screen, side: .towardScreenCenter))
        #expect(result.frame.minY == 8)
        // A card taller than the screen keeps its top in view.
        let tall = PerfCardPlacement.place(size: CGSize(width: 320, height: 1200),
                                                anchor: .init(row: low, container: popover, visibleFrame: Self.screen,
                                                              side: .towardScreenCenter))
        #expect(tall.frame.maxY == Self.screen.maxY - 8)
    }

    @Test func boardsUseTheSideWithMoreRoom() {
        let board = CGRect(x: 120, y: 400, width: 420, height: 300)
        let row = CGRect(x: 130, y: 600, width: 400, height: 30)
        let result = PerfCardPlacement.place(size: Self.size, anchor: .init(row: row, container: board,
                                                                                 visibleFrame: Self.screen, side: .moreRoom))
        #expect(result.frame.minX == board.maxX + 8)
        #expect(result.direction == 1)
        // Toward the centre, but no room on that side: it gives way to the other side.
        let edge = CGRect(x: 1250, y: 400, width: 250, height: 300)
        let flipped = PerfCardPlacement.place(size: Self.size, anchor: .init(row: row, container: edge,
                                                                                  visibleFrame: Self.screen, side: .moreRoom))
        #expect(flipped.frame.maxX == edge.minX - 8)
    }

    // MARK: Timing

    final class Clock: PerfCardScheduling {
        final class Item: PerfCardTimer {
            let due: TimeInterval
            let action: @MainActor () -> Void
            var cancelled = false
            init(due: TimeInterval, action: @escaping @MainActor () -> Void) {
                self.due = due
                self.action = action
            }
            func cancel() { cancelled = true }
        }

        var now: TimeInterval = 0
        var items: [Item] = []

        func after(_ seconds: TimeInterval, _ action: @escaping @MainActor () -> Void) -> PerfCardTimer {
            let item = Item(due: now + seconds, action: action)
            items.append(item)
            return item
        }

        func advance(_ seconds: TimeInterval) {
            now += seconds
            for item in items where !item.cancelled && item.due <= now + 1e-9 {
                item.cancelled = true
                item.action()
            }
        }
    }

    final class Display: PerfCardDisplaying {
        var shown: [String] = []
        var updates = 0
        var hides = 0
        var isShown = false
        func show(_ request: PerfCardRequest, key: String, reduceMotion: Bool, reduceTransparency: Bool) {
            shown.append(key)
            isShown = true
        }
        func update(_ model: PerfCardModel) { updates += 1 }
        func hide(animated: Bool) {
            hides += 1
            isShown = false
        }
    }

    static func request() -> PerfCardRequest {
        let model = PerfCardModel(
            header: .init(source: .codex, project: "p", model: nil, subAgents: 0),
            score: PerfCardScore(source: .codex, form: .none, shown: nil, provisional: false, n: 0, tools: 0, topCount: 1,
                                 baseline: nil, vsUsualPercent: nil, components: [], signals: .none),
            facts: [])
        let rect = CGRect(x: 0, y: 0, width: 10, height: 10)
        return PerfCardRequest(model: model, anchor: .init(row: rect, container: rect, visibleFrame: rect, side: .moreRoom))
    }

    func presenter(delay: TimeInterval = PerfCardPresenter.dashboardDelay) -> (PerfCardPresenter, Clock, Display) {
        let clock = Clock()
        let display = Display()
        let presenter = PerfCardPresenter(display: display, delay: delay, scheduler: clock) { (false, false) }
        return (presenter, clock, display)
    }

    @Test func itShowsAfterTheHoverDelayAndSwitchesAtOnce() {
        let (presenter, clock, display) = presenter()
        presenter.enter("a", provider: Self.request)
        clock.advance(0.44)
        #expect(display.shown.isEmpty)
        clock.advance(0.01)
        #expect(display.shown == ["a"])
        // Moving to another row while shown switches without a delay.
        presenter.exit("a")
        presenter.enter("b", provider: Self.request)
        #expect(display.shown == ["a", "b"])
        clock.advance(0.2)
        #expect(display.hides == 0)
    }

    @Test func itHidesShortlyAfterThePointerLeaves() {
        let (presenter, clock, display) = presenter(delay: PerfCardPresenter.boardDelay)
        presenter.enter("a", provider: Self.request)
        clock.advance(0.35)
        presenter.exit("a")
        clock.advance(0.14)
        #expect(display.hides == 0)
        clock.advance(0.01)
        #expect(display.hides == 1)
        #expect(!presenter.isShown)
    }

    @Test func leavingBeforeTheDelayShowsNothing() {
        let (presenter, clock, display) = presenter()
        presenter.enter("a", provider: Self.request)
        clock.advance(0.3)
        presenter.exit("a")
        clock.advance(1)
        #expect(display.shown.isEmpty)
    }

    @Test func theKeyboardShowsAtOnceFollowsFocusAndEscHides() {
        let (presenter, clock, display) = presenter()
        presenter.showNow("a", provider: Self.request)
        #expect(display.shown == ["a"])
        presenter.focus("b", provider: Self.request)
        #expect(display.shown == ["a", "b"])
        presenter.refresh()
        #expect(display.updates == 1)
        presenter.dismiss()
        #expect(display.hides == 1)
        // Not shown: focus moves alone.
        presenter.focus("c", provider: Self.request)
        clock.advance(1)
        #expect(display.shown == ["a", "b"])
    }

    @Test func aRowWithoutACardHidesIt() {
        let (presenter, _, display) = presenter()
        presenter.showNow("a", provider: Self.request)
        presenter.focus("b") { nil }
        #expect(display.hides == 1)
    }
}
