import AgentSpeedOrbModel
import AppKit
import Testing
@testable import AgentSpeed

@Suite("Board idle fade")
@MainActor
struct BoardIdleDimTests {
    @Test func dimsBothPanelsAtFortyFivePercentAndRestores() {
        var config = BoardConfig(id: "b0", style: .glass, size: .medium, opacity: 0.8, fadeWhenIdle: true)
        let window = BoardWindowController(config: config, center: .zero, dark: true)
        window.reduceMotion = true
        #expect(abs(window.panel.alphaValue - 0.8) < 0.001)
        #expect(abs(window.effectsPanel.alphaValue - 0.8) < 0.001)

        window.setDimmed(true)
        #expect(window.isDimmed)
        #expect(abs(window.panel.alphaValue - 0.8 * 0.45) < 0.001)
        #expect(abs(window.effectsPanel.alphaValue - 0.8 * 0.45) < 0.001)

        config.opacity = 1.0
        window.apply(config)
        #expect(abs(window.panel.alphaValue - 0.45) < 0.001)
        #expect(abs(window.effectsPanel.alphaValue - 0.45) < 0.001)

        config.fadeWhenIdle = false
        window.apply(config)
        #expect(!window.isDimmed)
        #expect(abs(window.panel.alphaValue - 1.0) < 0.001)
        #expect(abs(window.effectsPanel.alphaValue - 1.0) < 0.001)
        window.close()
    }

    @Test func dimmerTracksBoardActivityHoverAndToggle() {
        let gauge = LiveGauge()
        let config = BoardConfig(id: "b1", style: .neon, size: .medium, opacity: 1.0, fadeWhenIdle: true)
        let window = BoardWindowController(config: config, center: .zero, dark: true)
        window.reduceMotion = true
        let now = Clock.now
        let dimmer = OrbWindowIdleDimmer(gauge: gauge, enabled: { config.fadeWhenIdle },
                                         interaction: window.interaction, armedAt: now - 120) { dimmed in
            window.setDimmed(dimmed)
        }
        #expect(window.isDimmed)
        #expect(abs(window.panel.alphaValue - 0.45) < 0.001)

        window.interaction.isHovered = true
        dimmer.refresh()
        #expect(!window.isDimmed)
        #expect(abs(window.panel.alphaValue - 1.0) < 0.001)

        window.interaction.isHovered = false
        gauge.snap(to: 180, now: Clock.now)
        dimmer.refresh()
        #expect(!window.isDimmed)

        dimmer.cancel()
        window.close()
    }
}
