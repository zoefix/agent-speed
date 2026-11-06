import Foundation

/// One root of Devin's location list (METRICS 2.1, 4.17).
enum DevinRoot: Hashable, Sendable {
    /// The automatic entry's CLI database.
    case database(String)
    /// A user location: a folder whose database is looked up again at every rediscovery.
    case location(String)

    var path: String {
        switch self {
        case .database(let path), .location(let path): path
        }
    }

    /// The database this root names now, if it exists.
    var database: String? {
        switch self {
        case .database(let path): DevinPaths.isRegularFile(path) ? path : nil
        case .location(let folder): DevinPaths.database(ofLocation: folder)
        }
    }

    /// The database the root names for the editing rules (METRICS 2.1): the one that exists, else where a CLI folder
    /// keeps it, resolved.
    var locationKey: String {
        switch self {
        case .database(let path): LocationPaths.resolved(path)
        case .location(let folder):
            LocationPaths.resolved(database ?? (folder as NSString).appendingPathComponent(DevinPaths.databaseName))
        }
    }

    /// The folder Settings and the source row name for it: a database's folder, else the location itself.
    var displayFolder: String {
        if case .database(let path) = self { return (path as NSString).deletingLastPathComponent }
        return path
    }
}

/// Where the Devin CLI keeps its data (METRICS 4.17).
enum DevinPaths {
    static let databaseName = "sessions.db"

    /// `$CHISEL_SESSION_DB`, else `$XDG_DATA_HOME/devin/cli/sessions.db`, else `~/.local/share/devin/cli/sessions.db`.
    static func automaticDatabase(environment: [String: String], home: String) -> String {
        if let override = environment["CHISEL_SESSION_DB"], !override.isEmpty { return override }
        let data = environment["XDG_DATA_HOME"].flatMap { $0.isEmpty ? nil : $0 }
            ?? (home as NSString).appendingPathComponent(".local/share")
        return ((data as NSString).appendingPathComponent("devin/cli") as NSString).appendingPathComponent(databaseName)
    }

    static func automaticRoots(environment: [String: String], home: String) -> [DevinRoot] {
        [.database(automaticDatabase(environment: environment, home: home))]
    }

    /// A user location's database: `sessions.db` in it, else in its `cli` folder.
    static func database(ofLocation folder: String) -> String? {
        [databaseName, "cli/" + databaseName].lazy
            .map { (folder as NSString).appendingPathComponent($0) }
            .first(where: isRegularFile)
    }

    /// The databases a list of roots names now: each once (resolved), in list order.
    static func databases(_ roots: [DevinRoot]) -> [String] {
        var seen: Set<String> = []
        return roots.compactMap(\.database).filter { seen.insert(LocationPaths.resolved($0)).inserted }
    }

    /// The last component of a working directory, split on `/` and `\`.
    static func project(ofWorkingDirectory directory: String) -> String? {
        let name = directory.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init)
        return name?.isEmpty == false ? name : nil
    }

    /// A regular file, never a symbolic link.
    static func isRegularFile(_ path: String) -> Bool { FileStat.lstat(path)?.isRegularFile == true }
}
