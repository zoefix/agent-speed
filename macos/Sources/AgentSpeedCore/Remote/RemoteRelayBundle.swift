import Foundation
import os

/// One bundled relay executable with its digest (REMOTE 3.1).
public struct RemoteRelayExecutable: Equatable, Sendable {
    public var url: URL
    public var arch: RemoteArch
    /// Lowercase hex SHA-256 of the file.
    public var sha256: String
    public var size: Int64
    /// The name it is installed under on a host (REMOTE 4.2).
    public var installedName: String
}

/// Finds the relay executables: `Contents/Resources/relay/agent-speed-relay-linux-<arch>`, or the folder named by
/// `AGENT_SPEED_RELAY_DIR` for development builds. Digests are computed from the files (streamed) and cached by path,
/// size and modification date.
///
/// `@unchecked Sendable`: the cache is locked.
public final class RemoteRelayBundle: @unchecked Sendable {
    public static let overrideVariable = "AGENT_SPEED_RELAY_DIR"
    static let logger = Logger(subsystem: "io.github.zoefix.agentspeed", category: "remote")

    public let directory: URL?
    /// The app version (`CFBundleShortVersionString`; `0.0.0` for an unbundled build, as the update checker reads it).
    public let version: String

    private struct Stamp: Equatable, Sendable {
        var size: Int64
        var modified: Date
    }

    private let cache = OSAllocatedUnfairLock<[RemoteArch: (Stamp, RemoteRelayExecutable)]>(initialState: [:])

    public init(directory: URL?, version: String) {
        self.directory = directory
        self.version = version
    }

    /// The app's relay folder and version.
    public static func current(
        bundle: Bundle = .main,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> RemoteRelayBundle {
        let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
        if let override = environment[overrideVariable], !override.isEmpty {
            return RemoteRelayBundle(directory: URL(fileURLWithPath: override, isDirectory: true), version: version)
        }
        return RemoteRelayBundle(directory: bundle.resourceURL?.appendingPathComponent("relay", isDirectory: true),
                                 version: version)
    }

    /// The executable for `arch` with its digest; nil when it is missing or unreadable.
    public func executable(for arch: RemoteArch) -> RemoteRelayExecutable? {
        guard let directory else { return nil }
        let url = directory.appendingPathComponent(RemoteRelayName.bundled(arch))
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              attributes[.type] as? FileAttributeType == .typeRegular,
              let size = (attributes[.size] as? NSNumber)?.int64Value, size > 0 else { return nil }
        let stamp = Stamp(size: size, modified: attributes[.modificationDate] as? Date ?? .distantPast)
        if let (cachedStamp, cached) = cache.withLock({ $0[arch] }), cachedStamp == stamp { return cached }
        guard let digest = try? FileDigest.sha256(of: url) else {
            Self.logger.error("Bundled relay for \(arch.rawValue, privacy: .public) is unreadable")
            return nil
        }
        let executable = RemoteRelayExecutable(
            url: url, arch: arch, sha256: digest, size: size,
            installedName: RemoteRelayName.installed(version: version, arch: arch, sha256: digest)
        )
        cache.withLock { $0[arch] = (stamp, executable) }
        return executable
    }
}
