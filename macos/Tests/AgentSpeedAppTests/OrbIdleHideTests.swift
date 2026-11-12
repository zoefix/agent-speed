import AgentSpeedOrbModel
import AppKit
import Testing
@testable import AgentSpeed

@Suite("Orb completely hidden when idle", .serialized)
@MainActor
struct OrbIdleHideTests {
    final class Clock { var now: TimeInterval = 1000 }

    final class Manager: OrbWindowManaging {
        func otherDiscs(excluding id: String, on screen: NSScreen) -> [OrbDisc] { [] }
        func boardShapes(on screen: NSScreen) -> [FloatingShape] { [] }
        func orbSettled(_ id: String, center: CGPoint, radius: CGFloat, on screen: NSScreen) {}
        func orbInteracted(_ id: String) {}
        func orbHoverChanged(_ id: String, hovered: Bool) {}
        func showGuides(_ guides: [OrbGuide], on screen: NSScreen?) {}
        func orbMoved(_ id: String) {}
        func nameplatesNeedLayout() {}
    }

    private func model() -> AppModel {
        _ = NSApplication.shared
        let suite = "OrbIdleHideTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        let model = AppModel(settings: AppSettings(defaults: defaults),
                             updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults)))
        return model
    }

    @Test func idleBoundaryHoverDragAndModeReset() {
        let clock = Clock()
        let gauge = LiveGauge()
        let interaction = OrbInteraction()
        var idle = false
        let dimmer = OrbWindowIdleDimmer(gauge: gauge, enabled: { true }, interaction: interaction, now: { clock.now }) { idle = $0 }
        defer { dimmer.cancel() }
        clock.now += 59.99
        dimmer.refresh()
        #expect(!idle)
        clock.now += 0.01
        dimmer.refresh()
        #expect(idle)
        interaction.isHovered = true
        dimmer.refresh()
        #expect(!idle)
        interaction.isHovered = false
        interaction.isDragging = true
        dimmer.refresh()
        #expect(!idle)
        interaction.isDragging = false
        dimmer.refresh()
        clock.now += 2
        dimmer.refresh()
        #expect(idle)
        dimmer.refresh(resetDelay: true)
        #expect(!idle)
        clock.now += 59
        dimmer.refresh()
        #expect(!idle)
        clock.now += 1
        dimmer.refresh()
        #expect(idle)
        // New activity must restore even before tier hysteresis leaves .idle.
        gauge.ingest(120, now: clock.now)
        dimmer.refresh()
        #expect(!idle)
    }

    @Test(arguments: [false, true])
    func hidesWithoutInterceptingMouseAndRestoresWithoutChangingManualVisibility(clickThrough: Bool) async throws {
        let model = model()
        let clock = Clock()
        let manager = Manager()
        let config = OrbConfig(id: "a", clickThrough: clickThrough, showName: true, idleBehavior: .hide)
        let runtime = OrbRuntime(config: config)
        let window = OrbWindowController(model: model, runtime: runtime, manager: manager, idleNow: { clock.now })
        defer { window.close() }
        // Keep the test windows away from the live pointer and the user's desktop.
        window.jump(to: CGPoint(x: -10_000, y: -10_000))
        window.setShown(true)
        let panel = try #require(window.host.window)
        #expect(window.isShown)
        window.nameplate.setSlot(NameplateSlot(side: .below, budget: 180,
                                               rect: CGRect(x: -10_070, y: -10_110, width: 140, height: 30)))
        try await Task.sleep(for: .milliseconds(350))
        #expect(window.nameplate.window.isVisible)
        let originalCenter = window.center
        clock.now += 60
        window.dimmer?.refresh()
        // Allow AppKit animation completions and ObservationLoop callbacks to run.
        try await Task.sleep(for: .milliseconds(750))
        #expect(!window.isShown)
        #expect(!panel.isVisible)
        #expect(panel.ignoresMouseEvents)
        #expect(!window.nameplate.window.isVisible)
        #expect(window.config.visible)
        window.pointerMayHaveMoved()
        #expect(!window.isShown)
        runtime.feed.gauge.ingest(120, now: clock.now)
        try await Task.sleep(for: .milliseconds(250))
        #expect(window.isShown)
        #expect(panel.isVisible)
        #expect(panel.alphaValue > 0.99)
        #expect(window.center == originalCenter)
        #expect(window.nameplate.window.isVisible)
        // Manual hide wins over activity and mode edits.
        window.setShown(false)
        runtime.feed.gauge.ingest(150, now: clock.now + 1)
        var next = config
        next.idleBehavior = .keepVisible
        window.update(config: next)
        try await Task.sleep(for: .milliseconds(250))
        #expect(!window.isShown)
        #expect(!panel.isVisible)
    }

    @Test func eachGaugeHidesAndRecoversIndependently() {
        let clock = Clock()
        let first = LiveGauge()
        let second = LiveGauge()
        var firstIdle = false
        var secondIdle = false
        let a = OrbWindowIdleDimmer(gauge: first, enabled: { true }, interaction: OrbInteraction(), now: { clock.now }) { firstIdle = $0 }
        let b = OrbWindowIdleDimmer(gauge: second, enabled: { true }, interaction: OrbInteraction(), now: { clock.now }) { secondIdle = $0 }
        defer { a.cancel(); b.cancel() }
        clock.now += 60
        a.refresh()
        b.refresh()
        #expect(firstIdle && secondIdle)
        first.ingest(100, now: clock.now)
        a.refresh()
        b.refresh()
        #expect(!firstIdle && secondIdle)
    }

    @Test func activityObservationIsOptInSoBoardsKeepTheirTierBasedFade() async throws {
        let clock = Clock()
        let gauge = LiveGauge()
        var boardIdle = false
        var orbIdle = false
        let board = OrbWindowIdleDimmer(gauge: gauge, enabled: { true }, interaction: OrbInteraction(), now: { clock.now }) {
            boardIdle = $0
        }
        let orb = OrbWindowIdleDimmer(gauge: gauge, enabled: { true }, interaction: OrbInteraction(), now: { clock.now },
                                     observesActivity: true) { orbIdle = $0 }
        defer { board.cancel(); orb.cancel() }
        clock.now += 60
        board.refresh()
        orb.refresh()
        #expect(boardIdle && orbIdle)
        gauge.ingest(120, now: clock.now)
        #expect(gauge.tier == .idle, "the first sample has not passed tier hysteresis")
        try await Task.sleep(for: .milliseconds(50))
        #expect(boardIdle, "boards still wait for the tier to change")
        #expect(!orbIdle, "orbs restore on the first activity sample")
    }

    @Test func rapidRecoveryCancelsPendingOrderOutAndKeepsOpacityFactors() async throws {
        _ = NSApplication.shared
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 80, height: 80), styleMask: .borderless,
                            backing: .buffered, defer: false)
        let follower = NSPanel(contentRect: .zero, styleMask: .borderless, backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        follower.isReleasedWhenClosed = false
        defer { panel.close(); follower.close() }
        let fader = OrbWindowFader(panel: panel)
        fader.follower = follower
        fader.followerFactor = 0.7
        fader.setShown(true, motion: IdleFade.motion(dimming: false, reduceMotion: true))
        fader.setShown(false, motion: IdleFade.motion(dimming: true, reduceMotion: false))
        fader.setShown(true, motion: IdleFade.motion(dimming: false, reduceMotion: false))
        try await Task.sleep(for: .milliseconds(750))
        #expect(panel.isVisible)
        #expect(panel.alphaValue > 0.99)
        #expect(abs(follower.alphaValue - 0.7) < 0.001)
        fader.setDimmed(true, reduceMotion: true)
        #expect(abs(panel.alphaValue - 0.45) < 0.001)
        fader.setDimmed(false, reduceMotion: true)
        #expect(panel.alphaValue > 0.99)
        #expect(abs(follower.alphaValue - 0.7) < 0.001)
    }

    @Test func modeChangesRestoreAndGiveHideAFreshDelay() {
        let model = model()
        let clock = Clock()
        let manager = Manager()
        var config = OrbConfig(id: "a", idleBehavior: .hide)
        let runtime = OrbRuntime(config: config)
        let window = OrbWindowController(model: model, runtime: runtime, manager: manager, idleNow: { clock.now })
        defer { window.close() }
        window.setShown(true)
        clock.now += 61
        window.dimmer?.refresh()
        #expect(!window.isShown)
        config.idleBehavior = .keepVisible
        window.update(config: config)
        #expect(window.isShown)
        config.idleBehavior = .hide
        window.update(config: config)
        #expect(window.isShown)
        clock.now += 59
        window.dimmer?.refresh()
        #expect(window.isShown)
        clock.now += 1
        window.dimmer?.refresh()
        #expect(!window.isShown)
    }
}
