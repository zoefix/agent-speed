import AppKit
import Testing
@testable import AgentSpeed

/// DESIGN 2b, Context menu item 4: the size submenu's slider moves in 4 pt steps over 64–240 pt, reports each new size
/// once, shows it as `N pt`, steps from VoiceOver and leaves the arrow keys to the menu.
@Suite("Orb size slider item")
@MainActor
struct OrbSizeSliderItemTests {
    final class Recorder {
        var sizes: [Double] = []
    }

    static func make(_ size: Double, _ recorder: Recorder) -> (OrbSizeSliderItem, NSSlider, NSTextField) {
        let item = OrbSizeSliderItem(size: size, label: "Size", valueText: { "\($0) pt" }) { recorder.sizes.append($0) }
        let subviews = item.view?.subviews ?? []
        let slider = subviews.compactMap { $0 as? NSSlider }.first ?? NSSlider()
        let label = subviews.compactMap { $0 as? NSTextField }.first ?? NSTextField()
        return (item, slider, label)
    }

    @Test func dragsSnapToTheStepAndReportEachSizeOnce() {
        let recorder = Recorder()
        let (item, slider, label) = Self.make(148, recorder)
        defer { withExtendedLifetime(item) {} }
        #expect(slider.minValue == 64 && slider.maxValue == 240 && slider.isContinuous)
        #expect(label.stringValue == "148 pt")
        for value in [101.0, 101.4, 99, 63, 10, 300] {
            slider.doubleValue = value
            slider.sendAction(slider.action, to: slider.target)
        }
        #expect(recorder.sizes == [100, 64, 240])
        #expect(label.stringValue == "240 pt")
        #expect(slider.doubleValue == 240)
    }

    @Test func voiceOverStepsFourPointsAndTheRowNeverTakesFocus() {
        let recorder = Recorder()
        let (item, slider, label) = Self.make(236, recorder)
        defer { withExtendedLifetime(item) {} }
        #expect(slider.refusesFirstResponder)
        #expect(slider.accessibilityPerformIncrement())
        #expect(slider.accessibilityPerformIncrement())
        #expect(recorder.sizes == [240])
        #expect(slider.accessibilityPerformDecrement())
        #expect(slider.accessibilityPerformDecrement())
        #expect(slider.accessibilityPerformIncrement())
        #expect(recorder.sizes == [240, 236, 232, 236])
        #expect(label.stringValue == "236 pt")
        #expect(slider.accessibilityLabel() == "Size")
        #expect(slider.accessibilityValueDescription() == "236 pt")
    }

    @Test func aSizeOutsideTheRangeStartsClamped() {
        let recorder = Recorder()
        let (_, slider, label) = Self.make(20, recorder)
        #expect(slider.doubleValue == 64)
        #expect(label.stringValue == "64 pt")
        #expect(recorder.sizes.isEmpty)
    }
}
