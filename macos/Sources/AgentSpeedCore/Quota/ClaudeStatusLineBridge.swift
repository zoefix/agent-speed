import AgentSpeedStatusLineKit
import Darwin
import Foundation

/// The Claude CLI's status line bridge on disk (QUOTAS 5.1–5.4): installs the helper copy, edits Claude Code's user
/// settings and restores them, and tells its status. Off by default; nothing here runs until the user turns it on.
public struct ClaudeStatusLineBridge: Sendable {
    public enum Outcome: Equatable, Sendable {
        case on
        /// The settings file is not a JSON object: nothing was changed.
        case unreadable
        /// A file could not be written: nothing was changed.
        case failed
    }

    /// `settings`: Claude Code's user settings file. `data`: the app's data folder (`bin/`, `quota/`).
    public let settings: URL
    public let data: URL

    public init(settings: URL = ClaudeStatusLineBridge.defaultSettings, data: URL) {
        self.settings = settings
        self.data = data
    }

    /// `$CLAUDE_CONFIG_DIR/settings.json`, else `~/.claude/settings.json`.
    public static var defaultSettings: URL {
        claudeConfigFolder.appendingPathComponent("settings.json")
    }

    /// `$CLAUDE_CONFIG_DIR`, else `~/.claude`.
    public static var claudeConfigFolder: URL {
        if let folder = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"], !folder.isEmpty {
            return URL(fileURLWithPath: (folder as NSString).expandingTildeInPath, isDirectory: true)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude", isDirectory: true)
    }

    public var helperCopy: URL { data.appendingPathComponent("bin", isDirectory: true).appendingPathComponent(StatusLineSettings.helperName) }
    public var folders: StatusLineHelper.Folders { StatusLineHelper.Folders(quota: data.appendingPathComponent("quota", isDirectory: true)) }
    public var quotaFile: URL { folders.quotaFile }
    public var sidecar: URL { folders.previousFile }
    public var backup: URL { folders.quota.appendingPathComponent("claude-settings.backup.json") }
    public var command: String { StatusLineSettings.posixCommand(helperCopy.path) }

    // MARK: Turning it on and off

    /// QUOTAS 5.2: copies the helper, keeps the current status line in the sidecar and the settings in the backup,
    /// then points `statusLine` at the copy. A failure leaves the settings as they were.
    public func turnOn(helper: URL) -> Outcome {
        let text: String?
        switch readSettings() {
        case .missing: text = nil
        case .text(let value): text = value
        case .failed: return .failed
        }
        let sidecarValue = (try? Data(contentsOf: sidecar)).flatMap { OrderedJSON.parse($0) }
        guard case .on(let updated, let previous) = StatusLineSettings.turnOn(text, command: command, sidecar: sidecarValue) else {
            return .unreadable
        }
        guard installHelper(from: helper) else { return .failed }
        let ours = StatusLineSettings.isOurs(StatusLineSettings.object(text)?["statusLine"])
        if !ours {
            guard makeQuotaFolder(), let previous,
                  StatusLineHelper.writePrivate(Data(StatusLineSettings.text(previous).utf8), to: sidecar) else { return .failed }
            if let text, !StatusLineHelper.writePrivate(Data(text.utf8), to: backup) {
                removeSidecarFiles(keepingQuota: true)
                return .failed
            }
        }
        guard writeSettings(updated) else {
            if !ours { removeSidecarFiles(keepingQuota: true) }
            return .failed
        }
        return .on
    }

    /// QUOTAS 5.3: puts the previous status line back while ours is still there (a line the user set meanwhile is
    /// left alone), then deletes the sidecar, the backup and the quota file.
    @discardableResult
    public func turnOff() -> Bool {
        var restored = true
        if case .text(let text) = readSettings() {
            let previous = (try? Data(contentsOf: sidecar)).flatMap { OrderedJSON.parse($0) }
            if let updated = StatusLineSettings.turnOff(text, previous: previous) { restored = writeSettings(updated) }
        }
        removeSidecarFiles(keepingQuota: false)
        return restored
    }

    /// QUOTAS 5.4.
    public func status() -> StatusLineSettings.Status {
        let text: String?
        switch readSettings() {
        case .missing: text = nil
        case .text(let value): text = value
        case .failed: return .unreadable
        }
        return StatusLineSettings.status(text, hasQuotaFile: FileManager.default.fileExists(atPath: quotaFile.path))
    }

    /// At every launch while the bridge is on: the copy follows the app's helper (replaced only when the bytes differ).
    @discardableResult
    public func installHelper(from source: URL) -> Bool {
        guard let bytes = try? Data(contentsOf: source), !bytes.isEmpty else { return false }
        if let current = try? Data(contentsOf: helperCopy), current == bytes {
            return FileManager.default.isExecutableFile(atPath: helperCopy.path)
        }
        let folder = helperCopy.deletingLastPathComponent()
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o755])
        } catch {
            return false
        }
        return Self.replace(helperCopy, with: bytes, mode: 0o755)
    }

    // MARK: Files

    private enum SettingsRead {
        case missing
        case text(String)
        case failed
    }

    private func readSettings() -> SettingsRead {
        guard FileManager.default.fileExists(atPath: settings.path) else { return .missing }
        guard let data = try? Data(contentsOf: settings) else { return .failed }
        // Not UTF-8 reads as text that is not JSON; a byte-order mark stays and makes it unreadable, as for Python.
        guard String(bytes: data, encoding: .utf8) != nil else { return .text("\u{FFFD}") }
        return .text(String(decoding: data, as: UTF8.self))
    }

    /// A temporary file, then a rename, keeping the file's permissions; a symlinked settings file is written where it
    /// points, so the link stays.
    private func writeSettings(_ text: String) -> Bool {
        var target = settings
        if let resolved = realpath(settings.path, nil) {
            target = URL(fileURLWithPath: String(cString: resolved))
            free(resolved)
        }
        var mode: mode_t = 0o644
        if let current = try? FileManager.default.attributesOfItem(atPath: target.path)[.posixPermissions] as? NSNumber {
            mode = mode_t(current.uint16Value & 0o7777)
        }
        do {
            try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        } catch {
            return false
        }
        return Self.replace(target, with: Data(text.utf8), mode: mode)
    }

    private func makeQuotaFolder() -> Bool {
        do {
            try FileManager.default.createDirectory(at: folders.quota, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
            return true
        } catch {
            return false
        }
    }

    private func removeSidecarFiles(keepingQuota: Bool) {
        for url in [sidecar, backup] + (keepingQuota ? [] : [quotaFile]) { unlink(url.path) }
    }

    static func replace(_ url: URL, with data: Data, mode: mode_t) -> Bool {
        let temporary = url.deletingLastPathComponent()
            .appendingPathComponent(".\(url.lastPathComponent).agent-speed-\(getpid()).tmp")
        unlink(temporary.path)
        let descriptor = open(temporary.path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, mode)
        guard descriptor >= 0 else { return false }
        let written = data.withUnsafeBytes { buffer -> Bool in
            var offset = 0
            while offset < buffer.count {
                let count = write(descriptor, buffer.baseAddress! + offset, buffer.count - offset)
                if count <= 0 { return false }
                offset += count
            }
            return true
        }
        fchmod(descriptor, mode)
        let synced = fsync(descriptor) == 0
        let closed = close(descriptor) == 0
        guard synced, closed, written, rename(temporary.path, url.path) == 0 else {
            unlink(temporary.path)
            return false
        }
        return true
    }
}
