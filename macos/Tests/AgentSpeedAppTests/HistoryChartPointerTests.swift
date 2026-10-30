import AgentSpeedCore
import AppKit
import SwiftUI
import Testing
@testable import AgentSpeed

/// Whether this session delivers synthesized clicks to an offscreen window: a signed-in desktop does, a CI runner
/// without a window server session may not, and there the chart tests cannot observe anything.
@MainActor
enum SyntheticClicks {
    private final class Box { var tapped = false }
    private static var probed: Bool?

    static var delivered: Bool {
        if let probed { return probed }
        let result = probe()
        probed = result
        return result
    }

    private static func probe() -> Bool {
        _ = NSApplication.shared
        let box = Box()
        let window = NSWindow(contentRect: NSRect(x: -4000, y: -4000, width: 40, height: 40), styleMask: [.borderless],
                              backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        window.contentView = NSHostingView(rootView: Color.clear.contentShape(Rectangle()).onTapGesture { box.tapped = true })
        window.orderFrontRegardless()
        RunLoop.main.run(until: Date().addingTimeInterval(0.2))
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            guard let event = NSEvent.mouseEvent(with: type, location: NSPoint(x: 20, y: 20), modifierFlags: [],
                                                 timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
                                                 context: nil, eventNumber: 0, clickCount: 1, pressure: 1) else { return false }
            window.sendEvent(event)
        }
        RunLoop.main.run(until: Date().addingTimeInterval(0.1))
        return box.tapped
    }
}

/// The day under the pointer is the day drawn there (issue #42: on macOS 27 the pointer's local space behind
/// `.offset` still includes the offset, so the chart picked a day one y-axis width to the right).
@Suite("History chart pointer", .enabled("needs an interactive session that delivers synthesized clicks; not on CI runners") {
    if ProcessInfo.processInfo.environment["CI"] != nil { return false }
    return await MainActor.run { SyntheticClicks.delivered }
})
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

        // Clicks land only on the plot, so its edges are found by bisection from the middle (about 20 clicks): the
        // first hit right of the y-axis labels is the range's first day, the last one before the trailing axis its last.
        func edge(towards outside: CGFloat) -> (x: CGFloat, day: DayKey)? {
            let inside = size.width / 2
            guard let day = click(window, x: inside, interaction: interaction) else { return nil }
            var hit = (x: inside, day: day), miss = outside
            while abs(hit.x - miss) > 0.5 {
                let middle = (hit.x + miss) / 2
                if let day = click(window, x: middle, interaction: interaction) { hit = (middle, day) } else { miss = middle }
            }
            return hit
        }
        let first = try #require(edge(towards: 0), "no click reached the plot")
        #expect(first.x > 10, "the y-axis labels sit left of the plot")
        #expect(first.day == chart.range.start)
        #expect(edge(towards: size.width)?.day == chart.range.end)
    }
}
