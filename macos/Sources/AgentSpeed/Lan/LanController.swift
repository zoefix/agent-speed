import AgentSpeedCore
import Darwin
import Foundation
import Network
import Observation

/// Settings → API's two segments (DESIGN 14).
enum ApiSegment: Hashable {
    case report, lan
}

/// The local network feed (LAN, DESIGN 14): builds a frame at every engine tick while the server or a target is on,
/// keeps the server and the targets in step with the settings and the network master switch, and holds their status
/// for the 區域網路 segment.
@MainActor
@Observable
final class LanController {
    private(set) var serverStatus = LanServer.Status.stopped
    private(set) var targetStatuses: [String: LanTargetStatus] = [:]
    /// This Mac's local IPv4 addresses (DESIGN 14, Address), at most three.
    private(set) var addresses: [String] = []
    /// The API pane's segment, kept while the app runs (DESIGN 14).
    var segment = ApiSegment.report

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let store: SpeedStore
    @ObservationIgnored private let server = LanServer()
    @ObservationIgnored private let targets = LanTargets()
    @ObservationIgnored private let run = LanIdentity.newRun()
    @ObservationIgnored private var history = LanHistory()
    @ObservationIgnored private var lastFrame: ReportJSON?
    @ObservationIgnored private var seq: Int64 = 0
    @ObservationIgnored private var liveToken: UUID?
    @ObservationIgnored private var loop: ObservationLoop?
    @ObservationIgnored private var pathMonitor: NWPathMonitor?
    @ObservationIgnored private var active = false

    init(settings: AppSettings, store: SpeedStore) {
        self.settings = settings
        self.store = store
    }

    /// The feed runs while the master switch is on and the server or a target is (LAN 3.3).
    var isFeeding: Bool { settings.networkEnabled && settings.lan.wantsFeed }

    func activate() {
        guard !active else { return }
        active = true
        server.onStatus = { [weak self] status in
            Task { @MainActor in self?.serverStatus = status }
        }
        targets.onStatus = { [weak self] id, status in
            Task { @MainActor in self?.receive(status, for: id) }
        }
        liveToken = store.addLiveListener { [weak self] snapshot in self?.tick(snapshot) }
        loop = ObservationLoop { [weak self] in self?.reconcile() }
        startAddressMonitor()
    }

    func shutdown() {
        guard active else { return }
        active = false
        loop?.cancel()
        loop = nil
        if let liveToken { store.removeLiveListener(liveToken) }
        liveToken = nil
        pathMonitor?.cancel()
        pathMonitor = nil
        server.configure(nil)
        targets.configure([])
    }

    // MARK: Settings

    func setServerEnabled(_ on: Bool) {
        var document = settings.lan
        if on { document.enableServer() } else { document.server.enabled = false }
        settings.lan = document
    }

    func regenerateKey() {
        settings.lan.server.key = LanDocument.newKey()
    }

    /// False (and nothing changes) outside 1024–65535.
    @discardableResult
    func setPort(_ port: Int) -> Bool {
        guard LanDocument.ports.contains(port) else { return false }
        settings.lan.server.port = port
        return true
    }

    /// Adds the target or replaces the one with its id; false when it is invalid or the list is full.
    @discardableResult
    func save(_ target: LanDocument.Target) -> Bool {
        var document = settings.lan
        document.ensureInstance()
        let saved = document.targets.contains(where: { $0.id == target.id }) ? document.update(target) : document.add(target)
        guard saved else { return false }
        targetStatuses[target.id] = nil
        settings.lan = document
        return true
    }

    func remove(_ id: String) {
        settings.lan.remove(id)
        targetStatuses[id] = nil
    }

    func setEnabled(_ enabled: Bool, target id: String) {
        guard var target = settings.lan.targets.first(where: { $0.id == id }) else { return }
        target.enabled = enabled
        settings.lan.update(target)
    }

    /// The row status: the master switch and the target's own switch come first (LAN 5.5).
    func status(of target: LanDocument.Target) -> LanTargetStatus {
        if !settings.networkEnabled { return LanTargetStatus(state: .networkOff) }
        if !target.enabled { return LanTargetStatus(state: .off) }
        return targetStatuses[target.id] ?? LanTargetStatus(state: .waiting)
    }

