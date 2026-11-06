import Foundation

/// When a failed host tries again (REMOTE 4.4): 5, 10, 20, 40, 60 s, then every 60 s; reset after a connection that
/// stayed up for 60 s. Errors that need the user retry every 5 minutes without advancing the back-off.
struct RemoteBackoff: Equatable, Sendable {
    static let delays: [TimeInterval] = [5, 10, 20, 40, 60]
    static let userRetryDelay: TimeInterval = 300
    static let resetAfter: TimeInterval = 60

    private(set) var failures = 0

    /// The delay before the next attempt after a connection ended with `status`; `upSince` is when it received
    /// `hello` (nil when it never did).
    mutating func delay(after status: RemoteStatus, upSince: Date?, now: Date) -> TimeInterval {
        if status.needsUser { return Self.userRetryDelay }
        if let upSince, now.timeIntervalSince(upSince) >= Self.resetAfter { failures = 0 }
        let delay = Self.delays[min(failures, Self.delays.count - 1)]
        failures += 1
        return delay
    }

    mutating func reset() {
        failures = 0
    }
}
