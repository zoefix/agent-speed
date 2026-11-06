import Foundation

/// The system OpenSSH client and its argument vector (REMOTE 4.1). Nothing here ever goes through a local shell.
public enum RemoteSSH {
    public static let executable = "/usr/bin/ssh"

    public static let options: [String] = [
        "-T",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=15",
        "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=3",
        "-o", "Compression=yes",
        "-o", "ControlMaster=no",
        "-o", "ClearAllForwardings=yes",
        "-o", "ForwardAgent=no",
        "-o", "ForwardX11=no",
        "-o", "PermitLocalCommand=no",
        "-o", "RequestTTY=no",
        "-o", "LogLevel=ERROR",
    ]

    /// A development-only hook of debug builds (REMOTE 4.1): an absolute path in this variable adds `-F <path>` to
    /// every invocation, so tests reach a test host without the user's SSH configuration. Nothing in the app sets it.
    public static let configFileVariable = "AGENT_SPEED_SSH_CONFIG"

    /// The arguments after `argv[0]`; nil when the destination is not valid (REMOTE 2.1). `configFile` is the
    /// development hook's file (`developmentConfigFile`).
    public static func arguments(destination: String, remoteCommand: String, configFile: String? = nil) -> [String]? {
        guard RemoteHostValidation.destinationIssue(destination) == nil else { return nil }
        let config = configFile.map { ["-F", $0] } ?? []
        return options + config + ["--", destination, remoteCommand]
    }

    /// The file `AGENT_SPEED_SSH_CONFIG` names in `environment`; always nil in release builds.
    public static func developmentConfigFile(_ environment: [String: String]) -> String? {
        #if DEBUG
        guard let path = environment[configFileVariable], path.hasPrefix("/") else { return nil }
        return path
        #else
        return nil
        #endif
    }

    /// The app's environment plus `LC_ALL=C`, so error messages are the English ones the status reads.
    public static func environment(_ base: [String: String] = ProcessInfo.processInfo.environment) -> [String: String] {
        var environment = base
        environment["LC_ALL"] = "C"
        return environment
    }
}

/// The relay's architectures (REMOTE 1, 4.2).
public enum RemoteArch: String, CaseIterable, Sendable {
    case x86_64
    case aarch64

    /// `uname -m` → architecture: `x86_64` / `amd64` and `aarch64` / `arm64`; nil for anything else.
    public init?(machine: String) {
        switch machine.lowercased() {
        case "x86_64", "amd64": self = .x86_64
        case "aarch64", "arm64": self = .aarch64
        default: return nil
        }
    }
}

/// The file names of REMOTE 3.1 and 4.2.
public enum RemoteRelayName {
    public static let installedPrefix = "agent-speed-relay-"

    /// `relay/agent-speed-relay-linux-<arch>` in the app's resources.
    public static func bundled(_ arch: RemoteArch) -> String {
        "agent-speed-relay-linux-\(arch.rawValue)"
    }

    /// `agent-speed-relay-<version>-<arch>-<hash12>`. Characters of the version outside `[A-Za-z0-9._-]` become `-`, so
    /// the name stays safe to insert into a remote command.
    public static func installed(version: String, arch: RemoteArch, sha256: String) -> String {
        let bytes = version.utf8.prefix(64).map { RemoteHostValidation.isNameByte($0) ? $0 : UInt8(ascii: "-") }
        let cleanVersion = String(decoding: bytes, as: UTF8.self)
        return installedPrefix + (cleanVersion.isEmpty ? "0" : cleanVersion) + "-" + arch.rawValue + "-"
            + String(sha256.lowercased().prefix(12))
    }

    static func isSafe(_ name: String) -> Bool {
        !name.isEmpty && name.utf8.count <= 255 && name.utf8.allSatisfy(RemoteHostValidation.isNameByte)
            && !name.hasPrefix("-") && name != "." && name != ".."
    }
}

/// The three remote commands (REMOTE 4.2, 4.3): `sh -c '<script>' agent-speed <arguments>`. The scripts are fixed;
/// the arguments are the validated folder (unquoted, so the remote shell expands `~`), a safe file name and a hex
/// digest. Every builder returns nil for a value that fails validation.
public enum RemoteCommand {
    /// Exit statuses of the upload script.
    public static let mismatchStatus: Int32 = 3
    public static let writeFailedStatus: Int32 = 4
    public static let noHashToolStatus: Int32 = 5

    public static func probe(folder: String) -> String? {
        guard let folder = checkedFolder(folder) else { return nil }
        return command(RemoteScripts.probe, [folder])
    }

    public static func upload(folder: String, fileName: String, sha256: String) -> String? {
        guard let folder = checkedFolder(folder), RemoteRelayName.isSafe(fileName), isDigest(sha256) else { return nil }
        return command(RemoteScripts.upload, [folder, fileName, sha256.lowercased()])
    }

    public static func run(folder: String, fileName: String) -> String? {
        guard let folder = checkedFolder(folder), RemoteRelayName.isSafe(fileName) else { return nil }
        return command(RemoteScripts.run, [folder, fileName])
    }

    static func command(_ script: String, _ arguments: [String]) -> String {
        precondition(RemoteScripts.isQuotable(script))
        return (["sh", "-c", "'" + script + "'", "agent-speed"] + arguments).joined(separator: " ")
    }

    private static func checkedFolder(_ folder: String) -> String? {
        guard RemoteHostValidation.folderIssue(folder) == nil else { return nil }
        return RemoteHostValidation.normalizedFolder(folder)
    }

    static func isDigest(_ text: String) -> Bool {
        text.utf8.count == 64
            && text.utf8.allSatisfy { (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }
    }
}