    /// The sheet's Send Test: the latest frame (or one built now) to the address in the fields.
    func test(_ target: LanDocument.Target) async -> LanTargetStatus {
        let frame = lastFrame ?? buildFrame(store.liveSnapshot, nowMs: LanClock.nowMs(), record: false)
        return await withCheckedContinuation { continuation in
            targets.test(target, frame: frame) { continuation.resume(returning: $0) }
        }
    }

    // MARK: Feed

    private func reconcile() {
        let document = settings.lan
        let network = settings.networkEnabled
        if network, document.server.enabled, let key = document.server.key {
            server.configure(.init(port: document.server.port, key: key, serviceName: computerName,
                                   instance: document.instance ?? ""))
        } else {
            server.configure(nil)
        }
        targets.configure(network ? document.targets : [])
        let ids = Set(document.targets.map(\.id))
        for id in targetStatuses.keys where !ids.contains(id) { targetStatuses[id] = nil }
        if network, document.wantsFeed, document.instance == nil {
            settings.lan.ensureInstance()
        }
        if !(network && document.wantsFeed) { stopFeed() }
    }

    private func tick(_ snapshot: SpeedSnapshot) {
        guard isFeeding, snapshot.state == .running else { return }
        let frame = buildFrame(snapshot, nowMs: LanClock.nowMs(), record: true)
        let changed = LanFrame.changed(lastFrame, frame)
        lastFrame = frame
        server.publish(frame: frame.compactData, changed: changed)
        targets.publish(frame: frame, changed: changed)
    }

    /// `record`: the tick's frame (history and `seq` advance); otherwise a one-off for a test.
    private func buildFrame(_ snapshot: SpeedSnapshot, nowMs: Int64, record: Bool) -> ReportJSON {
        let live = LanLiveValues(sample: ReportSample(snapshot: snapshot, traffic: .off))
        let samples: [Double]
        if record {
            samples = history.tick(nowMs: nowMs, outputTokS: live.overall.outputTokS)
            seq += 1
        } else {
            samples = history.samples.isEmpty ? [LanFrame.tokS(live.overall.outputTokS)] : history.samples
        }
        let identity = LanIdentity(instance: settings.lan.instance ?? LanDocument.newId(), name: computerName, run: run)
        return LanFrame.build(identity: identity, seq: max(seq, 1), atMs: Int64(Date().timeIntervalSince1970 * 1000),
                              live: live, history: samples)
    }

    private func stopFeed() {
        guard lastFrame != nil || !history.samples.isEmpty else { return }
        history = LanHistory()
        lastFrame = nil
        server.clearFrame()
    }

    private var computerName: String { LanFrame.computerName(ReportComputerName.current) }

    private func receive(_ status: LanTargetStatus, for id: String) {
        guard settings.lan.targets.contains(where: { $0.id == id }) else { return }
        targetStatuses[id] = status
    }

    // MARK: Previews

    /// Statuses and addresses for `--render-previews` (DESIGN 14 in every state).
    func preview(server: LanServer.Status, targets: [String: LanTargetStatus], addresses: [String]) {
        serverStatus = server
        targetStatuses = targets
        self.addresses = addresses
    }

    // MARK: Addresses

    private func startAddressMonitor() {
        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] _ in
            Task { @MainActor in self?.addresses = Self.localAddresses() }
        }
        monitor.start(queue: DispatchQueue(label: "com.agentspeed.lan.path"))
        pathMonitor = monitor
        addresses = Self.localAddresses()
    }

    /// IPv4 addresses of interfaces that are up and not loopback, local by LAN 2.3, in interface order.
    nonisolated static func localAddresses() -> [String] {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return [] }
        defer { freeifaddrs(first) }
        var result: [String] = []
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = cursor {
            defer { cursor = entry.pointee.ifa_next }
            let flags = Int32(entry.pointee.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_RUNNING != 0, flags & IFF_LOOPBACK == 0,
                  let address = entry.pointee.ifa_addr, address.pointee.sa_family == sa_family_t(AF_INET) else { continue }
            let octets = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                withUnsafeBytes(of: $0.pointee.sin_addr) { Array($0) }
            }
            guard LanAddress.isLocal(ipv4: octets) else { continue }
            let text = octets.map(String.init).joined(separator: ".")
            if !result.contains(text) { result.append(text) }
        }
        return Array(result.prefix(3))
    }
}
