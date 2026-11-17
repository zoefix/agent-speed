import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Testing
@testable import AgentSpeed

/// DESIGN 2 *hide when idle* on a floating orb's window: after the fade's 60 s idle, with its hover and drag deferral,
/// the orb leaves the screen with its plate and chip and draws nothing; the pointer over its old place never brings it
/// back and the place stays taken. Activity, the switch turned off, or its `visible` or the master switch turned on
/// bring it back. Hiding replaces the fade, is never written to `visible`, and Reduce Motion makes both changes cuts.
@Suite("Orb hide when idle", .serialized)
@MainActor
struct OrbHideWhenIdleWindowTests {
    final class FakeClock {
        var now = Clock.now
    }

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

    /// One orb of a fresh model in its own window, far from every display so it never shows on the desktop or meets
    /// the pointer, on a clock the test moves.
    @MainActor
    struct Stage {
        let model: AppModel
        let clock: FakeClock
        let manager: Manager
        let window: OrbWindowController

        var id: String { window.id }
        var panel: NSWindow { window.host.window! }
        var gauge: LiveGauge { window.runtime.feed.gauge }

        func pass(_ seconds: TimeInterval) {
            clock.now += seconds
            window.dimmer?.refresh()
        }

        /// Two samples 1 s apart: past the tier's 0.8 s hold, so the tier leaves idle.
        func work() {
            gauge.ingest(120, now: clock.now)
            clock.now += 1
            gauge.ingest(120, now: clock.now)
            window.dimmer?.refresh()
        }

        /// Back to the idle tier (below cruise for 3 s, then the hold) without moving the clock, so the 60 s still
        /// count from the last activity.
        func rest() {
            for offset in [1.0, 5, 6] { gauge.ingest(0, now: clock.now + offset) }
        }

        func edit(_ change: (OrbsController, String) -> Void) {
            change(model.orbs, id)
            if let config = model.orbs.orb(id) { window.update(config: config) }
        }
    }

    static let away = CGPoint(x: -10_000, y: -10_000)

    static func stage(_ configure: (OrbsController, String) -> Void = { orbs, id in orbs.setHideWhenIdle(true, of: id) }) -> Stage {
        _ = NSApplication.shared
        let suite = "AgentSpeedOrbHideWhenIdleTests"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        let id = model.orbs.orbs[0].id
        configure(model.orbs, id)
        let clock = FakeClock()
        let manager = Manager()
        let window = OrbWindowController(model: model, runtime: model.orbs.runtime(id)!, manager: manager, idleNow: { clock.now })
        window.update(config: model.orbs.orb(id)!)
        window.jump(to: away)
        window.setShown(true)
        return Stage(model: model, clock: clock, manager: manager, window: window)
    }

    /// Waits for the window animations, which run on the main thread and end later while other tests keep it busy.
    static func waitUntil(timeout: Double = 5, _ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition(), Date() < deadline {
            try await Task.sleep(for: .milliseconds(50))
        }
    }

    // MARK: Hiding

    @Test func hidesAfterSixtySecondsIdleNotBefore() async throws {
        let stage = Self.stage()
        defer { stage.window.close() }
        let window = stage.window
        #expect(window.isShown && stage.panel.isVisible)
        stage.pass(59.9)
        #expect(window.isShown && !window.isHiddenWhileIdle)
        stage.pass(0.1)
        #expect(window.isHiddenWhileIdle && !window.isShown)
        if stage.model.accessibility.reduceMotion {
            #expect(stage.panel.alphaValue < 0.01, "Reduce Motion: a cut (AGENT_SPEED_REDUCE_MOTION=1 runs this)")
        }
        // It draws nothing from the moment it starts to fade; the pointer reaches it until it is ordered out.
        #expect(!window.interaction.isVisibleOnScreen)
        #expect(window.pointer.isRunning != stage.model.accessibility.reduceMotion)
        #expect(stage.panel.ignoresMouseEvents, "the pointer is not on it")
        try await Self.waitUntil { !stage.panel.isVisible }
        #expect(!stage.panel.isVisible, "ordered out after the fade")
        #expect(!window.pointer.isRunning)
        #expect(stage.model.orbs.orb(stage.id)?.visible == true, "never written to visible")
        #expect(stage.model.settings.orbsVisible)
    }

    @Test func activityAfterTheLastIdleStartsTheSixtySecondsAgain() {
        let stage = Self.stage()
        defer { stage.window.close() }
        stage.pass(30)
        stage.work()
        stage.rest()
        #expect(stage.gauge.tier == .idle)
        stage.pass(58)
        #expect(stage.window.isShown, "idle again, but not yet 60 s after the last activity")
        stage.pass(2)
        #expect(stage.window.isHiddenWhileIdle)
    }

