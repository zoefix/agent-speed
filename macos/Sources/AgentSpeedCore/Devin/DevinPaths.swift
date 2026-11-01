import Foundation

struct DevinLocation: Equatable, Sendable {
    var displayPath: String
    var databasePath: String?
    var desktopFolders: [String]

    var exists: Bool {
        if let databasePath, FileStat.stat(databasePath)?.isRegularFile == true { return true }
        return desktopFolders.contains { TranscriptDiscovery.isAvailable(root: $0) }
    }
}

enum DevinPaths {
    static func automaticLocation(environment: [String: String] = ProcessInfo.processInfo.environment,
                                  home: URL = FileManager.default.homeDirectoryForCurrentUser) -> DevinLocation {
        let xdg = environment["XDG_DATA_HOME"].flatMap { $0.isEmpty ? nil : $0 }
        let cli = xdg.map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? home.appendingPathComponent(".local/share", isDirectory: true)
        return DevinLocation(
            displayPath: cli.appendingPathComponent("devin", isDirectory: true).path,
            databasePath: cli.appendingPathComponent("devin/cli/sessions.db").path,
            desktopFolders: [home.appendingPathComponent("Library/Application Support/Devin/User/acp-events", isDirectory: true).path])
    }

    static func resolve(_ setting: URL?) -> DevinLocation {
        guard let setting else { return automaticLocation() }
        let path = TranscriptDiscovery.normalizedRoot(setting.path)
        if path.hasSuffix(".db") { return DevinLocation(displayPath: path, databasePath: path, desktopFolders: []) }
        let folder = URL(fileURLWithPath: path, isDirectory: true)
        if folder.lastPathComponent == "acp-events" {
            return DevinLocation(displayPath: path, databasePath: nil, desktopFolders: [path])
        }
        let direct = folder.appendingPathComponent("sessions.db").path
        let nested = folder.appendingPathComponent("cli/sessions.db").path
        let database = FileStat.stat(direct)?.isRegularFile == true ? direct : nested
        let desktop = folder.lastPathComponent == "User"
            ? folder.appendingPathComponent("acp-events").path
            : folder.appendingPathComponent("User/acp-events").path
        return DevinLocation(displayPath: path, databasePath: database, desktopFolders: [desktop])
    }

    static func desktopFiles(in folders: [String]) -> [DiscoveredFile] {
        var files: [DiscoveredFile] = []
        for folder in folders {
            guard let names = try? FileManager.default.contentsOfDirectory(atPath: folder) else { continue }
            for name in names where name.hasSuffix(".ndjson") && !name.hasPrefix(".") {
                let path = (folder as NSString).appendingPathComponent(name)
                guard let stat = FileStat.lstat(path), stat.isRegularFile else { continue }
                files.append(DiscoveredFile(path: path, stat: stat))
            }
        }
        return files.sorted { $0.path < $1.path }
    }
}
