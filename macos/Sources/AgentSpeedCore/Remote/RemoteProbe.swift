import Foundation

/// The output of the probe command (REMOTE 4.2 step 1).
public struct RemoteProbeResult: Equatable, Sendable {
    /// `uname -s -m` as printed, printable ASCII only and at most 64 characters (shown with `unsupportedSystem`).
    public var system: String
    public var kernel: String
    public var machine: String
    /// The hash command the host has; nil when it has none.
    public var hashTool: String?
    /// Lowercase SHA-256 of each existing relay file, by its name (only safe names are kept).
    public var hashes: [String: String]
    /// The script ran to its last line.
    public var isComplete: Bool

    public enum Verdict: Equatable, Sendable {
        case supported(RemoteArch)
        case unsupportedSystem(String)
        case cannotVerify
        /// The output is not the probe's (a login banner on stdout, a cut-off stream): treated as an exit.
        case malformed
    }

    public static let maxOutputBytes = 64 * 1024

    public init(system: String = "", kernel: String = "", machine: String = "", hashTool: String? = nil,
                hashes: [String: String] = [:], isComplete: Bool = false) {
        self.system = system
        self.kernel = kernel
        self.machine = machine
        self.hashTool = hashTool
        self.hashes = hashes
        self.isComplete = isComplete
    }

    /// Reads the probe's stdout; lines it does not know are ignored (a shell may print a banner first).
    public static func parse(_ output: Data) -> RemoteProbeResult {
        var result = RemoteProbeResult()
        let text = String(decoding: output.prefix(maxOutputBytes), as: UTF8.self)
        for line in text.split(whereSeparator: \.isNewline) {
            if line.hasPrefix("system ") {
                let value = displayable(line.dropFirst(7))
                let words = value.split(separator: " ")
                result.system = value
                result.kernel = words.first.map(String.init) ?? ""
                result.machine = words.count > 1 ? words.last.map(String.init) ?? "" : ""
            } else if line.hasPrefix("tool ") {
                let tool = String(line.dropFirst(5))
                result.hashTool = tool == "none" ? nil : displayable(Substring(tool))
            } else if line.hasPrefix("hash ") {
                let parts = line.dropFirst(5).split(separator: " ", maxSplits: 1)
                guard parts.count == 2 else { continue }
                let digest = String(parts[0]).lowercased()
                let name = String(parts[1])
                if RemoteCommand.isDigest(digest), RemoteRelayName.isSafe(name),
                   name.hasPrefix(RemoteRelayName.installedPrefix) {
                    result.hashes[name] = digest
                }
            } else if line == "end" {
                result.isComplete = true
            }
        }
        return result
    }

    public var verdict: Verdict {
        guard isComplete, !kernel.isEmpty else { return .malformed }
        guard kernel == "Linux", let arch = RemoteArch(machine: machine) else { return .unsupportedSystem(system) }
        guard hashTool != nil else { return .cannotVerify }
        return .supported(arch)
    }

    /// The installed relay matches the bundled one.
    public func hasInstalled(name: String, sha256: String) -> Bool {
        hashes[name] == sha256.lowercased()
    }

    /// The hash the upload script printed (`hash <hex>`), lowercase; nil when absent.
    public static func uploadedHash(_ output: Data) -> String? {
        let text = String(decoding: output.prefix(maxOutputBytes), as: UTF8.self)
        for line in text.split(whereSeparator: \.isNewline) where line.hasPrefix("hash ") {
            let digest = String(line.dropFirst(5)).lowercased()
            if RemoteCommand.isDigest(digest) { return digest }
        }
        return nil
    }

    private static func displayable(_ text: Substring) -> String {
        String(String.UnicodeScalarView(text.unicodeScalars.filter { (0x20...0x7E).contains($0.value) }.prefix(64)))
            .trimmingCharacters(in: .whitespaces)
    }
}