    @Test func hoverAndDragDeferIt() {
        let stage = Self.stage()
        defer { stage.window.close() }
        let window = stage.window
        window.interaction.isHovered = true
        stage.pass(61)
        #expect(window.isShown, "never while the pointer is on it")
        window.interaction.isHovered = false
        stage.pass(0)
        #expect(window.isShown, "1.5 s after the pointer leaves")
        window.interaction.isDragging = true
        stage.pass(2)
        #expect(window.isShown, "never during a drag")
        window.interaction.isDragging = false
        stage.pass(0)
        #expect(window.isShown)
        stage.pass(1.5)
        #expect(window.isHiddenWhileIdle && !window.isShown)
    }

    @Test(arguments: ["hover", "drag"])
    func aHoverOrADragDuringTheFadeBringsItBack(_ gesture: String) async throws {
        let stage = Self.stage()
        defer { stage.window.close() }
        let window = stage.window
        stage.pass(60)
        #expect(window.isHiddenWhileIdle && !window.isShown)
        guard !stage.model.accessibility.reduceMotion else {
            #expect(!window.pointer.isRunning && stage.panel.ignoresMouseEvents, "Reduce Motion: a cut, with no fade to catch")
            return
        }
        #expect(window.pointer.isRunning && stage.panel.isVisible, "still on screen and under the pointer")
        if gesture == "hover" {
            window.pointer.onMove?(window.center, true)
            #expect(window.interaction.isHovered && !stage.panel.ignoresMouseEvents, "the disc takes the pointer")
        } else {
            // What a press that moves on the disc sets (a drag starts only where the disc takes the pointer).
            window.interaction.isDragging = true
        }
        try await Self.waitUntil { window.isShown && stage.panel.alphaValue > 0.99 }
        #expect(window.isShown && !window.isHiddenWhileIdle)
        #expect(stage.panel.alphaValue > 0.99, "back to full")
        try await Task.sleep(for: .milliseconds(700))
        #expect(stage.panel.isVisible && window.isShown, "the fade's order-out was dropped")

        // It hides again 1.5 s after the pointer leaves or the drag ends, as the fade does.
        if gesture == "hover" {
            window.pointer.onMove?(CGPoint(x: Self.away.x + 1_000, y: Self.away.y + 1_000), true)
            #expect(!window.interaction.isHovered)
        } else {
            window.interaction.isDragging = false
        }
        stage.pass(0)
        stage.pass(1.4)
        #expect(window.isShown)
        stage.pass(0.1)
        #expect(window.isHiddenWhileIdle && !window.isShown)
        try await Self.waitUntil { !stage.panel.isVisible }
        #expect(!stage.panel.isVisible && !window.pointer.isRunning)
    }

    @Test func thePointerPassesThroughOnceItHasLeftTheScreen() async throws {
        let stage = Self.stage()
        defer { stage.window.close() }
        let window = stage.window
        let place = window.center
        stage.pass(60)
        try await Self.waitUntil { !stage.panel.isVisible }
        #expect(!window.pointer.isRunning)
        window.pointer.onMove?(place, true)
        window.hostPointerMoved()
        window.pointerMayHaveMoved()
        #expect(!window.interaction.isHovered && stage.panel.ignoresMouseEvents, "nothing over its old place reaches it")
        stage.pass(30)
        try await Task.sleep(for: .milliseconds(200))
        #expect(window.isHiddenWhileIdle && !window.isShown)
        #expect(!window.pointer.isRunning && !stage.panel.isVisible)
    }

    @Test func itsPlateAndChipGoWithIt() async throws {
        let stage = Self.stage { orbs, id in
            orbs.setHideWhenIdle(true, of: id)
            orbs.setName("Work", of: id)
            orbs.setShowName(true, of: id)
        }
        defer { stage.window.close() }
        let window = stage.window
        let plate = window.nameplate
        // A sources change shows the chip for 2 s while no plate is placed.
        stage.edit { orbs, id in orbs.setSources(.explicit([.codex]), of: id) }
        #expect(window.chipPanel.isShown)
        stage.pass(60)
        #expect(!window.chipPanel.isShown)
        stage.edit { orbs, id in orbs.setSources(.explicit([.codex, .claudeCode]), of: id) }
        #expect(!window.chipPanel.isShown, "no chip while hidden")

        stage.work()
        stage.rest()
        #expect(window.isShown)
        plate.setSlot(NameplateSlot(side: .below, budget: 180, rect: CGRect(x: Self.away.x - 70, y: Self.away.y - 110,
                                                                             width: 140, height: 30)))
        try await Self.waitUntil { plate.window.isVisible }
        #expect(plate.wantsPlate && plate.isPresenting && plate.window.isVisible)
        stage.pass(60)
        #expect(!plate.isPresenting, "the plate leaves with the orb")
        try await Self.waitUntil { !plate.window.isVisible }
        #expect(!plate.window.isVisible)
        stage.work()
        try await Self.waitUntil { plate.window.isVisible }
        #expect(plate.isPresenting && plate.window.isVisible, "and comes back with it")
    }

