import Darwin
import Foundation

/// agy's own usage report (QUOTAS 9.4 *CLI usage report*), for when no Antigravity server answers.
public protocol AntigravityReporting: Sendable {
    /// Whether an agy binary exists: without one no report runs.
    func available() -> Bool
    /// Runs the report: its windows and when it ended, or nil for a failure.
    func report() -> (windows: [QuotaWindow], observedMs: Int64)?
}

/// Runs `agy -p /usage --output-format json --print-timeout 90s` once agy's version (asked once per binary) is at
/// least 1.1.11, in a private folder with stub `open`/`xdg-open` scripts first on `PATH` and `BROWSER` naming one, so
/// that a signed-out agy cannot open a browser to sign in. agy's sign-in is never read. One report runs at a time.
public final class AgyUsageReport: AntigravityReporting, @unchecked Sendable {
    static let versionTimeoutMs: Int64 = 3_000
    static let versionOutputCap = 4 << 10
    static let reportTimeoutMs: Int64 = 100_000
    static let reportOutputCap = 1 << 20
    static let reportArguments = ["-p", "/usage", "--output-format", "json", "--print-timeout", "90s"]

    /// What identifies a binary for the version kept (QUOTAS 9.4 *Version*).
    struct Identity: Equatable {
        var path: String
        var size: Int64
        var modifiedNs: Int64
        var file: FileIdentity
    }

    private let home: URL
    private let variables: [String: String]
    private let now: @Sendable () -> Int64
    private let run: @Sendable (ChildProcess.Command) -> ChildProcess.Outcome
    private let lock = NSLock()
    private var version: (identity: Identity, version: [Int])?
    private var running = false

    public convenience init(home: URL, variables: [String: String],
                            now: @escaping @Sendable () -> Int64 = { Int64((Date().timeIntervalSince1970 * 1000).rounded(.down)) }) {
        self.init(home: home, variables: variables, now: now, run: ChildProcess.run)
    }

    init(home: URL, variables: [String: String], now: @escaping @Sendable () -> Int64,
         run: @escaping @Sendable (ChildProcess.Command) -> ChildProcess.Outcome) {
        self.home = home
        self.variables = variables
        self.now = now
        self.run = run
    }

    public func available() -> Bool { binary() != nil }

    /// `~/.local/bin/agy`, else the first `agy` on the app's `PATH`: a regular executable file.
    func binary() -> String? {
        let installed = home.appendingPathComponent(".local/bin/agy").path
        let candidates = [installed] + (variables["PATH"] ?? "").split(separator: ":").map { "\($0)/agy" }
        return candidates.first { path in
            path.hasPrefix("/") && FileStat.stat(path)?.isRegularFile == true && access(path, X_OK) == 0
        }
    }

    public func report() -> (windows: [QuotaWindow], observedMs: Int64)? {
        guard begin() else { return nil }
        defer { end() }
        guard let binary = binary(), let folder = Self.sandbox() else { return nil }
        defer { try? FileManager.default.removeItem(atPath: folder) }
        let environment = Self.environment(variables, folder: folder)
        guard let version = version(of: binary, environment: environment, folder: folder),
              AntigravitySummary.agySupported(version) else { return nil }
        let outcome = run(ChildProcess.Command(executable: binary, arguments: Self.reportArguments, environment: environment,
                                               directory: folder, timeoutMs: Self.reportTimeoutMs,
                                               outputCap: Self.reportOutputCap))
        let ended = now()
        guard case .finished(let output) = outcome, let windows = AntigravitySummary.cliWindows(output) else { return nil }
        return (windows, ended)
    }

    /// The version kept for this binary, else `agy --version`; an output that is no version is asked again next time.
    func version(of binary: String, environment: [String: String], folder: String) -> [Int]? {
        let identity = Self.identity(binary)
        if let identity, let kept = lock.withLock({ version }), kept.identity == identity { return kept.version }
        let outcome = run(ChildProcess.Command(executable: binary, arguments: ["--version"], environment: environment,
                                               directory: folder, timeoutMs: Self.versionTimeoutMs,
                                               outputCap: Self.versionOutputCap))
        guard case .finished(let output) = outcome,
              let parsed = AntigravitySummary.agyVersion(String(decoding: output, as: UTF8.self)) else { return nil }
        if let identity { lock.withLock { version = (identity, parsed) } }
        return parsed
    }

    static func identity(_ binary: String) -> Identity? {
        guard let resolved = realpath(binary, nil) else { return nil }
        defer { free(resolved) }
        let path = String(cString: resolved)
        guard let stat = FileStat.stat(path) else { return nil }
        return Identity(path: path, size: stat.size, modifiedNs: stat.modifiedNs, file: stat.identity)
    }

    /// A new private folder (0700) holding the stub `open` and `xdg-open` scripts (0700) that exit 1.
    static func sandbox() -> String? {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("agent-speed-agy-\(UUID().uuidString)").path
        guard mkdir(folder, 0o700) == 0 else { return nil }
        for name in ["open", "xdg-open"] {
            guard FileManager.default.createFile(atPath: "\(folder)/\(name)", contents: Data("#!/bin/sh\nexit 1\n".utf8),
                                                 attributes: [.posixPermissions: 0o700]) else {
                try? FileManager.default.removeItem(atPath: folder)
                return nil
            }
        }
        return folder
    }

    /// The app's environment, agy's updater off, the stubs first on `PATH` and `BROWSER` naming `open`.
    static func environment(_ variables: [String: String], folder: String) -> [String: String] {
        var environment = variables
        environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true"
        environment["PATH"] = folder + ":" + (variables["PATH"].flatMap { $0.isEmpty ? nil : $0 } ?? "/usr/bin:/bin")
        environment["BROWSER"] = "\(folder)/open"
        return environment
    }

    private func begin() -> Bool {
        lock.withLock {
            guard !running else { return false }
            running = true
            return true
        }
    }

    private func end() {
        lock.withLock { running = false }
    }
}
