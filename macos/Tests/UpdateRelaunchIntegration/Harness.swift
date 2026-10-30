import AgentSpeedCore
import AppKit
import Darwin
import Foundation

final class EventLog: @unchecked Sendable {
    private let lock = NSLock()
    private let handle: FileHandle
    private let started = Date()

    init(path: String) throws {
        FileManager.default.createFile(atPath: path, contents: nil, attributes: [.posixPermissions: 0o600])
        handle = try FileHandle(forWritingTo: URL(fileURLWithPath: path))
    }

    func event(_ name: String, fields: [String: String] = [:]) {
        lock.lock()
        defer { lock.unlock() }
        let object: [String: Any] = [
            "event": name, "pid": getpid(), "elapsed": Date().timeIntervalSince(started), "fields": fields
        ]
        if let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]) {
            try? handle.write(contentsOf: data + Data([10]))
            try? handle.synchronize()
        }
    }
}

@MainActor
final class Delegate: NSObject, NSApplicationDelegate {
    let root: URL
    let version: String
    let defaults: UserDefaults
    let log: EventLog
    var controller: UpdateController?
    var progress: Timer?
    var lastState = ""

    init(root: URL, version: String, defaults: UserDefaults, log: EventLog) {
        self.root = root
        self.version = version
        self.defaults = defaults
        self.log = log
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let log = log
        log.event("launched", fields: ["version": version, "bundle": Bundle.main.bundleIdentifier!])
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 10) {
            log.event("watchdog_exit")
            Darwin._exit(124)
        }
        let installedBefore = defaults.string(forKey: "update.installedVersion") ?? "nil"
        let previousBefore = defaults.string(forKey: "update.previousVersion") ?? "nil"
        let updates = UpdateController(configuration: .current(), store: UpdateStore(defaults: defaults))
        controller = updates
        if version == "1.1.0" {
            let result: [String: Any] = [
                "pid": getpid(), "version": version,
                "installedBefore": installedBefore, "previousBefore": previousBefore,
                "installedAfter": defaults.string(forKey: "update.installedVersion") ?? "nil",
                "previousAfter": defaults.string(forKey: "update.previousVersion") ?? "nil",
                "justUpdatedTo": updates.justUpdatedTo ?? "nil"
            ]
            try! JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
                .write(to: root.appendingPathComponent("new-launch.json"))
            log.event("new_marker_written", fields: [
                "installedBefore": installedBefore, "previousBefore": previousBefore,
                "justUpdatedTo": updates.justUpdatedTo ?? "nil"
            ])
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { NSApp.terminate(nil) }
            return
        }
        try! String(getpid()).write(to: root.appendingPathComponent("old-pid.txt"), atomically: true, encoding: .utf8)
        updates.onReadyToRelaunch = { [self] in
            self.log.event("ready_to_relaunch", fields: [
                "state": String(describing: updates.installation),
                "installed": self.defaults.string(forKey: "update.installedVersion") ?? "nil",
                "previous": self.defaults.string(forKey: "update.previousVersion") ?? "nil"
            ])
            NSApp.terminate(nil)
        }
        progress = Timer(timeInterval: 0.02, repeats: true) { [self] _ in
            MainActor.assumeIsolated {
                let state = String(describing: updates.installation)
                if state != self.lastState {
                    self.lastState = state
                    self.log.event("installation_state", fields: ["state": state])
                }
                if case .failed(let error) = updates.installation {
                    self.log.event("install_failed", fields: ["error": String(describing: error)])
                    Darwin.exit(31)
                }
            }
        }
        RunLoop.main.add(progress!, forMode: .common)
        Task { @MainActor in
            await updates.check(manual: true)
            guard updates.availableRelease?.version.description == "1.1.0" else {
                self.log.event("no_offer", fields: ["status": String(describing: updates.status)])
                Darwin.exit(32)
            }
            self.log.event("release_offered")
            updates.install()
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        log.event("should_terminate")
        if version == "1.1.0" { return .terminateNow }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [self] in
            self.log.event("leaving_reply")
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        log.event("return_terminate_later")
        return .terminateLater
    }

    func applicationWillTerminate(_ notification: Notification) {
        log.event("will_terminate")
    }
}

@main
struct Main {
    @MainActor static func main() throws {
        let bundle = Bundle.main
        let root = URL(fileURLWithPath: bundle.object(forInfoDictionaryKey: "ASOutputRoot") as! String, isDirectory: true)
        let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as! String
        let suite = bundle.object(forInfoDictionaryKey: "ASDefaultsSuite") as! String
        let defaults = UserDefaults(suiteName: suite)!
        let eventFile = version == "1.0.0" ? "old-events.jsonl" : "new-events.jsonl"
        let log = try EventLog(path: root.appendingPathComponent(eventFile).path)
        let delegate = Delegate(root: root, version: version, defaults: defaults, log: log)
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        app.delegate = delegate
        withExtendedLifetime(delegate) { app.run() }
        log.event("application_run_returned")
    }
}
