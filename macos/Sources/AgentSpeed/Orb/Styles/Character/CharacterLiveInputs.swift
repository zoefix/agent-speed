import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// What a living character reacts to, shared by the orb's runtime (the agents), its window (the pointer) and its
/// renderer, which reads it every frame (CHARACTERS 5, Behavior, Inputs). Plain storage: writing it never redraws.
@MainActor
final class OrbCharacterLive: Equatable {
    // The agents, every engine tick (OrbRuntime).
    var phase = AgentActivity.idle
    /// Clock times (`Clock.now`): the request still waiting for its first output started, the newest response ended.
    var waitingSince: TimeInterval?
    var idleSince: TimeInterval = Clock.now
    var hour = 12
    private(set) var peaks = 0
    private(set) var troubles = 0
    /// The orb's new peaks cheer her only while peaks are celebrated (Settings).
    var celebratesPeaks = true

    // The pointer (OrbWindowController).
    /// The orb's window, to place the pointer on her canvas; nil for embedded orbs and previews.
    weak var window: NSWindow?
    /// The pointer may be read: the orb shows, takes the mouse (not click-through) and is not closing.
    var pointerAllowed = false
    var isDragging = false
    private(set) var clicks = 0
    /// The screen point (y up) of the last click's press.
    private(set) var clickPoint = NSPoint.zero

    // The frame harness: a scripted pointer instead of the real one.
    /// The cursor in her heights relative to her face (x right, y down); `scripted` stops polling the pointer.
    var scripted = false
    var scriptedCursor: (x: Double, y: Double)?

    nonisolated static func == (a: OrbCharacterLive, b: OrbCharacterLive) -> Bool { a === b }

    func notePeak() { peaks &+= 1 }
    func noteTrouble() { troubles &+= 1 }

    func click(at point: NSPoint) {
        clickPoint = point
        clicks &+= 1
    }

    /// A scripted click on her figure (the frame harness).
    func scriptedClick() { clicks &+= 1 }

    /// Takes one engine tick of the orb's agents.
    func update(_ activity: CharacterActivity, now: TimeInterval) {
        phase = switch activity.phase {
        case .generating: .generating
        case .tools: .tools
        case .idle: .idle
        }
        waitingSince = activity.waitingSince?.timeIntervalSinceReferenceDate
        idleSince = activity.idleSince.timeIntervalSinceReferenceDate
        hour = Calendar.current.component(.hour, from: Date(timeIntervalSinceReferenceDate: now))
    }

    /// The pointer on her canvas (points, y down, before the hover scale), or nil while it cannot be read: not
    /// allowed, dragging, the screen locked, or the pointer on another display.
    func pointerOnCanvas(canvasSide: Double, contentScale: Double) -> CGPoint? {
        guard pointerAllowed, !isDragging, !CharacterScreenLock.shared.isLocked, let window else { return nil }
        let mouse = NSEvent.mouseLocation
        guard let screen = window.screen, screen.frame.contains(mouse) else { return nil }
        return Self.canvasPoint(screen: mouse, windowFrame: window.frame, canvasSide: canvasSide,
                                contentScale: contentScale)
    }

    /// The last click's press on her canvas.
    func clickOnCanvas(canvasSide: Double, contentScale: Double) -> CGPoint? {
        guard let window else { return nil }
        return Self.canvasPoint(screen: clickPoint, windowFrame: window.frame, canvasSide: canvasSide,
                                contentScale: contentScale)
    }

    /// A screen point (y up) on the canvas filling `windowFrame` (y down), with the hover scale around its center undone.
    nonisolated static func canvasPoint(screen point: NSPoint, windowFrame: NSRect, canvasSide: Double,
                                        contentScale: Double) -> CGPoint {
        let side = windowFrame.width > 0 ? Double(windowFrame.width) : canvasSide
        let k = canvasSide / side
        let x = Double(point.x - windowFrame.minX) * k, y = Double(windowFrame.maxY - point.y) * k
        let c = canvasSide / 2, s = max(contentScale, 0.01)
        return CGPoint(x: c + (x - c) / s, y: c + (y - c) / s)
    }
}

/// Whether the screen is locked (the `com.apple.screenIsLocked` notifications): the pointer is not read meanwhile.
@MainActor
final class CharacterScreenLock {
    static let shared = CharacterScreenLock()
    private(set) var isLocked = false
    private var observers: [NSObjectProtocol] = []

    private init() {
        let center = DistributedNotificationCenter.default()
        observers.append(center.addObserver(forName: .init("com.apple.screenIsLocked"), object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { CharacterScreenLock.shared.isLocked = true }
        })
        observers.append(center.addObserver(forName: .init("com.apple.screenIsUnlocked"), object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { CharacterScreenLock.shared.isLocked = false }
        })
    }

    /// Starts observing (once, at launch).
    static func start() { _ = shared }
}
