import AgentSpeedCore
import Foundation

/// The service presets of DESIGN 11: Codex Radar, then a custom URL.
enum ReportPreset: String, Codable, CaseIterable, Identifiable, Sendable {
    case codexRadar, custom

    var id: String { rawValue }

    static let codexRadarName = "Codex Radar"

    /// Debug runs may point the preset at a local reference server (`AGENT_SPEED_REPORT_PRESET_URL`), so the
    /// invitation's path can be tested end to end without contacting the real service.
    static var codexRadarURL: String {
        #if DEBUG
        if let url = presetOverride { return url }
        #endif
        return "https://deng.codexradar.com"
    }

    /// The domain the preset covers (REPORTING 3.3): its API may live on any host under it. The invitation names it.
    static var codexRadarDomain: String? {
        #if DEBUG
        if presetOverride != nil { return nil }
        #endif
        return "codexradar.com"
    }

    #if DEBUG
    private static var presetOverride: String? {
        guard let url = ProcessInfo.processInfo.environment["AGENT_SPEED_REPORT_PRESET_URL"], !url.isEmpty else { return nil }
        return url
    }
    #endif
}

/// How a service's reports travel (DESIGN 11, Transport): WebSocket by default, HTTP by the user's choice. Reports go
/// over HTTP meanwhile when the service lacks the WebSocket capability (METRICS 11.9, effective transport).
enum ReportTransportKind: String, Codable, CaseIterable, Identifiable, Sendable {
    case http, websocket

    var id: String { rawValue }
}

/// One service this device registered with (DESIGN 11, Storage): what the user confirmed, the device id, the
/// registered name and the key's age. Keys themselves live in `ReportKeyStore`, never here.
struct ReportIdentity: Codable, Equatable, Identifiable, Sendable {
    var service: ReportServiceURL
    var deviceId: String
    /// The registered username; nil until a registration succeeded.
    var username: String?
    var profileURL: URL?
    /// What the consent sheet showed when the user started (REPORTING 3.3).
    var pin: ReportConsentPin?
    /// The last valid discovery document and when it was fetched (REPORTING 3.4).
    var discovery: ReportDiscovery?
    var discoveryFetchedAt: Date?
    var keyCreatedAt: Date
    /// A rotated key waits for the service's answer (REPORTING 4.5).
    var pendingKey = false
    var hasAPIKey = false
    var lastUsedAt: Date

    var id: String { service.origin }
    var isRegistered: Bool { username != nil }
}

/// Live reporting's settings (DESIGN 11, Storage), persisted as one document in the settings store.
struct ReportPreferences: Codable, Equatable, Sendable {
    static let maxIdentities = 8
    static let defaultSections: Set<ReportSection> = [.sources, .models, .cost, .traffic]

    var enabled = false
    var preset: ReportPreset = .codexRadar
    var customURL = ""
    var sections: Set<ReportSection> = defaultSections
    /// The 額度 switch (`report.section.quota`, REPORTING-EXTENSIONS 11.1): on by default, its own key so that earlier
    /// builds' stored sections keep it on.
    var shareQuota = true
    /// `distantFuture` pauses until the user resumes.
    var pausedUntil: Date?
    var identities: [ReportIdentity] = []
    /// The dashboard invitation's answer (`report.invite`); nil until the user answers it.
    var invite: ReportInvite.Answer?
    /// The name for the next registration when the user typed one; nil means the computer name (DESIGN 11, Username).
    var username: String?
    /// The origins whose transport the user set to HTTP, the latest choice last. Every other service uses WebSocket,
    /// also those set up by earlier builds; their list of WebSocket origins (`webSocketServices`) is ignored.
    var httpServices: [String] = []

    static let maxHTTPServices = 16

    init() {}

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        enabled = (try? container.decode(Bool.self, forKey: .enabled)) ?? false
        preset = (try? container.decode(ReportPreset.self, forKey: .preset)) ?? .codexRadar
        customURL = (try? container.decode(String.self, forKey: .customURL)) ?? ""
        // Names this build no longer knows (the retired `machineTraffic`) are ignored, the rest is kept.
        sections = (try? container.decode([String].self, forKey: .sections))
            .map { Set($0.compactMap(ReportSection.init(rawValue:))) } ?? Self.defaultSections
        shareQuota = (try? container.decode(Bool.self, forKey: .shareQuota)) ?? true
        pausedUntil = try? container.decode(Date.self, forKey: .pausedUntil)
        identities = (try? container.decode([ReportIdentity].self, forKey: .identities)) ?? []
        invite = try? container.decode(ReportInvite.Answer.self, forKey: .invite)
        username = try? container.decode(String.self, forKey: .username)
        httpServices = Array(((try? container.decode([String].self, forKey: .httpServices)) ?? [])
            .filter { !$0.isEmpty && $0.count <= ReportServiceURL.maxLength }.suffix(Self.maxHTTPServices))
    }

    /// The user's choice for `service` (DESIGN 11, Transport): WebSocket unless HTTP was chosen.
    func transport(for service: ReportServiceURL?) -> ReportTransportKind {
        guard let service, httpServices.contains(service.origin) else { return .websocket }
        return .http
    }

    mutating func setTransport(_ transport: ReportTransportKind, for origin: String) {
        httpServices.removeAll { $0 == origin }
        if transport == .http { httpServices.append(origin) }
        httpServices = Array(httpServices.suffix(Self.maxHTTPServices))
    }

    /// An identity is registered with some service (the invitation is not offered then).
    var hasRegisteredIdentity: Bool { identities.contains { $0.isRegistered } }

    func identity(for service: ReportServiceURL?) -> ReportIdentity? {
        guard let service else { return nil }
        return identities.first { $0.id == service.origin }
    }

    /// Adds or replaces the identity of its origin; beyond `maxIdentities` the oldest unused one is forgotten
    /// locally first, and its device id is returned so the caller deletes its keys.
    mutating func upsert(_ identity: ReportIdentity) -> [String] {
        if let index = identities.firstIndex(where: { $0.id == identity.id }) {
            identities[index] = identity
            return []
        }
        identities.append(identity)
        var dropped: [String] = []
        while identities.count > Self.maxIdentities,
              let oldest = identities.filter({ $0.id != identity.id }).min(by: { $0.lastUsedAt < $1.lastUsedAt }) {
            identities.removeAll { $0.id == oldest.id }
            dropped.append(oldest.deviceId)
        }
        return dropped
    }

    mutating func remove(_ origin: String) {
        identities.removeAll { $0.id == origin }
    }

    var isPaused: Bool { pausedUntil.map { $0 > Date() } ?? false }

    /// Persisted next to the app's other settings.
    static func load(from defaults: UserDefaults) -> ReportPreferences {
        guard let data = defaults.data(forKey: storageKey),
              let value = try? JSONDecoder().decode(ReportPreferences.self, from: data) else { return ReportPreferences() }
        return value
    }

    func save(to defaults: UserDefaults) {
        guard let data = try? JSONEncoder().encode(self) else { return }
        defaults.set(data, forKey: Self.storageKey)
    }

    static let storageKey = "report"
}
