import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Testing
@testable import AgentSpeed

/// DESIGN 12 *Keyboard* on the board's window: it can become key only while its rows take focus, a click never makes
/// it key, it leaves the window cycle otherwise; the focus ring is a cached piece around the row in the system focus
/// color; sub rows are VoiceOver items in the group rows' style.
@Suite("Board keyboard")
@MainActor
struct BoardKeyboardTests {
    @Test func thePanelTakesTheKeyboardOnlyWhenItsRowsTakeFocus() {
        let panel = BoardPanel()
        panel.apply(alwaysOnTop: true, allSpaces: true)
        #expect(!panel.canBecomeKey && !panel.canBecomeMain)
        #expect(panel.collectionBehavior.contains(.ignoresCycle))
        #expect(panel.styleMask.contains(.nonactivatingPanel) && panel.becomesKeyOnlyIfNeeded)
        panel.keyboardFocusable = true
        #expect(panel.canBecomeKey && !panel.canBecomeMain)
        #expect(!panel.collectionBehavior.contains(.ignoresCycle), "the window cycle can reach it")
        panel.apply(alwaysOnTop: false, allSpaces: false)
        #expect(!panel.collectionBehavior.contains(.ignoresCycle), "a settings change keeps it reachable")
        panel.keyboardFocusable = false
        #expect(!panel.canBecomeKey && panel.collectionBehavior.contains(.ignoresCycle))
        panel.close()
    }

    @Test func aClickNeverMakesTheBoardKey() {
        let host = BoardHostView(warp: CALayer())
        #expect(!host.needsPanelToBecomeKey)
        #expect(!host.acceptsFirstResponder)
        host.keyboardFocusable = true
        #expect(host.acceptsFirstResponder && !host.needsPanelToBecomeKey)
    }

    @Test func theRingIsACachedPieceAroundTheRow() throws {
        let row = BoardContentItem(key: "a", frame: CGRect(x: 10, y: 100, width: 300, height: 38), revision: AnyHashable("row")) { _, _ in }
        let ring = BoardFocusRing.item(around: row, style: .glass, dark: true, highContrast: false)
        #expect(ring.key == "a#focus" && ring.motion == .row, "it springs with its row")
        #expect(ring.frame.contains(row.frame) && ring.frame.minX == row.frame.minX - 4)
        let contrast = BoardFocusRing.item(around: row, style: .glass, dark: true, highContrast: true)
        #expect(contrast.revision != ring.revision && contrast.frame.width > ring.frame.width)
        #expect(BoardFocusRing.color(dark: true, highContrast: true).alpha == 1)
        #expect(BoardFocusRing.color(dark: false, highContrast: false).alpha < 1, "the system's half-strength focus color")
        // The ring hugs the outside of the row: its band starts at the row's edge, the row's inside stays clear.
        let image = try #require(BoardRaster.image(size: ring.frame.size, scale: 2, draw: ring.draw))
        func alpha(_ x: CGFloat, _ y: CGFloat) -> UInt8 {
            let data = image.dataProvider!.data! as Data
            let offset = Int(y * 2) * image.bytesPerRow + Int(x * 2) * 4 + 3
            return data[offset]
        }
        let mid = ring.frame.height / 2
        #expect(alpha(2.5, mid) > 60, "the band just outside the row's left edge")
        #expect(alpha(8, mid) == 0, "inside the row")
    }

    @Test func subRowsReadInTheGroupRowsStyle() {
        let l10n = Localizer(preference: .zhHant)
        var agent = AgentStatus(id: "s", source: .claudeCode, project: "explore", model: "m", phase: .generating, isSubagent: true,
                                live: Rate(output: 118, total: 118), recentSpeed: nil, lastActivity: Date(), parentId: "r")
        agent = BoardScorePreviewData.scored(agent, score: 1048.2, provisional: true)
        let label = BoardAccessibilityReporter.subRowLabel(agent, metric: .output, l10n: l10n)
        #expect(label == "explore，子 agent，118 tok/s，性能 1,048，初步（樣本少）")
    }
}
