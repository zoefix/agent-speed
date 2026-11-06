import Foundation

/// A host's connection status (REMOTE 4.5).
public enum RemoteStatus: Equatable, Sendable {
    /// `ssh` running, no `hello` yet (probe and upload included).
    case connecting
    /// Uploading the relay; `fraction` of its bytes written.
    case installing(fraction: Double)
    /// `hello` received, no `ready` yet; the relay's progress.
    case seeding(fraction: Double)
    case connected
    case unreachable
    case authFailed
    case hostKeyUnknown
    /// The system `uname -s -m` reported (printable ASCII, at most 64 characters).
    case unsupportedSystem(system: String)
    case cannotVerify
    case uploadFailed
    case notExecutable
    /// The relay's `hello.protocol`.
    case versionMismatch(relayProtocol: Int64)
    case protocolError
    /// The exit status of `ssh` (for a signal: 128 + its number).
    case exited(status: Int32)
    case sshMissing
    case invalidDestination
    case networkOff
    case disabled

    /// The status code the log keeps (never stderr, REMOTE 4.1).
    public var code: String {
        switch self {
        case .connecting: "connecting"
        case .installing: "installing"
        case .seeding: "seeding"
        case .connected: "connected"
        case .unreachable: "unreachable"
        case .authFailed: "authFailed"
        case .hostKeyUnknown: "hostKeyUnknown"
        case .unsupportedSystem: "unsupportedSystem"
        case .cannotVerify: "cannotVerify"
        case .uploadFailed: "uploadFailed"
        case .notExecutable: "notExecutable"
        case .versionMismatch: "versionMismatch"
        case .protocolError: "protocolError"
        case .exited: "exited"
        case .sshMissing: "sshMissing"
        case .invalidDestination: "invalidDestination"
        case .networkOff: "networkOff"
        case .disabled: "disabled"
        }
    }

    /// `connecting`, `installing` or `seeding`: the UI shows progress.
    public var isInProgress: Bool {
        switch self {
        case .connecting, .installing, .seeding: true
        default: false
        }
    }

    var isSeeding: Bool {
        if case .seeding = self { return true }
        return false
    }

    /// Errors that need the user (REMOTE 4.4): retried every 5 minutes, at once on *Retry* or an edit. An app update
    /// (`versionMismatch`) and an upload that keeps failing need the user too.
    public var needsUser: Bool {
        switch self {
        case .authFailed, .hostKeyUnknown, .unsupportedSystem, .notExecutable, .cannotVerify, .invalidDestination,
             .sshMissing, .uploadFailed, .versionMismatch:
            true
        default:
            false
        }
    }

    /// Failures retried with the normal back-off (REMOTE 4.4).
    public var isRetrying: Bool {
        switch self {
        case .unreachable, .protocolError, .exited: true
        default: false
        }
    }
}

/// What the UI shows for one host: the status plus its timing facts (DESIGN 13 status line and tooltip).
public struct RemoteHostStatus: Equatable, Sendable {
    public var status: RemoteStatus
    /// When the next attempt starts (the countdown of `unreachable`, the 5-minute retry); nil when none is planned.
    public var retryAt: Date?
    /// When the last failure happened.
    public var lastErrorAt: Date?
    /// The estimated clock offset `δ = remote − local` in milliseconds (REMOTE 6); nil before `hello`.
    public var clockOffsetMs: Int64?
    /// The relay's `hello` facts of the current connection.
    public var relayVersion: String?
    public var relayArch: String?
    /// `historyTooLarge` (REMOTE 8.3): the host's last history batch exceeded the bounds and was discarded. Comes
    /// from the history store (`HistoryStore.remoteTooLarge`), not from the connection.
    public var historyTooLarge: Bool

    public init(status: RemoteStatus, retryAt: Date? = nil, lastErrorAt: Date? = nil, clockOffsetMs: Int64? = nil,
                relayVersion: String? = nil, relayArch: String? = nil, historyTooLarge: Bool = false) {
        self.status = status
        self.retryAt = retryAt
        self.lastErrorAt = lastErrorAt
        self.clockOffsetMs = clockOffsetMs
        self.relayVersion = relayVersion
        self.relayArch = relayArch
        self.historyTooLarge = historyTooLarge
    }

    /// `remote.clock.skewed`: `|δ| ≥ 5 s` (REMOTE 6).
    public var isClockSkewed: Bool {
        clockOffsetMs.map { RemoteClockEstimator.isSkewed($0) } ?? false
    }
}

/// Maps how a command ended to a status (REMOTE 4.5). stderr is inspected here and nowhere else; it is never logged.
public enum RemoteStatusClassifier {
    public static let maxStderrBytes = 64 * 1024

    public enum Stage: Sendable {
        case probe, upload, run
    }

    private static let unreachableMarkers = [
        "Could not resolve", "timed out", "Connection refused", "Connection reset", "No route", "Network is unreachable",
        "Connection closed",
    ]

    /// The status of a command that ended with `exitStatus` and wrote `stderr` (already capped).
    public static func classify(exitStatus: Int32, stderr: Data, stage: Stage) -> RemoteStatus {
        let text = String(decoding: stderr.suffix(maxStderrBytes), as: UTF8.self)
        if text.contains("Host key verification failed") { return .hostKeyUnknown }
        if exitStatus == 255 {
            // "ssh: connect to host h port 22: Permission denied" is a local firewall, not a refused login: every connect
            // failure is unreachable.
            if text.contains("connect to host ") { return .unreachable }
            if text.contains("Permission denied") { return .authFailed }
            if unreachableMarkers.contains(where: text.contains) { return .unreachable }
            // LogLevel=ERROR hides "Connection to … closed by remote host"; the relay itself never exits with 255.
            if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return .unreachable }
            return .exited(status: exitStatus)
        }
        switch stage {
        case .run:
            if exitStatus == 126 || text.contains("Permission denied") || text.contains("cannot execute") {
                return .notExecutable
            }
        case .upload:
            if exitStatus == RemoteCommand.noHashToolStatus { return .cannotVerify }
            if exitStatus != 0 { return .uploadFailed }
        case .probe:
            break
        }
        return .exited(status: exitStatus)
    }
}
