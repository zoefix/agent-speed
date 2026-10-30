import AgentSpeedCore
import AppKit
import SwiftUI
import Testing
@testable import AgentSpeed

/// The day under the pointer is the day drawn there (issue #42: on macOS 27 the pointer's local space behind
/// `.offset` still includes the offset, so the chart picked a day one y-axis width to the right).
@Suite("History chart pointer")
@MainActor
struct HistoryChartPointerTests {
    let today = DayKey("2026-10-28")!

    private func chart() -> HistoryChartModel {
        HistoryContent.build(
            usage: PreviewHistory.days(endingAt: today.date(in: HistoryDates.calendar), count: 40), source: nil, rangeKind: .days30,
            customRange: nil, sort: HistorySort(), today: today, l10n: Localizer(preference: .en),
            costOptions: HistoryCostOptions(metric: .output), traffic: .none
        ).chart
    }

    /// Clicks at `x` (window points from the left), halfway down the plot, and returns the day the chart revealed.
    private func click(_ window: NSWindow, x: CGFloat, interaction: HistoryInteraction) -> DayKey? {
        let before = interaction.reveal?.id
        let point = NSPoint(x: x, y: window.frame.height * 0.45)
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            guard let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [],
                                                 timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
                                                 context: nil, eventNumber: 0, clickCount: 1, pressure: 1) else { return nil }
            window.sendEvent(event)
        }
        RunLoop.main.run(until: Date().addingTimeInterval(0.03))
        return interaction.reveal?.id == before ? nil : interaction.reveal?.day
    }

    @Test func theFirstPointInsideThePlotIsTheRangesFirstDay() throws {
        _ = NSApplication.shared
        let chart = chart()
        let interaction = HistoryInteraction()
        let size = NSSize(width: 720, height: 300)
        let window = NSWindow(contentRect: NSRect(origin: NSPoint(x: -4000, y: -4000), size: size), styleMask: [.borderless],
                              backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        let host = NSHostingView(rootView: HistoryChartView(chart: chart, interaction: interaction, l10n: Localizer(preference: .en),
                                                             motion: nil, reduceMotion: true)
            .frame(width: size.width, height: size.height))
        window.contentView = host
        window.orderFrontRegardless()
        host.layoutSubtreeIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.3))

        // The plot starts right of the y-axis labels; its first hit is the first point of the range's first day.
        var first: (x: CGFloat, day: DayKey)?
        for x in stride(from: CGFloat(1), to: size.width / 2, by: 2) {
            if let day = click(window, x: x, interaction: interaction) {
                first = (x, day)
                break
            }
        }
        let hit = try #require(first, "no click reached the plot")
        #expect(hit.x > 10, "the y-axis labels sit left of the plot")
        #expect(hit.day == chart.range.start)

        // And the last point before the trailing axis is the range's last day.
        var last: DayKey?
        for x in stride(from: size.width - 1, to: size.width / 2, by: -2) {
            if let day = click(window, x: x, interaction: interaction) {
                last = day
                break
            }
        }
        #expect(last == chart.range.end)
    }
}
