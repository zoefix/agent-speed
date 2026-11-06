import AgentSpeedOrbModel
import AppKit
import Testing
@testable import AgentSpeed

/// DESIGN 2 *fade when idle*, shared by the orbs and the boards (2c): 60 s of idle dims the window to 45 % of its
/// opacity in 0.6 s ease-in-out, activity, hover and a drag bring it back at once, Reduce Motion cuts both ways, and a
/// board dims its square and its effects together.
@Suite("Board fade when idle")
@MainActor
struct BoardIdleFadeTests {
    final class FakeClock {
        var now: TimeInterval = 1000
    }

    // MARK: The rule

    @Test func anIdleWindowFadesSixtySecondsAfterItsLastActivity() throws {
        let due = try #require(IdleFade.due(enabled: true, idle: true, engaged: false, lastActive: 500, armedAt: 0,
                                            pointerLeftAt: -.infinity))
        #expect(due == 560)
        // Never sooner than 60 s after the window was set up (launch).
        #expect(IdleFade.due(enabled: true, idle: true, engaged: false, lastActive: -.infinity, armedAt: 900,
                             pointerLeftAt: -.infinity) == 960)
        // A pointer that just left gives it 1.5 s more.
        #expect(IdleFade.due(enabled: true, idle: true, engaged: false, lastActive: 0, armedAt: 0, pointerLeftAt: 999) == 1000.5)
    }

    @Test func activityHoverDragOrTheSwitchOffKeepItRestored() {
        #expect(IdleFade.due(enabled: true, idle: false, engaged: false, lastActive: 0, armedAt: 0, pointerLeftAt: -.infinity) == nil)
        #expect(IdleFade.due(enabled: true, idle: true, engaged: true, lastActive: 0, armedAt: 0, pointerLeftAt: -.infinity) == nil)
        #expect(IdleFade.due(enabled: false, idle: true, engaged: false, lastActive: 0, armedAt: 0, pointerLeftAt: -.infinity) == nil)
    }

    @Test func theFadeMatchesTheOrbsAndReduceMotionCutsIt() {
        #expect(IdleFade.alpha == 0.45)
        #expect(IdleFade.motion(dimming: true, reduceMotion: false) == .init(duration: 0.6, timing: .easeInEaseOut))
        #expect(IdleFade.motion(dimming: false, reduceMotion: false) == .init(duration: 0.12, timing: .easeOut))
        #expect(IdleFade.motion(dimming: true, reduceMotion: true).duration == 0)
        #expect(IdleFade.motion(dimming: false, reduceMotion: true).duration == 0)
    }

    // MARK: The dimmer on a board's gauge

    @Test func theDimmerFollowsIdleActivityHoverAndDrag() {
        let clock = FakeClock()
        let gauge = LiveGauge()
        let interaction = OrbInteraction()
        var enabled = true
        var dimmed: [Bool] = []
        let dimmer = OrbWindowIdleDimmer(gauge: gauge, enabled: { enabled }, interaction: interaction, now: { clock.now }) {
            dimmed.append($0)
        }
        defer { dimmer.cancel() }
        #expect(dimmed.last == false, "not before 60 s")
        clock.now += 61
        dimmer.refresh()
        #expect(dimmed.last == true, "idle for 60 s: faded")

        interaction.isHovered = true
        dimmer.refresh()
        #expect(dimmed.last == false, "hover restores")
        interaction.isHovered = false
        dimmer.refresh()
        #expect(dimmed.last == false, "back 1.5 s after the pointer leaves, not at once")
        clock.now += 2
        dimmer.refresh()
        #expect(dimmed.last == true)

        interaction.isDragging = true
        dimmer.refresh()
        #expect(dimmed.last == false, "a drag restores")
        interaction.isDragging = false
        dimmer.refresh()
        clock.now += 2
        dimmer.refresh()
        #expect(dimmed.last == true)

        gauge.ingest(120, now: clock.now)
        dimmer.refresh()
        #expect(dimmed.last == false, "activity restores")
        clock.now += 1
        gauge.ingest(120, now: clock.now)
        #expect(gauge.tier != .idle)
        dimmer.refresh()
        #expect(dimmed.last == false)
        // Back to idle (below 0.5 for 3 s, then the 0.8 s hold), 60 s after the last activity.
        gauge.ingest(0, now: clock.now + 1)
        gauge.ingest(0, now: clock.now + 59)
        gauge.ingest(0, now: clock.now + 60)
        #expect(gauge.tier == .idle)
        clock.now += 59
        dimmer.refresh()
        #expect(dimmed.last == false, "idle, but not yet for 60 s")
        clock.now += 2
        dimmer.refresh()
        #expect(dimmed.last == true)

        enabled = false
        dimmer.refresh()
        #expect(dimmed.last == false, "switching it off restores")
    }

    // MARK: The board's windows

    static func board(opacity: Double = 0.8) -> BoardWindowController {
        BoardWindowController(config: BoardConfig(id: "b", style: .neon, opacity: opacity, fadeWhenIdle: true), center: .zero,
                              dark: true)
    }

    static func mouse(_ type: NSEvent.EventType) -> NSEvent {
        NSEvent.mouseEvent(with: type, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0, context: nil,
                           eventNumber: 0, clickCount: 1, pressure: 1)!
    }

    @Test func aFadedBoardDimsItsSquareAndItsEffectsTogether() {
        let board = Self.board()
        defer { board.close() }
        board.reduceMotion = true
        #expect(board.panel.alphaValue == 0.8 && board.effectsPanel.alphaValue == 0.8)
        board.setDimmed(true)
        #expect(abs(board.panel.alphaValue - 0.36) < 0.001, "45 % of its opacity, at once with Reduce Motion")
        #expect(abs(board.effectsPanel.alphaValue - 0.36) < 0.001)
        // An opacity change while faded keeps it faded.
        board.apply(BoardConfig(id: "b", style: .neon, opacity: 0.6, fadeWhenIdle: true))
        #expect(abs(board.panel.alphaValue - 0.27) < 0.001 && abs(board.effectsPanel.alphaValue - 0.27) < 0.001)
        board.setDimmed(false)
        #expect(abs(board.panel.alphaValue - 0.6) < 0.001 && abs(board.effectsPanel.alphaValue - 0.6) < 0.001)
    }

    @Test func hoverAndDragReachTheDimmer() {
        let board = Self.board()
        defer { board.close() }
        board.host.mouseEntered(with: Self.mouse(.mouseMoved))
        #expect(board.interaction.isHovered)
        board.host.mouseExited(with: Self.mouse(.mouseMoved))
        #expect(!board.interaction.isHovered)
        board.host.mouseDown(with: Self.mouse(.leftMouseDown))
        #expect(board.interaction.isDragging)
        board.host.mouseUp(with: Self.mouse(.leftMouseUp))
        #expect(!board.interaction.isDragging)
        // Click-through: the pointer never reaches the board, so it is never hovered.
        board.host.mouseEntered(with: Self.mouse(.mouseMoved))
        board.apply(BoardConfig(id: "b", style: .neon, opacity: 0.8, clickThrough: true, fadeWhenIdle: true))
        #expect(!board.interaction.isHovered)
    }
}
