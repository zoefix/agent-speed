import AgentSpeedCore
import AppKit

/// Lets the user pick a transcript folder (or, where a source accepts one, its database file) in a sheet and validates
/// the choice. Never writes anything.
@MainActor
enum FolderChooser {
    /// OpenClaw's agent database (METRICS 4.14), the one file a picker may accept.
    nonisolated static let openclawDatabaseName = "openclaw-agent.sqlite"

    static func choose(
        message: String,
        prompt: String,
        startingAt current: URL,
        window: NSWindow?,
        acceptsDatabaseFile: Bool = false,
        completion: @escaping @MainActor (URL) -> Void
    ) {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = acceptsDatabaseFile
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = false
        panel.treatsFilePackagesAsDirectories = false
        panel.resolvesAliases = true
        panel.showsHiddenFiles = true
        panel.message = message
        panel.prompt = prompt
        panel.directoryURL = nearestExistingFolder(current)
        // The panel holds its delegate weakly; the handlers below keep the filter alive while it is open.
        let filter = acceptsDatabaseFile ? DatabaseFileFilter() : nil
        panel.delegate = filter

        if let window {
            panel.beginSheetModal(for: window) { response in
                MainActor.assumeIsolated {
                    withExtendedLifetime(filter) { finish(panel, response, acceptsDatabaseFile, completion) }
                }
            }
        } else {
            panel.begin { response in
                MainActor.assumeIsolated {
                    withExtendedLifetime(filter) { finish(panel, response, acceptsDatabaseFile, completion) }
                }
            }
        }
    }

    private static func finish(
        _ panel: NSOpenPanel,
        _ response: NSApplication.ModalResponse,
        _ acceptsDatabaseFile: Bool,
        _ completion: @MainActor (URL) -> Void
    ) {
        guard response == .OK, let url = panel.url else { return }
        let database = acceptsDatabaseFile && isOpenClawDatabase(url) ? URL(fileURLWithPath: url.standardizedFileURL.path) : nil
        guard let chosen = database ?? validatedFolder(url) else {
            NSSound.beep()
            return
        }
        completion(chosen)
    }

    /// A local regular file named `openclaw-agent.sqlite` (METRICS 4.14).
    nonisolated static func isOpenClawDatabase(_ url: URL) -> Bool {
        guard url.isFileURL, url.lastPathComponent == openclawDatabaseName else { return false }
        let values = try? url.standardizedFileURL.resourceValues(forKeys: [.isRegularFileKey])
        return values?.isRegularFile == true
    }

    /// A local, existing directory (not a package), as a standardized file URL.
    static func validatedFolder(_ url: URL) -> URL? {
        guard url.isFileURL else { return nil }
        let standardized = url.standardizedFileURL
        let values = try? standardized.resourceValues(forKeys: [.isDirectoryKey, .isPackageKey])
        guard values?.isDirectory == true, values?.isPackage != true else { return nil }
        return URL(fileURLWithPath: standardized.path, isDirectory: true)
    }

    /// Enables folders and `openclaw-agent.sqlite` only, so no other file can be picked.
    private final class DatabaseFileFilter: NSObject, NSOpenSavePanelDelegate {
        func panel(_ sender: Any, shouldEnable url: URL) -> Bool {
            let values = try? url.resourceValues(forKeys: [.isDirectoryKey])
            return values?.isDirectory == true || url.lastPathComponent == FolderChooser.openclawDatabaseName
        }
    }

    private static func nearestExistingFolder(_ url: URL) -> URL {
        var candidate = url.standardizedFileURL
        while !TranscriptFolder.exists(candidate), candidate.pathComponents.count > 1 {
            candidate.deleteLastPathComponent()
        }
        return candidate
    }
}
