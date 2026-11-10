import AppKit

/// Event-driven pointer tracking for the orb: global monitors see the pointer while the panel ignores the mouse
/// (events go to other apps), local monitors while it is over our own windows. No timers, no polling. Presses count
/// too: a press another app received on the disc means a move went unseen, and fixes the routing for the next one.
@MainActor
final class OrbWindowPointerTracker {
    /// Pointer location in screen coordinates, and whether the event was a plain move (no button held).
    var onMove: ((NSPoint, Bool) -> Void)?

    /// True while another app is tracking a mouse drag, so the disc must not grab the pointer mid-drag.
    private(set) var isForeignDragActive = false

    private static let events: NSEvent.EventTypeMask = [
        .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged, .leftMouseUp, .rightMouseUp,
        .leftMouseDown, .rightMouseDown, .otherMouseDown,
    ]

    nonisolated(unsafe) private var monitors: [Any] = []

    var isRunning: Bool { !monitors.isEmpty }

    func start() {
        guard monitors.isEmpty else { return }
        if let global = NSEvent.addGlobalMonitorForEvents(matching: Self.events, handler: { [weak self] event in
            MainActor.assumeIsolated { self?.handle(event, foreign: true) }
        }) {
            monitors.append(global)
        }
        if let local = NSEvent.addLocalMonitorForEvents(matching: Self.events, handler: { [weak self] event in
            MainActor.assumeIsolated { self?.handle(event, foreign: false) }
            return event
        }) {
            monitors.append(local)
        }
    }

    func stop() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors.removeAll()
        isForeignDragActive = false
    }

    deinit {
        monitors.forEach(NSEvent.removeMonitor)
    }

    private func handle(_ event: NSEvent, foreign: Bool) {
        switch event.type {
        case .leftMouseDragged, .rightMouseDragged, .otherMouseDragged: isForeignDragActive = foreign
        case .mouseMoved, .leftMouseDown, .rightMouseDown, .otherMouseDown: isForeignDragActive = false
        default: isForeignDragActive = foreign && NSEvent.pressedMouseButtons != 0
        }
        if foreign, event.type == .rightMouseDown || event.type == .leftMouseDown {
            OrbWindowLog.debug("press in another app at \(OrbWindowLog.describe(NSEvent.mouseLocation))")
        }
        onMove?(NSEvent.mouseLocation, Self.isPlainMove(event.type, pressedButtons: NSEvent.pressedMouseButtons))
    }

    /// A move with no button held now. A move the window server sent before a press can reach a monitor just after
    /// it; with the button still down it is no sign of a lost mouse-up, so it must not end the drag that press began.
    nonisolated static func isPlainMove(_ type: NSEvent.EventType, pressedButtons: Int) -> Bool {
        type == .mouseMoved && pressedButtons == 0
    }
}
