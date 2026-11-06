import AppKit
import Foundation
import QuartzCore
import Testing
@testable import AgentSpeed

/// DESIGN 5, Effects: `mac.settings.effects.displaySync` is on by default, persists, and new canvases present the way
/// it says (issue #48).
@Suite("Display sync setting")
@MainActor
struct DisplaySyncTests {
    private static func store() -> UserDefaults {
        let suite = "DisplaySyncTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        return defaults
    }

    @Test func isOnByDefaultAndPersists() {
        let defaults = Self.store()
        #expect(AppSettings(defaults: defaults).displaySync)
        AppSettings(defaults: defaults).displaySync = false
        #expect(!AppSettings(defaults: defaults).displaySync)
    }

    @Test func canvasesPresentAsTheSettingSays() {
        let saved = OrbCanvasView.displaySync
        defer { OrbCanvasView.displaySync = saved }
        OrbCanvasView.displaySync = true
        #expect((OrbCanvasView(frame: .zero).layer as? CAMetalLayer)?.displaySyncEnabled == true)
        OrbCanvasView.displaySync = false
        #expect((OrbCanvasView(frame: .zero).layer as? CAMetalLayer)?.displaySyncEnabled == false)
    }

    @Test func aDrawnCanvasFollowsAChangeAtItsNextFrame() throws {
        let saved = OrbCanvasView.displaySync
        defer { OrbCanvasView.displaySync = saved }
        OrbCanvasView.displaySync = true
        let canvas = OrbCanvasView(frame: CGRect(x: 0, y: 0, width: 16, height: 16))
        canvas.sceneProvider = { _, _, _, _ in }
        let layer = try #require(canvas.layer as? CAMetalLayer)
        OrbCanvasView.displaySync = false
        #expect(layer.displaySyncEnabled)
        canvas.renderFrame(at: 0)
        #expect(canvas.framesDrawn == 1)
        #expect(!layer.displaySyncEnabled)
    }
}
