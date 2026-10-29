import AppKit

/// One card to show: its content, where, and above which window.
struct PerfCardRequest {
    var model: PerfCardModel
    var anchor: PerfCardAnchor
    weak var window: NSWindow?
}

/// What the presenter drives: the card window, or a stand-in in tests.
@MainActor
protocol PerfCardDisplaying: AnyObject {
    var isShown: Bool { get }
    func show(_ request: PerfCardRequest, key: String, reduceMotion: Bool, reduceTransparency: Bool)
    func update(_ model: PerfCardModel)
    func hide(animated: Bool)
}

extension PerfCardPanel: PerfCardDisplaying {
    func show(_ request: PerfCardRequest, key: String, reduceMotion: Bool, reduceTransparency: Bool) {
        show(request.model, key: key, anchor: request.anchor, above: request.window, reduceMotion: reduceMotion,
             reduceTransparency: reduceTransparency)
    }
}

/// A pending show or hide that can be called off.
@MainActor
protocol PerfCardTimer: AnyObject {
    func cancel()
}

/// Runs an action after a delay on the main actor (a manual clock in tests).
@MainActor
protocol PerfCardScheduling {
    func after(_ seconds: TimeInterval, _ action: @escaping @MainActor () -> Void) -> PerfCardTimer
}

/// Main-queue work items.
struct PerfCardMainScheduler: PerfCardScheduling {
    private final class Item: PerfCardTimer {
        let work: DispatchWorkItem
        init(_ work: DispatchWorkItem) { self.work = work }
        func cancel() { work.cancel() }
    }

    func after(_ seconds: TimeInterval, _ action: @escaping @MainActor () -> Void) -> PerfCardTimer {
        let work = DispatchWorkItem { MainActor.assumeIsolated { action() } }
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: work)
        return Item(work)
    }
}

/// The card's timing (DESIGN 12, *Placement and timing*): it shows after a hover delay (0.45 s on dashboard rows,
/// 0.35 s on boards); while shown, moving to another row switches at once; it hides 0.15 s after the pointer leaves.
/// Space or the row's custom action shows it at once, it then follows the keyboard focus, and Esc hides it.
@MainActor
final class PerfCardPresenter {
    typealias Provider = @MainActor () -> PerfCardRequest?

    static let dashboardDelay: TimeInterval = 0.45
    static let boardDelay: TimeInterval = 0.35
    static let hideDelay: TimeInterval = 0.15

    private let display: PerfCardDisplaying
    private let scheduler: PerfCardScheduling
    private let delay: TimeInterval
    private let accessibility: () -> (reduceMotion: Bool, reduceTransparency: Bool)

    /// The row the card shows, and the one under the pointer.
    private(set) var shownKey: String?
    private var hovered: (key: String, provider: Provider)?
    private var shownProvider: Provider?
    private var pendingShow: PerfCardTimer?
    private var pendingHide: PerfCardTimer?
    /// Shown from the keyboard: it follows the focus and stays until Esc or the pointer takes over.
    private var fromKeyboard = false

    init(display: PerfCardDisplaying, delay: TimeInterval, scheduler: PerfCardScheduling = PerfCardMainScheduler(),
         accessibility: @escaping () -> (reduceMotion: Bool, reduceTransparency: Bool)) {
        self.display = display
        self.delay = delay
        self.scheduler = scheduler
        self.accessibility = accessibility
    }

    var isShown: Bool { shownKey != nil }

    /// The pointer entered a row.
    func enter(_ key: String, provider: @escaping Provider) {
        pendingHide?.cancel()
        pendingHide = nil
        hovered = (key, provider)
        fromKeyboard = false
        if shownKey != nil {
            present(key, provider)
            return
        }
        pendingShow?.cancel()
        pendingShow = scheduler.after(delay) { [weak self] in
            guard let self, let hovered = self.hovered, hovered.key == key else { return }
            self.pendingShow = nil
            self.present(key, hovered.provider)
        }
    }

    /// The pointer left a row.
    func exit(_ key: String) {
        guard hovered?.key == key else { return }
        hovered = nil
        pendingShow?.cancel()
        pendingShow = nil
        guard shownKey != nil, !fromKeyboard else { return }
        pendingHide?.cancel()
        pendingHide = scheduler.after(Self.hideDelay) { [weak self] in
            guard let self, self.hovered == nil else { return }
            self.pendingHide = nil
            self.dismiss()
        }
    }

    /// Space on a focused row, or its custom action `a11y.perf.card`.
    func showNow(_ key: String, provider: @escaping Provider) {
        pendingShow?.cancel()
        pendingShow = nil
        pendingHide?.cancel()
        pendingHide = nil
        fromKeyboard = true
        present(key, provider)
    }

    /// The keyboard focus moved to another row: the card follows while shown from the keyboard.
    func focus(_ key: String, provider: @escaping Provider) {
        guard fromKeyboard, shownKey != nil, shownKey != key else { return }
        present(key, provider)
    }

    /// Esc, or the surface closed.
    func dismiss() {
        pendingShow?.cancel()
        pendingShow = nil
        pendingHide?.cancel()
        pendingHide = nil
        fromKeyboard = false
        shownKey = nil
        shownProvider = nil
        display.hide(animated: true)
    }

    /// The engine tick: the shown card takes its row's new content.
    func refresh() {
        guard shownKey != nil, let request = shownProvider?() else { return }
        display.update(request.model)
    }

    private func present(_ key: String, _ provider: @escaping Provider) {
        guard let request = provider() else {
            if shownKey != nil { dismiss() }
            return
        }
        let options = accessibility()
        shownKey = key
        shownProvider = provider
        display.show(request, key: key, reduceMotion: options.reduceMotion, reduceTransparency: options.reduceTransparency)
    }
}
