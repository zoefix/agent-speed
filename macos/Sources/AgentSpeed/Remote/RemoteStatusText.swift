import AgentSpeedCore
import Foundation

/// The four looks of a host's status dot (DESIGN 13): connected green, a breathing grey dot while connecting,
/// installing or seeding, orange while retrying, red when it needs the user, hollow when off.
enum RemoteDotKind: Equatable, Sendable {
    case connected, progress, retrying, attention, idle

    init(_ status: RemoteStatus) {
        switch status {
        case .connected: self = .connected
        case .connecting, .installing, .seeding: self = .progress
        case .networkOff, .disabled: self = .idle
        default: self = status.needsUser ? .attention : .retrying
        }
    }
}

/// What a host's status says in words (DESIGN 13 status line, REMOTE 4.5): one sentence per status with the one thing
/// the user can do, the retry countdown, the clock note, the tooltip and the dashboard's short word.
@MainActor
enum RemoteStatusText {
    /// The status line. `sessions`: today's main sessions on the host; `destination` for `hostKeyUnknown`.
    static func sentence(_ status: RemoteHostStatus, destination: String, sessions: Int, now: Date,
                         l10n: Localizer) -> String {
        switch status.status {
        case .connecting: return l10n("remote.status.connecting")
        case .installing: return l10n("remote.status.installing")
        case .seeding(let fraction): return l10n("remote.status.seeding", percent(fraction))
        case .connected:
            guard sessions > 0 else { return l10n("remote.status.connected") }
            return l10n(sessions == 1 ? "remote.status.connected.one" : "remote.status.connected.agents", sessions)
        case .unreachable, .protocolError, .exited:
            return retrying(base(status.status, destination: destination, l10n: l10n), retryAt: status.retryAt,
                            now: now, l10n: l10n)
        default:
            return base(status.status, destination: destination, l10n: l10n)
        }
    }

    /// The plain sentence of a status, without its countdown.
    static func base(_ status: RemoteStatus, destination: String, l10n: Localizer) -> String {
        switch status {
        case .connecting: l10n("remote.status.connecting")
        case .installing: l10n("remote.status.installing")
        case .seeding(let fraction): l10n("remote.status.seeding", percent(fraction))
        case .connected: l10n("remote.status.connected")
        case .unreachable: l10n("remote.status.unreachable")
        case .authFailed: l10n("remote.status.authFailed")
        case .hostKeyUnknown: l10n("remote.status.hostKeyUnknown", destination)
        case .unsupportedSystem(let system): l10n("remote.status.unsupportedSystem", system)
        case .cannotVerify: l10n("remote.status.cannotVerify")
        case .uploadFailed: l10n("remote.status.uploadFailed")
        case .notExecutable: l10n("remote.status.notExecutable")
        case .versionMismatch: l10n("remote.status.versionMismatch")
        case .protocolError: l10n("remote.status.protocolError")
        case .exited: l10n("remote.status.exited")
        case .sshMissing: l10n("remote.status.sshMissing")
        case .invalidDestination: l10n("remote.status.invalidDestination")
        case .networkOff: l10n("remote.status.networkOff")
        case .disabled: l10n("remote.status.disabled")
        }
    }

    /// `無法連線，10 秒後重試`; at the deadline (the next attempt is starting) `無法連線，正在重試…`.
    static func retrying(_ base: String, retryAt: Date?, now: Date, l10n: Localizer) -> String {
        guard let retryAt else { return base }
        let seconds = Int(retryAt.timeIntervalSince(now).rounded(.up))
        return seconds >= 1 ? l10n("remote.status.retryIn", base, seconds) : l10n("remote.status.retryingNow", base)
    }

    /// The clock note under a connected host's line when `|δ| ≥ 5 s` (REMOTE 6): seconds below two minutes.
    static func clockNote(_ status: RemoteHostStatus, l10n: Localizer) -> String? {
        guard status.status == .connected, status.isClockSkewed, let offset = status.clockOffsetMs else { return nil }
        let seconds = Int((Double(offset.magnitude) / 1000).rounded())
        guard seconds >= 120 else { return l10n("remote.clock.skewed", seconds) }
        return l10n("remote.clock.skewedMinutes", Int((Double(seconds) / 60).rounded()))
    }

    /// The note under a host's line while its history cannot be synced (`historyTooLarge`, REMOTE 8.3).
    static func historyNote(_ status: RemoteHostStatus, l10n: Localizer) -> String? {
        status.historyTooLarge ? l10n("remote.status.historyTooLarge") : nil
    }

    /// The status's tooltip: the relay's version while connected, else the last error's time and, for `exited`, the
    /// exit status. Nil when there is nothing to add.
    static func tooltip(_ status: RemoteHostStatus, now: Date, l10n: Localizer) -> String? {
        var lines: [String] = []
        switch status.status {
        case .connected, .seeding:
            if let version = status.relayVersion, let arch = status.relayArch {
                lines.append(l10n("remote.status.relay", version, arch))
            }
        case .connecting, .installing, .disabled, .networkOff:
            break
        default:
            if let at = status.lastErrorAt { lines.append(l10n("remote.status.lastError", time(at, now: now, l10n: l10n))) }
            if case .exited(let code) = status.status { lines.append(l10n("remote.status.exitCode", Int(code))) }
        }
        return lines.isEmpty ? nil : lines.joined(separator: "\n")
    }

    /// The dashboard's word for a host that is not connected.
    static func word(_ status: RemoteStatus, l10n: Localizer) -> String {
        switch status {
        case .connected: l10n("remote.status.connected")
        case .disabled: l10n("remote.status.disabled")
        case .networkOff: l10n("remote.word.offline")
        default:
            switch RemoteDotKind(status) {
            case .progress: l10n("remote.word.connecting")
            case .retrying: l10n("remote.word.retrying")
            default: l10n("remote.word.attention")
            }
        }
    }

    /// The field error of a destination or relay folder (REMOTE 2.1).
    static func fieldError(_ issue: RemoteFieldIssue, folder: Bool, l10n: Localizer) -> String {
        switch issue {
        case .empty: l10n("remote.error.empty")
        case .tooLong: l10n("remote.error.tooLong", folder ? RemoteHostValidation.folderLimit : RemoteHostValidation.destinationLimit)
        case .leadingDash: l10n("remote.error.leadingDash")
        case .forbiddenCharacter: l10n(folder ? "remote.error.folderCharacter" : "remote.error.forbiddenCharacter")
        case .notAbsolute: l10n("remote.error.notAbsolute")
        case .unsafeComponent: l10n("remote.error.unsafeComponent")
        }
    }

    private static func percent(_ fraction: Double) -> Int {
        Int((min(max(fraction, 0), 1) * 100).rounded(.down))
    }

    /// `14:32` today, else the short date and time, in the app's language.
    private static func time(_ date: Date, now: Date, l10n: Localizer) -> String {
        let formatter = DateFormatter()
        formatter.locale = l10n.locale
        formatter.timeStyle = .short
        formatter.dateStyle = Calendar.current.isDate(date, inSameDayAs: now) ? .none : .short
        return formatter.string(from: date)
    }
}