    @Test func itKeepsItsPlaceWhileHidden() async throws {
        let stage = Self.stage()
        defer { stage.window.close() }
        let window = stage.window
        let center = window.center
        stage.pass(60)
        try await Self.waitUntil { !stage.panel.isVisible }
        #expect(window.holdsPlace && !window.isShown)
        #expect(window.center == center)
        window.setShown(false)
        #expect(!window.holdsPlace, "an orb the user hid gives its place up as before")
    }

    // MARK: Coming back

    @Test func activityBringsItBackAtItsPlaceWithItsOpacity() async throws {
        let stage = Self.stage { orbs, id in
            orbs.setHideWhenIdle(true, of: id)
            orbs.setOpacity(0.6, of: id)
        }
        defer { stage.window.close() }
        let window = stage.window
        let center = window.center
        stage.pass(60)
        try await Self.waitUntil { !stage.panel.isVisible }
        stage.gauge.ingest(120, now: stage.clock.now)
        #expect(window.isHiddenWhileIdle, "the first sample is still within the tier's hold")
        stage.clock.now += 1
        stage.gauge.ingest(120, now: stage.clock.now)
        #expect(stage.gauge.tier != .idle)
        // The dimmer follows the tier on its own.
        try await Self.waitUntil { window.isShown && stage.panel.alphaValue > 0.99 }
        #expect(!window.isHiddenWhileIdle && window.isShown)
        #expect(window.pointer.isRunning, "tracking the pointer again")
        #expect(stage.panel.isVisible && stage.panel.alphaValue > 0.99, "the panel is opaque; the orb's opacity is its body's")
        #expect(abs(window.host.blurView.alphaValue - 0.6) < 0.001)
        #expect(window.center == center)
        #expect(!window.fader.isDimmed)
    }

    @Test func anOrbTheUserHidStaysHidden() async throws {
        let stage = Self.stage()
        defer { stage.window.close() }
        let window = stage.window
        stage.pass(60)
        window.setShown(false)
        #expect(!window.pointer.isRunning, "hidden by the user during the fade: the pointer goes at once")
        stage.work()
        try await Self.waitUntil { !stage.panel.isVisible }
        #expect(!window.isHiddenWhileIdle && !window.isShown)
        #expect(!stage.panel.isVisible, "activity does not undo the user's hide")
        #expect(!window.holdsPlace)
    }

    /// `OrbWindowManager` shows a window when the master switch and its `visible` are both on.
    @Test func turningVisibleOrTheMasterSwitchOnBringsItBackForSixtySeconds() {
        let stage = Self.stage()
        defer { stage.window.close() }
        let window = stage.window
        stage.pass(60)
        #expect(window.isHiddenWhileIdle)
        window.setShown(false)
        stage.pass(30)
        window.setShown(true)
        #expect(window.isShown && !window.isHiddenWhileIdle)
        // The manager calls this again on every document change; only the off-to-on turn restarts the 60 s.
        stage.pass(59)
        window.setShown(true)
        #expect(window.isShown)
        stage.pass(1)
        #expect(window.isHiddenWhileIdle && !window.isShown)
    }

    // MARK: The switch

    @Test func turningItOffBringsItBackAndStartsTheSixtySecondsAgain() {
        let stage = Self.stage { orbs, id in
            orbs.setHideWhenIdle(true, of: id)
            orbs.setFadeWhenIdle(true, of: id)
        }
        defer { stage.window.close() }
        let window = stage.window
        stage.pass(60)
        #expect(window.isHiddenWhileIdle)
        stage.edit { orbs, id in orbs.setHideWhenIdle(false, of: id) }
        #expect(window.isShown && !window.isHiddenWhileIdle)
        #expect(!window.fader.isDimmed, "back at full, not at 45 %")
        stage.pass(59)
        #expect(!window.fader.isDimmed)
        stage.pass(1)
        #expect(window.fader.isDimmed && window.isShown, "fade when idle takes over")
    }

