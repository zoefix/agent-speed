import Foundation

/// Why a `destination` or `relayFolder` is refused (REMOTE 2.1). The editor shows it under the field.
public enum RemoteFieldIssue: Equatable, Sendable {
    case empty
    case tooLong
    case leadingDash
    /// A byte outside the allowed set (shell metacharacters, spaces, quotes, non-ASCII, control characters).
    case forbiddenCharacter
    /// `relayFolder` only: neither `~/…` nor absolute.
    case notAbsolute
    /// `relayFolder` only: a `..` component or `//`.
    case unsafeComponent
}

/// The rules of REMOTE 2.1. Every value that reaches an `ssh` argument vector or a remote command passed these, so a
/// remote command can insert it unquoted.
public enum RemoteHostValidation {
    public static let destinationLimit = 255
    public static let folderLimit = 200
    public static let nameLimit = 32

    /// Printable ASCII except space and the shell metacharacters of REMOTE 2.1.
    private static let destinationForbidden = Set("\"'`\\;&|$<>()*?!{}#".utf8)

    public static func destinationIssue(_ destination: String) -> RemoteFieldIssue? {
        let bytes = Array(destination.utf8)
        guard !bytes.isEmpty else { return .empty }
        guard bytes.count <= destinationLimit else { return .tooLong }
        guard bytes.allSatisfy({ (0x21...0x7E).contains($0) && !destinationForbidden.contains($0) }) else {
            return .forbiddenCharacter
        }
        guard bytes[0] != UInt8(ascii: "-") else { return .leadingDash }
        return nil
    }

    public static func folderIssue(_ folder: String) -> RemoteFieldIssue? {
        let bytes = Array(folder.utf8)
        guard !bytes.isEmpty else { return .empty }
        guard bytes.count <= folderLimit else { return .tooLong }
        guard bytes.allSatisfy(isFolderByte) else { return .forbiddenCharacter }
        guard folder.hasPrefix("/") || folder.hasPrefix("~/") else { return .notAbsolute }
        guard !folder.contains("//"), !folder.split(separator: "/").contains("..") else { return .unsafeComponent }
        return nil
    }

    /// `[A-Za-z0-9._/~-]`.
    static func isFolderByte(_ byte: UInt8) -> Bool {
        isNameByte(byte) || byte == UInt8(ascii: "/") || byte == UInt8(ascii: "~")
    }

    /// `[A-Za-z0-9._-]`: relay file names (REMOTE 4.1).
    static func isNameByte(_ byte: UInt8) -> Bool {
        (0x30...0x39).contains(byte) || (0x41...0x5A).contains(byte) || (0x61...0x7A).contains(byte)
            || byte == UInt8(ascii: ".") || byte == UInt8(ascii: "_") || byte == UInt8(ascii: "-")
    }

    /// A valid folder without its trailing slash (`~/` stays `~`), so `<folder>/<file>` never doubles a slash.
    static func normalizedFolder(_ folder: String) -> String {
        var folder = folder
        while folder.count > 1, folder.hasSuffix("/") { folder.removeLast() }
        return folder
    }

    /// An upper-case UUID string (`UUID().uuidString`), never `local`.
    public static func isValidId(_ id: String) -> Bool {
        guard id != RemoteHost.localId, let uuid = UUID(uuidString: id) else { return false }
        return uuid.uuidString == id
    }

    /// Trimmed, control characters removed, at most 32 characters; empty means automatic (nil).
    public static func cleanName(_ name: String?) -> String? {
        guard let name else { return nil }
        let visible = String(String.UnicodeScalarView(name.unicodeScalars.filter { !isControl($0) }))
        let trimmed = visible.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        guard trimmed.count > nameLimit else { return trimmed }
        return String(trimmed.prefix(nameLimit)).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The host part of a destination: `ssh://` removed, then everything up to the last `@`, then a port (`:<digits>`
    /// of the `ssh://` form, brackets of an IPv6 literal). Falls back to the destination when nothing is left.
    public static func automaticName(for destination: String) -> String {
        var rest = Substring(destination)
        let isURL = rest.lowercased().hasPrefix("ssh://")
        if isURL {
            rest = rest.dropFirst(6)
            if let slash = rest.firstIndex(of: "/") { rest = rest[..<slash] }
        }
        if let at = rest.lastIndex(of: "@") { rest = rest[rest.index(after: at)...] }
        if rest.hasPrefix("["), let close = rest.firstIndex(of: "]") {
            rest = rest[rest.index(after: rest.startIndex)..<close]
        } else if isURL, let colon = rest.lastIndex(of: ":"),
                  rest[rest.index(after: colon)...].allSatisfy(\.isASCIIDigit) {
            rest = rest[..<colon]
        }
        let name = String(rest.prefix(nameLimit))
        return name.isEmpty ? String(destination.prefix(nameLimit)) : name
    }

    static func isControl(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value < 0x20 || (0x7F...0x9F).contains(scalar.value)
    }
}

extension Character {
    fileprivate var isASCIIDigit: Bool { isASCII && isNumber }
}
