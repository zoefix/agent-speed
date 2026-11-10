import Foundation

/// What a source's status line says (DESIGN 5, *額度, phase 2*; REMOTE 11.1 `status`).
public enum QuotaRouteStatus: Equatable, Hashable, Sendable {
    /// The last request (or read) succeeded at this time.
    case ok(Int64)
    case noCredential
    /// No browser on this computer has the site's session.
    case noBrowser
    /// The token's own expiry has passed: the agent has to renew it.
    case expired
    /// The vendor answered 401 or 403; nothing is asked until the credential changes.
    case denied(host: String)
    case failed
    case networkOff
    /// Nothing asks yet: the agent is not running and the dashboard is closed.
    case notRunning
    /// A remote host has no CA certificates (REMOTE 11.4).
    case noCertificates

    public var isProblem: Bool {
        switch self {
        case .ok, .notRunning, .networkOff: false
        default: true
        }
    }
}

/// When a vendor request may run (QUOTAS 9.1, NETWORK 9): at most every 5 minutes, 15 minutes after a failure, never
/// again after 401/403 until the credential changes; a new credential may ask at once.
public struct QuotaCadence: Equatable, Sendable {
    public static let intervalMs: Int64 = 5 * 60_000
    public static let failureWaitMs: Int64 = 15 * 60_000

    public enum Outcome: Equatable, Sendable {
        case success
        case failure
        case denied
    }

    private(set) var lastRequestMs: Int64?
    private(set) var lastOutcome: Outcome?
    private(set) var credential: QuotaFingerprint?

    public init() {}

    /// The credential was refused and has not changed since.
    public func isDenied(_ fingerprint: QuotaFingerprint) -> Bool {
        lastOutcome == .denied && credential == fingerprint
    }

    public func isDue(nowMs: Int64, credential fingerprint: QuotaFingerprint) -> Bool {
        guard credential == fingerprint, let last = lastRequestMs else { return true }
        switch lastOutcome {
        case .denied: return false
        case .failure: return nowMs - last >= Self.failureWaitMs
        case .success, nil: return nowMs - last >= Self.intervalMs
        }
    }

    public mutating func record(_ outcome: Outcome, nowMs: Int64, credential fingerprint: QuotaFingerprint) {
        lastRequestMs = nowMs
        lastOutcome = outcome
        credential = fingerprint
    }
}
