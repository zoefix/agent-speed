import Foundation

/// A cancellable one-shot timer.
public protocol RemoteScheduledWork: Sendable {
    func cancel()
}

/// The wall clock and timers of the connections; tests replace it with a clock they advance by hand.
public protocol RemoteScheduling: Sendable {
    var now: Date { get }
    /// Runs `work` once on `queue` at `deadline` (wall clock, so a deadline that passed during sleep fires on wake).
    func schedule(at deadline: Date, on queue: DispatchQueue, _ work: @escaping @Sendable () -> Void) -> any RemoteScheduledWork
}

public struct SystemRemoteScheduling: RemoteScheduling {
    public init() {}

    public var now: Date { Date() }

    public func schedule(at deadline: Date, on queue: DispatchQueue, _ work: @escaping @Sendable () -> Void)
        -> any RemoteScheduledWork {
        WallTimer(after: max(0, deadline.timeIntervalSinceNow), queue: queue, work)
    }
}

/// `@unchecked Sendable`: a dispatch source may be cancelled from any thread.
private final class WallTimer: RemoteScheduledWork, @unchecked Sendable {
    private let source: DispatchSourceTimer

    init(after seconds: TimeInterval, queue: DispatchQueue, _ work: @escaping @Sendable () -> Void) {
        source = DispatchSource.makeTimerSource(queue: queue)
        source.schedule(wallDeadline: .now() + seconds, leeway: .milliseconds(100))
        source.setEventHandler(handler: work)
        source.resume()
    }

    deinit {
        source.cancel()
    }

    func cancel() {
        source.cancel()
    }
}
