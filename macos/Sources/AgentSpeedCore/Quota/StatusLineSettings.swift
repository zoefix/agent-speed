import Foundation

/// The settings edits of the status line bridge on their own, text in and text out (QUOTAS 5.2–5.4): what turning it
/// on or off makes of Claude Code's `settings.json`, and what its status is.
public enum StatusLineSettings {
    public static let helperName = "agent-speed-statusline"

    public enum Status: Equatable, Sendable {
        case waiting
        case ok
        case replaced
        case unreadable
    }

    enum TurnOn: Equatable {
        /// The new settings text and the sidecar to keep (nil: none was there and none is written).
        case on(settings: String, previous: OrderedJSON?)
        case unreadable
    }

    /// The copy's absolute path in single quotes, `'` written as `'\''`.
    public static func posixCommand(_ path: String) -> String {
        "'" + path.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    /// A `statusLine` that is a command object whose command runs the helper.
    static func isOurs(_ value: OrderedJSON?) -> Bool {
        guard let value, value.isObject, value["type"]?.string == "command", let command = value["command"]?.string
        else { return false }
        return command.contains(helperName)
    }

    /// The settings as an object; a missing file reads as `{}`, anything but an object is nil.
    static func object(_ text: String?) -> OrderedJSON? {
        guard let text else { return .object([]) }
        guard let value = OrderedJSON.parse(Data(text.utf8)), value.isObject else { return nil }
        return value
    }

    /// Claude Code's own layout: two-space indentation and a final newline.
    static func text(_ value: OrderedJSON) -> String { value.text() + "\n" }

    /// QUOTAS 5.2: the sidecar keeps the current `statusLine` unless it is ours already (then `sidecar` stays), and our
    /// command takes its place, with a previous numeric `padding`.
    static func turnOn(_ settings: String?, command: String, sidecar: OrderedJSON?) -> TurnOn {
        guard var current = object(settings) else { return .unreadable }
        let existing = current["statusLine"]
        let previous: OrderedJSON?
        if isOurs(existing) {
            previous = sidecar
        } else {
            previous = .object([("version", .number("1"))] + (existing.map { [("statusLine", $0)] } ?? []))
        }
        var line: [(String, OrderedJSON)] = [("type", .string("command")), ("command", .string(command))]
        if let padding = previous?["statusLine"]?["padding"], padding.isNumber { line.append(("padding", padding)) }
        current.set("statusLine", .object(line))
        return .on(settings: text(current), previous: previous)
    }

    /// QUOTAS 5.3: the previous value back while the line is still ours (removed when there was none); nil leaves the
    /// file alone.
    static func turnOff(_ settings: String?, previous: OrderedJSON?) -> String? {
        guard let settings, var current = object(settings), isOurs(current["statusLine"]) else { return nil }
        if let line = previous?["statusLine"] {
            current.set("statusLine", line)
        } else {
            current.remove("statusLine")
        }
        return text(current)
    }

    /// QUOTAS 5.4.
    static func status(_ settings: String?, hasQuotaFile: Bool) -> Status {
        guard let current = object(settings) else { return .unreadable }
        guard isOurs(current["statusLine"]) else { return .replaced }
        return hasQuotaFile ? .ok : .waiting
    }
}
