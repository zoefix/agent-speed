import Darwin
import Foundation

/// What a profile's `config.yaml` says (METRICS 4.15).
struct HermesProfileConfig: Equatable, Sendable {
    /// The Codex runtime switch.
    var codexRuntime = false
    /// Configured values: `agent.reasoning_effort` and `agent.service_tier`.
    var configuredModes: ExecutionModes = .empty
}

/// Hermes Agent's Codex runtime switch (METRICS 4.15): whether a profile's `config.yaml` sets `model.openai_runtime`
/// to `codex_app_server`, plus the two scalars of its `agent` block. A tolerant line reader for the block layout Hermes
/// writes, not a YAML parser; no other key of the file is read. It works on the UTF-8 bytes (every pattern is ASCII).
enum HermesCodexRuntime {
    /// Larger files (at the check or while reading) turn the switch off and hold no values.
    static let maxBytes = 1 << 20

    /// Whether the text selects the Codex runtime: the last `openai_runtime` key line of a top-level `model` block.
    static func isOn(_ data: some Collection<UInt8>) -> Bool {
        var bytes = Array(data)
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) { bytes.removeFirst(3) }
        var codex = false
        var inModel = false
        var indent: Int?
        for var line in bytes.split(separator: UInt8(ascii: "\n"), omittingEmptySubsequences: false) {
            if line.last == UInt8(ascii: "\r") { line = line.dropLast() }
            let content = line.drop(while: { $0 == space || $0 == tab })
            if content.isEmpty || content.first == UInt8(ascii: "#") { continue }
            let body = line.drop(while: { $0 == space })
            let depth = line.count - body.count
            if depth == 0 {
                inModel = isModelHeader(line)
                indent = nil
                continue
            }
            guard inModel else { continue }
            if indent == nil { indent = depth }
            if depth == indent, let value = runtimeValue(body) { codex = selectsCodex(value) }
        }
        return codex
    }

    /// The scalars `reasoning_effort` and `service_tier` of a top-level `agent` block: the last key line of each
    /// decides, and anything but one plain scalar gives nil.
    static func agentScalars(_ data: some Collection<UInt8>) -> [String: String?] {
        var bytes = Array(data)
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) { bytes.removeFirst(3) }
        var found: [String: String?] = [:]
        var inAgent = false
        var indent: Int?
        for var line in bytes.split(separator: UInt8(ascii: "\n"), omittingEmptySubsequences: false) {
            if line.last == UInt8(ascii: "\r") { line = line.dropLast() }
            let content = line.drop(while: { $0 == space || $0 == tab })
            if content.isEmpty || content.first == UInt8(ascii: "#") { continue }
            let body = line.drop(while: { $0 == space })
            let depth = line.count - body.count
            if depth == 0 {
                inAgent = isBlockHeader(line, "agent")
                indent = nil
                continue
            }
            guard inAgent else { continue }
            if indent == nil { indent = depth }
            guard depth == indent else { continue }
            for name in ["reasoning_effort", "service_tier"] {
                if let value = keyValue(body, name) { found[name] = .some(scalar(value)) }
            }
        }
        return found
    }

    /// The configured facts of the `agent` block.
    static func configuredModes(_ data: some Collection<UInt8>) -> ExecutionModes {
        let scalars = agentScalars(data)
        return ExecutionModes([
            ExecutionModeAdapters.hermesEffort(scalars["reasoning_effort"] ?? nil, evidence: .configured, scope: .configuration),
            ExecutionModeAdapters.hermesServiceTier(scalars["service_tier"] ?? nil, evidence: .configured, scope: .configuration),
        ].compactMap { $0 })
    }

    /// `^[ \t]+("[ \t]*(token)[ \t]*"|'[ \t]*(token)[ \t]*'|(token))([ \t]+#.*)?[ \t]*$`, token `[A-Za-z0-9._:-]+`.
    static func scalar(_ value: ArraySlice<UInt8>) -> String? {
        func isToken(_ byte: UInt8) -> Bool {
            (UInt8(ascii: "a")...UInt8(ascii: "z")).contains(byte) || (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains(byte)
                || (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(byte) || ".-_:".utf8.contains(byte)
        }
        guard value.first == space || value.first == tab else { return nil }
        var rest = value.drop(while: { $0 == space || $0 == tab })
        let quote = rest.first.flatMap { $0 == UInt8(ascii: "\"") || $0 == UInt8(ascii: "'") ? $0 : nil }
        if quote != nil { rest = rest.dropFirst().drop(while: { $0 == space || $0 == tab }) }
        let token = rest.prefix(while: isToken)
        guard !token.isEmpty else { return nil }
        rest = rest.dropFirst(token.count)
        if let quote {
            rest = rest.drop(while: { $0 == space || $0 == tab })
            guard rest.first == quote else { return nil }
            rest = rest.dropFirst()
        }
        let gap = rest.prefix(while: { $0 == space || $0 == tab })
        rest = rest.dropFirst(gap.count)
        guard rest.isEmpty || (!gap.isEmpty && rest.first == UInt8(ascii: "#")) else { return nil }
        return String(decoding: token, as: UTF8.self)
    }

    /// `^(model|"model"|'model')[ \t]*:([ \t]+(#.*)?)?$`.
    static func isModelHeader(_ line: ArraySlice<UInt8>) -> Bool { isBlockHeader(line, "model") }

    private static func isBlockHeader(_ line: ArraySlice<UInt8>, _ name: String) -> Bool {
        guard var rest = key(line, name) else { return false }
        rest = rest.drop(while: { $0 == space || $0 == tab })
        guard rest.first == UInt8(ascii: ":") else { return false }
        rest = rest.dropFirst()
        if rest.isEmpty { return true }
        guard rest.first == space || rest.first == tab else { return false }
        rest = rest.drop(while: { $0 == space || $0 == tab })
        return rest.isEmpty || rest.first == UInt8(ascii: "#")
    }

    /// The text after the colon of `^(openai_runtime|"openai_runtime"|'openai_runtime')[ \t]*:(|[ \t].*)$`.
    static func runtimeValue(_ body: ArraySlice<UInt8>) -> ArraySlice<UInt8>? { keyValue(body, "openai_runtime") }

    private static func keyValue(_ body: ArraySlice<UInt8>, _ name: String) -> ArraySlice<UInt8>? {
        guard var rest = key(body, name) else { return nil }
        rest = rest.drop(while: { $0 == space || $0 == tab })
        guard rest.first == UInt8(ascii: ":") else { return nil }
        rest = rest.dropFirst()
        guard rest.isEmpty || rest.first == space || rest.first == tab else { return nil }
        return rest
    }

    /// `^[ \t]+("[ \t]*codex_app_server[ \t]*"|'[ \t]*codex_app_server[ \t]*'|codex_app_server)([ \t]+#.*)?[ \t]*$`,
    /// `codex_app_server` compared ASCII case-insensitively.
    static func selectsCodex(_ value: ArraySlice<UInt8>) -> Bool {
        guard value.first == space || value.first == tab else { return false }
        var rest = value.drop(while: { $0 == space || $0 == tab })
        if let quote = rest.first, quote == UInt8(ascii: "\"") || quote == UInt8(ascii: "'") {
            rest = rest.dropFirst().drop(while: { $0 == space || $0 == tab })
            guard let after = codexName(rest) else { return false }
            rest = after.drop(while: { $0 == space || $0 == tab })
            guard rest.first == quote else { return false }
            rest = rest.dropFirst()
        } else {
            guard let after = codexName(rest) else { return false }
            rest = after
        }
        let gap = rest.prefix(while: { $0 == space || $0 == tab })
        rest = rest.dropFirst(gap.count)
        return rest.isEmpty || (!gap.isEmpty && rest.first == UInt8(ascii: "#"))
    }

    // MARK: - Reading the file

    /// The result of checking a profile's `config.yaml`.
    enum Check: Equatable {
        /// The file was read (or is too large): the switch and values take this.
        case read(HermesProfileConfig, FileSignature)
        /// Missing or not a regular file: the switch is off and there are no values.
        case missing
        /// Unchanged since the last successful read, or a read failure: the switch keeps its value.
        case unchanged
    }

    /// Modification time and size of the last successful read.
    struct FileSignature: Equatable, Sendable {
        var modifiedNs: Int64
        var size: Int64
    }

    /// Checks the file (following a symbolic link) and reads it when it is a regular file whose signature differs.
    static func check(_ path: String, last: FileSignature?) -> Check {
        var info = Darwin.stat()
        guard fstatat(AT_FDCWD, path, &info, 0) == 0 else {
            return errno == ENOENT || errno == ENOTDIR ? .missing : .unchanged
        }
        guard info.st_mode & S_IFMT == S_IFREG else { return .missing }
        let signature = FileSignature(
            modifiedNs: Int64(info.st_mtimespec.tv_sec) * 1_000_000_000 + Int64(info.st_mtimespec.tv_nsec),
            size: Int64(info.st_size))
        if signature == last { return .unchanged }
        guard signature.size <= maxBytes else { return .read(HermesProfileConfig(), signature) }
        let descriptor = Darwin.open(path, O_RDONLY | O_CLOEXEC | O_NONBLOCK)
        guard descriptor >= 0 else { return .unchanged }
        defer { Darwin.close(descriptor) }
        var data = [UInt8](repeating: 0, count: maxBytes + 1)
        var count = 0
        while count < data.count {
            let read = data.withUnsafeMutableBytes { Darwin.pread(descriptor, $0.baseAddress! + count, $0.count - count, off_t(count)) }
            if read < 0 {
                if errno == EINTR { continue }
                return .unchanged
            }
            if read == 0 { break }
            count += read
        }
        guard count <= maxBytes else { return .read(HermesProfileConfig(), signature) }
        let text = data[0..<count]
        return .read(HermesProfileConfig(codexRuntime: isOn(text), configuredModes: configuredModes(text)), signature)
    }

    // MARK: - Helpers

    private static let space = UInt8(ascii: " ")
    private static let tab = UInt8(ascii: "\t")
    private static let codexValue = Array("codex_app_server".utf8)

    /// The rest after `name`, `"name"` or `'name'` at the start of `text`.
    private static func key(_ text: ArraySlice<UInt8>, _ name: String) -> ArraySlice<UInt8>? {
        let plain = Array(name.utf8)
        for quote in [nil, UInt8(ascii: "\""), UInt8(ascii: "'")] as [UInt8?] {
            let candidate = quote.map { [$0] + plain + [$0] } ?? plain
            if text.starts(with: candidate) { return text.dropFirst(candidate.count) }
        }
        return nil
    }

    /// The rest after `codex_app_server` (ASCII case-insensitive) at the start of `text`.
    private static func codexName(_ text: ArraySlice<UInt8>) -> ArraySlice<UInt8>? {
        guard text.count >= codexValue.count else { return nil }
        for (byte, expected) in zip(text, codexValue) where lowercased(byte) != expected { return nil }
        return text.dropFirst(codexValue.count)
    }

    private static func lowercased(_ byte: UInt8) -> UInt8 {
        (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains(byte) ? byte + 32 : byte
    }
}