    @Test func turningItOnStartsAFreshSixtySecondsAtFullOpacity() async throws {
        let stage = Self.stage { orbs, id in orbs.setFadeWhenIdle(true, of: id) }
        defer { stage.window.close() }
        let window = stage.window
        stage.pass(90)
        #expect(window.isShown && window.fader.isDimmed, "long idle: faded to 45 %")
        stage.edit { orbs, id in orbs.setHideWhenIdle(true, of: id) }
        #expect(window.isShown && !window.isHiddenWhileIdle, "not hidden at once")
        #expect(!window.fader.isDimmed, "hiding replaces the fade, so it is back at full")
        try await Self.waitUntil { stage.panel.alphaValue > 0.99 }
        #expect(stage.panel.alphaValue > 0.99)
        stage.pass(59)
        #expect(window.isShown && !window.fader.isDimmed)
        stage.pass(1)
        #expect(window.isHiddenWhileIdle && !window.isShown && !window.fader.isDimmed)
    }

    @Test func itIsOffByDefault() {
        let stage = Self.stage { _, _ in }
        defer { stage.window.close() }
        #expect(stage.model.orbs.orb(stage.id)?.hideWhenIdle == false)
        stage.pass(600)
        #expect(stage.window.isShown && !stage.window.isHiddenWhileIdle)
    }

    @Test func hidingReplacesTheFade() async throws {
        let stage = Self.stage { orbs, id in
            orbs.setFadeWhenIdle(true, of: id)
            orbs.setHideWhenIdle(true, of: id)
        }
        defer { stage.window.close() }
        let window = stage.window
        stage.pass(60)
        #expect(window.isHiddenWhileIdle && !window.fader.isDimmed, "no 45 % step")
        stage.work()
        if stage.model.accessibility.reduceMotion {
            #expect(stage.panel.alphaValue > 0.99, "Reduce Motion: a cut (AGENT_SPEED_REDUCE_MOTION=1 runs this)")
        }
        try await Self.waitUntil { stage.panel.alphaValue > 0.99 }
        #expect(window.isShown && !window.fader.isDimmed && stage.panel.alphaValue > 0.99)
    }

    // MARK: Motion

    @Test func itFadesOutOverSixTenthsAndBackInATenthReduceMotionCutsBoth() {
        #expect(IdleFade.motion(dimming: true, reduceMotion: false) == .init(duration: 0.6, timing: .easeInEaseOut))
        #expect(IdleFade.motion(dimming: false, reduceMotion: false) == .init(duration: 0.12, timing: .easeOut))
        let panel = NSPanel(contentRect: NSRect(origin: Self.away, size: NSSize(width: 80, height: 80)), styleMask: .borderless,
                            backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        defer { panel.close() }
        let fader = OrbWindowFader(panel: panel)
        fader.setShown(true, motion: IdleFade.motion(dimming: false, reduceMotion: true))
        #expect(panel.isVisible && panel.alphaValue > 0.99, "a cut in")
        fader.setShown(false, motion: IdleFade.motion(dimming: true, reduceMotion: true))
        #expect(panel.alphaValue < 0.01, "a cut out")
    }

    @Test func comingBackMidFadeKeepsThePanelAndThePlatesOpacity() async throws {
        _ = NSApplication.shared
        let panel = NSPanel(contentRect: NSRect(origin: Self.away, size: NSSize(width: 80, height: 80)), styleMask: .borderless,
                            backing: .buffered, defer: false)
        let follower = NSPanel(contentRect: NSRect(origin: Self.away, size: NSSize(width: 40, height: 20)), styleMask: .borderless,
                               backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        follower.isReleasedWhenClosed = false
        defer {
            panel.close()
            follower.close()
        }
        let fader = OrbWindowFader(panel: panel)
        fader.follower = follower
        fader.followerFactor = 0.7
        fader.setShown(true, motion: IdleFade.motion(dimming: false, reduceMotion: true))
        fader.setShown(false, motion: IdleFade.motion(dimming: true, reduceMotion: false))
        fader.setShown(true, motion: IdleFade.motion(dimming: false, reduceMotion: false))
        try await Task.sleep(for: .milliseconds(700))
        try await Self.waitUntil { panel.alphaValue > 0.99 }
        #expect(panel.isVisible, "the pending order-out of the fade was dropped")
        #expect(panel.alphaValue > 0.99)
        #expect(abs(follower.alphaValue - 0.7) < 0.001)
    }
}
