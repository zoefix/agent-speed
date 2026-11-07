import AgentSpeedCore
import Foundation

/// The status sentences and dots of the 區域網路 segment (DESIGN 14).
@MainActor
enum LanStatusText {
    /// The server's line, or nil while it is off.
    static func server(_ status: LanServer.Status, network: Bool, port: Int, l10n: Localizer) -> (String, RemoteDotKind) {
        guard network else { return (l10n("lan.server.status.networkOff"), .idle) }
        switch status.state {
        case .listening where status.devices > 0:
            let key = status.devices == 1 ? "lan.server.status.connected.one" : "lan.server.status.connected"
            return (l10n(key, status.devices), .connected)
        case .listening, .stopped:
            return (l10n("lan.server.status.listening"), .progress)
        case .portInUse:
            return (l10n("lan.server.status.portInUse", port), .retrying)
        case .failed:
            return (l10n("lan.server.status.failed"), .attention)
        }
    }

    static func dot(_ state: LanTargetState) -> RemoteDotKind {
        switch state {
        case .ok: .connected
        case .waiting: .progress
        case .unreachable, .httpStatus, .failed: .retrying
        case .notFound, .notLocal: .attention
        case .off, .networkOff: .idle
        }
    }

    /// A target's line at `now` (the time since the last send and the retry countdown move with it).
    static func target(_ status: LanTargetStatus, now: Date, l10n: Localizer) -> String {
        switch status.state {
        case .ok:
            return l10n("lan.target.status.ok", ago(status.lastSuccess ?? now, now: now, l10n: l10n))
        case .waiting:
            return l10n("lan.target.status.waiting")
        case .notFound:
            return l10n("lan.target.status.notFound")
        case .notLocal:
            return l10n("lan.target.status.notLocal")
        case .unreachable:
            let seconds = max(1, Int((status.retryAt ?? now).timeIntervalSince(now).rounded(.up)))
            return l10n("lan.target.status.unreachable", seconds)
        case .httpStatus:
            return l10n("lan.target.status.httpStatus", status.httpStatus ?? 0)
        case .failed:
            return l10n("lan.target.status.failed")
        case .networkOff:
            return l10n("lan.target.status.networkOff")
        case .off:
            return l10n("lan.target.status.off")
        }
    }

    /// `剛剛`, `12 秒前`, `3 分鐘前`.
    static func ago(_ date: Date, now: Date, l10n: Localizer) -> String {
        let seconds = max(0, Int(now.timeIntervalSince(date)))
        if seconds < 5 { return l10n("time.justNow") }
        if seconds < 60 { return l10n("time.secondsAgo", seconds) }
        return l10n("time.minutesAgo", seconds / 60)
    }

    /// The sheet's field error for an address problem (DESIGN 14).
    static func addressProblem(_ problem: LanAddressProblem, l10n: Localizer) -> String {
        switch problem {
        case .port: l10n("lan.target.address.port")
        case .https: l10n("lan.target.address.https")
        default: l10n("lan.target.address.invalid")
        }
    }

    /// A UDP host that is the limited broadcast or an IPv4 address ending in `.255`.
    static func isBroadcast(_ address: LanTargetAddress, kind: LanDocument.Kind) -> Bool {
        guard kind == .udp, address.isLiteral, let octets = LanAddress.ipv4(address.host) else { return false }
        return octets.last == 255
    }
}
