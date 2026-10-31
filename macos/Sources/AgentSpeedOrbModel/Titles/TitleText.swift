import Foundation

/// The text rules of custom titles and names (DESIGN 1, Custom title sets), applied on commit.
public enum TitleText {
    /// A title or idle label holds at most this many grapheme clusters.
    public static let graphemeLimit = 24
    /// The maximum width budget `w7` in points (the readout's title style at the 7 pt floor, tracking 0).
    public static let maximumWidth = 51.5
    /// The comfortable budget (fits every style from 64 pt).
    public static let comfortableWidth = 44.2

    public struct Result: Equatable, Sendable {
        public var text: String
        /// Color emoji were removed (`titles.custom.noEmoji`).
        public var removedEmoji: Bool
    }

    /// NFC; controls, line/paragraph separators and bidi controls removed; tabs, line breaks and other spaces become one
    /// space; runs collapse; trimmed; emoji with emoji presentation removed; at most 24 grapheme clusters.
    public static func sanitize(_ text: String, limit: Int = graphemeLimit) -> Result {
        var scalars = String.UnicodeScalarView()
        var lastWasSpace = true
        for scalar in text.precomposedStringWithCanonicalMapping.unicodeScalars {
            if isSpace(scalar) {
                if !lastWasSpace { scalars.append(" ") }
                lastWasSpace = true
                continue
            }
            if isRemoved(scalar) { continue }
            scalars.append(scalar)
            lastWasSpace = false
        }
        var removedEmoji = false
        var kept = ""
        var count = 0
        for character in String(scalars) {
            if isColorEmoji(character) {
                removedEmoji = true
                continue
            }
            guard count < limit else { break }
            kept.append(character)
            count += 1
        }
        let collapsed = kept.split(separator: " ", omittingEmptySubsequences: true).joined(separator: " ")
        return Result(text: collapsed, removedEmoji: removedEmoji)
    }

    /// The longest prefix of whole grapheme clusters whose width fits `maxWidth`, and whether anything was cut.
    public static func fitted(_ text: String, maxWidth: Double, measure: (String) -> Double) -> (text: String, cut: Bool) {
        guard measure(text) > maxWidth else { return (text, false) }
        let characters = Array(text)
        var low = 0
        var high = characters.count
        while low < high {
            let middle = (low + high + 1) / 2
            if measure(String(characters[0..<middle])) <= maxWidth { low = middle } else { high = middle - 1 }
        }
        let prefix = String(characters[0..<low]).trimmingCharacters(in: .whitespaces)
        return (prefix, true)
    }

    /// The readout's case rule: Latin uppercase on the orb and the badge when the app language is English.
    public static func displayCase(_ text: String, english: Bool) -> String {
        english ? text.uppercased() : text
    }

    private static func isSpace(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x85: return true
        default: return scalar.properties.generalCategory == .spaceSeparator
        }
    }

    private static func isRemoved(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x202A...0x202E, 0x2066...0x2069: return true
        default: break
        }
        switch scalar.properties.generalCategory {
        case .control, .lineSeparator, .paragraphSeparator: return true
        default: return false
        }
    }

    /// A grapheme drawn as a color emoji: an emoji-presentation scalar, or any emoji with the emoji variation selector
    /// or a keycap. Text symbols such as ★ ✦ ∞ stay.
    static func isColorEmoji(_ character: Character) -> Bool {
        let scalars = character.unicodeScalars
        if scalars.contains(where: { $0.properties.isEmojiPresentation }) { return true }
        if scalars.contains(where: { $0.value == 0xFE0F || $0.value == 0x20E3 }) {
            return scalars.contains { $0.properties.isEmoji }
        }
        return false
    }
}
