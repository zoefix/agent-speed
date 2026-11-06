import AgentSpeedOrbModel
import AppKit

/// Switching an orb's unit by clicking it (DESIGN 9, Orb, Switching 1): a press and release within 4 pt of movement
/// and 0.5 s on the unit line toggles once the system double-click interval has passed without a second click, so a
/// double-click still opens the dashboard and never toggles; a drag that starts on the unit moves the orb.
@MainActor
final class OrbUnitToggle {
    static let maxMovement: CGFloat = 4
    static let maxPress: TimeInterval = 0.5
    /// The hit area is the unit text grown to at least this, clipped to the disc.
    static let minimumHitSize = CGSize(width: 44, height: 20)

    var onToggle: (() -> Void)?

    private var press: (time: TimeInterval, location: NSPoint)?
    private var pending: Timer?

    /// A press on the disc; `onUnit` when it starts inside the unit's hit area.
    func mouseDown(_ event: NSEvent, onUnit: Bool) {
        if event.clickCount >= 2 {
            cancel()
            return
        }
        press = onUnit ? (event.timestamp, NSEvent.mouseLocation) : nil
    }

    /// The release; `moved` when the press turned into a drag.
    func mouseUp(_ event: NSEvent, moved: Bool) {
        guard let press else { return }
        self.press = nil
        let location = NSEvent.mouseLocation
        let distance = hypot(location.x - press.location.x, location.y - press.location.y)
        guard !moved, distance <= Self.maxMovement, event.timestamp - press.time <= Self.maxPress else { return }
        let delay = max(0, NSEvent.doubleClickInterval - (event.timestamp - press.time))
        pending?.invalidate()
        let timer = Timer(timeInterval: delay, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.pending = nil
                self?.onToggle?()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        pending = timer
    }

    func cancel() {
        press = nil
        pending?.invalidate()
        pending = nil
    }

    /// The hit area in the host view (y up): the unit line, or the value without one (`minimal`, small orbs).
    static func hitRect(layout: OrbReadoutLayout, discDiameter: CGFloat, in bounds: NSRect, unit: String, value: String) -> NSRect {
        let s = discDiameter
        let center = NSPoint(x: bounds.midX, y: bounds.midY)
        let text: String
        let font: NSFont
        let baselineBelowCenter: CGFloat
        let tracking: CGFloat
        if let unitSize = layout.unitSize {
            let size = max(OrbReadoutBuilder.textFloor, unitSize * s)
            text = unit
            font = .systemFont(ofSize: size, weight: .medium)
            baselineBelowCenter = (layout.valueBaseline + layout.unitGap) * s
            tracking = 0.5 * s / 148
        } else {
            text = value
            font = .monospacedDigitSystemFont(ofSize: layout.valueSize * s, weight: .bold)
            baselineBelowCenter = layout.valueBaseline * s
            tracking = 0
        }
        let measured = (text as NSString).size(withAttributes: [.font: font])
        let width = max(minimumHitSize.width, measured.width + tracking * CGFloat(text.count))
        let height = max(minimumHitSize.height, font.capHeight + 2 * max(4, font.capHeight * 0.4))
        let midY = center.y - baselineBelowCenter + font.capHeight / 2
        let rect = NSRect(x: center.x - width / 2, y: midY - height / 2, width: width, height: height)
        let disc = NSRect(x: center.x - s / 2, y: center.y - s / 2, width: s, height: s)
        return rect.intersection(disc)
    }

    /// Whether a point of the host view is in the hit area and on the disc.
    static func contains(_ point: NSPoint, rect: NSRect, discDiameter: CGFloat, in bounds: NSRect) -> Bool {
        rect.contains(point) && hypot(point.x - bounds.midX, point.y - bounds.midY) <= discDiameter / 2
    }
}
