import Foundation

/// One root of Qoder IDE's location list (METRICS 2.1, 4.18).
enum QoderRoot: Hashable, Sendable {
    /// An automatic entry's database: one per edition.
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
        case .database(let path): QoderPaths.isRegularFile(path) ? path : nil
        case .location(let folder): QoderPaths.database(ofLocation: folder)
        }
    }

    /// The database the root names for the editing rules (METRICS 2.1): the one that exists, else where the folder's
    /// layout keeps it, resolved.
    var locationKey: String {
        switch self {
        case .database(let path): LocationPaths.resolved(path)
        case .location(let folder): LocationPaths.resolved(database ?? QoderPaths.expectedDatabase(ofLocation: folder))
        }
    }

    /// The folder Settings and the source row name for it: a database's folder, else the location itself.
    var displayFolder: String {
        if case .database(let path) = self { return (path as NSString).deletingLastPathComponent }
        return path
    }
}

/// Where Qoder IDE and Qoder CLI keep their data (METRICS 2, 4.18, 4.19).
enum QoderPaths {
    static let databaseName = "local.db"
    /// The IDE data folders: the international edition, then the China edition.
    static let editions = ["Qoder", "QoderCN"]
    static let databaseTail = "SharedClientCache/cache/db/" + databaseName
    /// What a user location may be, in lookup order: the folder that holds `local.db`, the `SharedClientCache` folder,
    /// the IDE's data folder.
    static let locationTails = [databaseName, "cache/db/" + databaseName, databaseTail]

    /// `<data>/<edition>/SharedClientCache/cache/db/local.db` of both editions, `<data>` = `~/Library/Application Support`.
    static func automaticDatabases(home: String) -> [String] {
        let data = (home as NSString).appendingPathComponent("Library/Application Support")
        return editions.map { ((data as NSString).appendingPathComponent($0) as NSString).appendingPathComponent(databaseTail) }
    }

    static func automaticRoots(home: String) -> [QoderRoot] {
        automaticDatabases(home: home).map(QoderRoot.database)
    }

    /// A user location's database: the first of its layouts that exists.
    static func database(ofLocation folder: String) -> String? {
        locationTails.lazy.map { (folder as NSString).appendingPathComponent($0) }.first(where: isRegularFile)
    }

    /// Where a folder without a database would keep it: under the deepest layout whose folders exist.
    static func expectedDatabase(ofLocation folder: String) -> String {
        for tail in locationTails.reversed() {
            let candidate = (folder as NSString).appendingPathComponent(tail)
            if FileStat.stat((candidate as NSString).deletingLastPathComponent)?.isDirectory == true { return candidate }
        }
        return (folder as NSString).appendingPathComponent(databaseName)
    }

    /// The databases a list of roots names now: each once (resolved), in list order.
    static func databases(_ roots: [QoderRoot]) -> [String] {
        var seen: Set<String> = []
        return roots.compactMap(\.database).filter { seen.insert(LocationPaths.resolved($0)).inserted }
    }

    /// The working folder of `chat_session.project_uri`: a path as it is, or a `file://` URI's path, percent-decoded,
    /// without the `/` before a drive letter (`file:///c%3A/work/app` → `c:/work/app`).
    static func projectPath(ofURI uri: String?) -> String? {
        guard let uri, !uri.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        guard uri.prefix(7).lowercased() == "file://" else { return uri }
        var rest = Substring(uri.dropFirst(7))
        if !rest.hasPrefix("/") {
            rest = rest.firstIndex(of: "/").map { rest[$0...] } ?? ""
        }
        var decoded = percentDecoded(rest)
        let scalars = Array(decoded.unicodeScalars.prefix(3))
        if scalars.count == 3, scalars[0] == "/", scalars[2] == ":", scalars[1].isASCII, scalars[1].properties.isAlphabetic {
            decoded.removeFirst()
        }
        return decoded.isEmpty ? nil : decoded
    }

    /// `%XX` sequences as bytes, read as UTF-8 (invalid sequences become U+FFFD); any other `%` stays.
    static func percentDecoded(_ text: Substring) -> String {
        let bytes = Array(text.utf8)
        var output: [UInt8] = []
        output.reserveCapacity(bytes.count)
        var index = 0
        while index < bytes.count {
            if bytes[index] == UInt8(ascii: "%"), index + 2 < bytes.count,
               let high = hexValue(bytes[index + 1]), let low = hexValue(bytes[index + 2]) {
                output.append(high << 4 | low)
                index += 3
            } else {
                output.append(bytes[index])
                index += 1
            }
        }
        return String(decoding: output, as: UTF8.self)
    }

    private static func hexValue(_ byte: UInt8) -> UInt8? {
        switch byte {
        case UInt8(ascii: "0")...UInt8(ascii: "9"): byte - UInt8(ascii: "0")
        case UInt8(ascii: "a")...UInt8(ascii: "f"): byte - UInt8(ascii: "a") + 10
        case UInt8(ascii: "A")...UInt8(ascii: "F"): byte - UInt8(ascii: "A") + 10
        default: nil
        }
    }

    /// A regular file, never a symbolic link.
    static func isRegularFile(_ path: String) -> Bool { FileStat.lstat(path)?.isRegularFile == true }

    // MARK: - Qoder CLI (METRICS 4.19)

    /// Each edition's config folder variable and default folder name.
    static let cliFolders = [("QODER_CONFIG_DIR", ".qoder"), ("QODERCN_CONFIG_DIR", ".qoder-cn")]

    /// `<config>/projects` of both editions: `$QODER_CONFIG_DIR`, else `~/.qoder`; `$QODERCN_CONFIG_DIR`, else
    /// `~/.qoder-cn`.
    static func cliRoots(environment: [String: String], home: String) -> [String] {
        cliFolders.map { variable, folder in
            let base = environment[variable].flatMap { $0.isEmpty ? nil : $0 } ?? (home as NSString).appendingPathComponent(folder)
            return (base as NSString).appendingPathComponent("projects")
        }
    }

    /// A Qoder CLI user location, a config folder or its `projects` folder, read as that `projects` folder.
    static func cliProjectsFolder(_ url: URL) -> URL {
        let folder = URL(fileURLWithPath: TranscriptDiscovery.normalizedRoot(url.path), isDirectory: true)
        return folder.lastPathComponent == "projects" ? folder : folder.appendingPathComponent("projects", isDirectory: true)
    }
}
