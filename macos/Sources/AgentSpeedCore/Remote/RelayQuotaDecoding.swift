import Foundation

/// The relay's quota lines (REMOTE 11.1), validated by REMOTE 5.6: at most 16 windows of the QUOTAS kinds, names of
/// at most 64 characters of display text, minutes 1–527 040 or null, percents finite 0–1000, times within 2000–2100.
/// A source a host cannot read is ignored.
extension RelayLineDecoder {
    static let quotaStatuses: [String: QuotaRouteStatus] = [
        "noCredential": .noCredential, "expired": .expired, "failed": .failed, "networkOff": .networkOff,
        "noCertificates": .noCertificates, "notRunning": .notRunning,
    ]

    /// The host a source's refusal names (NETWORK 9); the line itself names none.
    static func vendorHost(_ source: String) -> String {
        switch source {
        case "grokBuild": QuotaHosts.grok
        case "kimi": QuotaHosts.kimi
        case "gemini": QuotaHosts.gemini
        case "droid": QuotaHosts.factory
        default: QuotaHosts.loopback
        }
    }

    func quota(_ f: RemoteFields) throws(RemoteLineError) -> RelayMessage? {
        let q = try f.object("q")
        let key = try q.string("source", maxBytes: 64)
        let observed = try q.time("observedMs")
        let items = try q.array("windows", limit: RemoteQuotaReport.maxWindows)
        var windows: [QuotaWindow] = []
        for (index, item) in items.enumerated() {
            windows.append(try quotaWindow(try RemoteFields(item, path: "q.windows.\(index)")))
        }
        let running = try q.bool("running")
        let signedIn = try q.bool("signedIn")
        let statusText = try q.string("status", maxBytes: 32)
        let status: QuotaRouteStatus
        switch statusText {
        case "ok": status = .ok(observed)
        case "denied": status = .denied(host: Self.vendorHost(key))
        default:
            guard let known = Self.quotaStatuses[statusText] else { throw RemoteLineError(.outOfRange, "q.status") }
            status = known
        }
        guard let source = SourceKind(rawValue: key), RemoteQuotaConfig.hostSources.contains(source) else { return nil }
        return .quota(RemoteQuotaReport(hostId: hostId, snapshot: QuotaSnapshot(source: source, observedMs: observed, windows: windows),
                                        running: running, signedIn: signedIn, status: status))
    }

    private func quotaWindow(_ w: RemoteFields) throws(RemoteLineError) -> QuotaWindow {
        let kindText = try w.string("kind", maxBytes: 16)
        guard let kind = QuotaWindowKind(rawValue: kindText) else { throw RemoteLineError(.outOfRange, "kind") }
        let name = try w.optionalString("name", maxBytes: RemoteQuotaReport.maxNameCharacters * 4, .model)
        if let name, name.count > RemoteQuotaReport.maxNameCharacters { throw RemoteLineError(.tooLong, "name") }
        let minutes = try w.optionalInteger("minutes")
        if let minutes, !(1...Int64(RemoteQuotaReport.maxMinutes)).contains(minutes) { throw RemoteLineError(.outOfRange, "minutes") }
        if kind == .other, minutes == nil { throw RemoteLineError(.missing, "minutes") }
        let used = try w.number("usedPercent")
        guard (0...RemoteQuotaReport.maxUsedPercent).contains(used) else { throw RemoteLineError(.outOfRange, "usedPercent") }
        let resets = try w.optionalTime("resetsAtMs")
        return QuotaWindow(kind: kind, name: name.flatMap { $0.isEmpty ? nil : $0 }, minutes: minutes.map { Int($0) },
                           usedPercent: used, resetsAtMs: resets)
    }

    func quotaRemoved(_ f: RemoteFields) throws(RemoteLineError) -> RelayMessage? {
        let key = try f.string("source", maxBytes: 64)
        guard let source = SourceKind(rawValue: key), RemoteQuotaConfig.hostSources.contains(source) else { return nil }
        return .quotaRemoved(hostId: hostId, source: source)
    }

    func quotaBridge(_ f: RemoteFields) throws(RemoteLineError) -> RelayMessage {
        let text = try f.string("state", maxBytes: 16)
        guard let state = RemoteQuotaBridge.State(rawValue: text) else { throw RemoteLineError(.outOfRange, "state") }
        let path = try f.string("settingsPath", maxBytes: RemoteLimits.cwdBytes, .model)
        return .quotaBridge(RemoteQuotaBridge(hostId: hostId, state: state, settingsPath: path))
    }
}
