import Foundation

enum OpenBitFunPaths {
    static var defaultRoot: URL {
        let environment = ProcessInfo.processInfo.environment
        let override = [environment["OPENBITFUN_HOME"], environment["OPENBITFUN_E2E_HOME"]]
            .compactMap { $0 }.first { !$0.isEmpty }
        let home = override.map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".openbitfun")
        return home.appendingPathComponent("projects")
    }

    static func projects(_ url: URL) -> URL {
        url.lastPathComponent == "projects" ? url : url.appendingPathComponent("projects")
    }

    static func documents(_ path: String, includeTraces: Bool) -> [(path: String, stat: FileStat)] {
        let session = URL(fileURLWithPath: path).deletingLastPathComponent()
        var folders = [(session.appendingPathComponent("turns"), "turn-")]
        if includeTraces {
            folders.append((session.appendingPathComponent("request-traces"), "request-"))
            let shared = session.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("request-traces")
            if FileStat.lstat(shared.path)?.isDirectory == true {
                folders.append((shared.appendingPathComponent(session.lastPathComponent), "request-"))
            }
        }
        return folders.flatMap { folder, prefix -> [(path: String, stat: FileStat)] in
            guard FileStat.lstat(folder.path)?.isDirectory == true,
                  let names = try? FileManager.default.contentsOfDirectory(atPath: folder.path) else { return [] }
            return names.compactMap { name in
                guard name.hasPrefix(prefix), name.hasSuffix(".json") else { return nil }
                let path = folder.path + "/" + name
                guard let stat = FileStat.lstat(path), stat.isRegularFile else { return nil }
                return (path, stat)
            }
        }.sorted { $0.path < $1.path }
    }

    static func usage(_ path: String, ledger: OpenBitFunUsageLedger = .shared) -> OpenBitFunUsageLedger.Snapshot {
        ledger.snapshot(for: URL(fileURLWithPath: path).deletingLastPathComponent().lastPathComponent)
    }

    static func stat(_ path: String) -> FileStat? {
        guard let metadata = FileStat.lstat(path), metadata.isRegularFile else { return nil }
        let usage = usage(path)
        return combined(metadata, documents: documents(path, includeTraces: usage.rounds.isEmpty), usage: usage)
    }

    static func combined(_ metadata: FileStat, documents: [(path: String, stat: FileStat)],
                         usage: OpenBitFunUsageLedger.Snapshot) -> FileStat {
        var result = metadata
        result.contentSignature = usage.signature
        result.modifiedNs = max(result.modifiedNs, usage.modifiedNs)
        for (_, stat) in documents {
            result.size += stat.size
            result.modifiedNs = max(result.modifiedNs, stat.modifiedNs)
        }
        return result
    }

    static func metadata(forChanged path: String) -> String {
        let folder = URL(fileURLWithPath: path).deletingLastPathComponent()
        if ["request-traces", "turns"].contains(folder.lastPathComponent) {
            return folder.deletingLastPathComponent().appendingPathComponent("metadata.json").path
        }
        if folder.deletingLastPathComponent().lastPathComponent == "request-traces" {
            return folder.deletingLastPathComponent().deletingLastPathComponent()
                .appendingPathComponent("sessions").appendingPathComponent(folder.lastPathComponent)
                .appendingPathComponent("metadata.json").path
        }
        return path
    }
}

/// A session's metadata and turn files change independently of its shared daily usage records.
struct OpenBitFunReader: Sendable {
    let path: String
    private let ledger: OpenBitFunUsageLedger
    private var known: [String: FileStat] = [:]
    private var previous = OpenBitFunUsageLedger.Snapshot.empty
    private var baseline: Set<OpenBitFunRoundID> = []

    init(path: String, start: FileTailer.Start, ledger: OpenBitFunUsageLedger = .shared) {
        self.path = path
        self.ledger = ledger
        if start != .beginning {
            let usage = OpenBitFunPaths.usage(path, ledger: ledger)
            baseline = Set(usage.rounds.keys)
            for (document, stat) in OpenBitFunPaths.documents(path, includeTraces: usage.rounds.isEmpty) {
                known[document] = stat
            }
        }
    }

    mutating func read() -> (OpenBitFunBatch, FileTailer.ReadResult) {
        var batch = OpenBitFunBatch()
        guard let metadata = FileStat.lstat(path), metadata.isRegularFile else { return (batch, .init(failed: true)) }
        let usage = OpenBitFunPaths.usage(path, ledger: ledger)
        let documents = OpenBitFunPaths.documents(path, includeTraces: usage.rounds.isEmpty)
        var result = FileTailer.ReadResult()
        result.stat = OpenBitFunPaths.combined(metadata, documents: documents, usage: usage)
        if previous !== usage {
            batch.usage = usage
            previous = usage
            result.bytesRead = 1  // A ledger-only update must also reach the live parser.
        }
        batch.baseline = baseline
        baseline.removeAll()
        var present: Set<String> = []
        for (document, stat) in [(path, metadata)] + documents {
            present.insert(document)
            guard known[document] != stat else { continue }
            if document != path && stat.size > DocumentReader.defaultMaxBytes {
                known[document] = stat
                continue
            }
            guard let bytes = HarnessMetadata.bytes(document, maxBytes: DocumentReader.defaultMaxBytes) else {
                result.failed = true
                continue
            }
            batch.lines.append(bytes.withUnsafeBytes(OpenBitFunLine.extract))
            result.bytesRead += bytes.count
            known[document] = stat
        }
        known = known.filter { present.contains($0.key) }
        return (batch, result)
    }
}
