import Foundation

final class DevinSource {
    struct Update {
        var upserts: [(ResponseUpsert, String)] = []
        var agents: [String: AgentState] = [:]
        var removedAgents: [String] = []
        var removedResponseKeys: [String] = []
        var isEmpty: Bool {
            upserts.isEmpty && agents.isEmpty && removedAgents.isEmpty && removedResponseKeys.isEmpty
        }
    }

    private struct Stamp: Equatable {
        var database: FileStat?
        var wal: FileStat?
        var desktop: [String: FileStat]
    }

    let location: DevinLocation
    private let fileID: FileID
    private(set) var status: OpencodeSource.Status = .missing
    private var sessions: [String: DevinSession] = [:]
    private var states: [String: AgentState] = [:]
    private var parsers: [String: DevinDesktopParser] = [:]
    private var readers: [String: TranscriptReader] = [:]
    private var emitted: [String: ResponseRecord] = [:]
    private var usedCliSessions: Set<String> = []
    private var duplicateAgents: Set<String> = []
    private var lastRowId: Int64 = 0
    private var sinceMs: Int64 = 0
    private var lastStamp: Stamp?
    private var databaseIdentity: FileIdentity?
    private var needsDatabaseRead = true

    init(location: DevinLocation, fileID: FileID) {
        self.location = location
        self.fileID = fileID
    }

    var exists: Bool { location.exists }
    var isReady: Bool { exists && status != .unsupported && status != .unreadable }
    var displayPath: String { location.displayPath }
    var malformedRows: Int { states.values.reduce(0) { $0 + $1.malformedLines } }

    var watchedDirectories: [String] {
        var result = location.desktopFolders.filter { TranscriptDiscovery.isAvailable(root: $0) }
        if let path = location.databasePath, FileStat.stat(path)?.isRegularFile == true {
            result.append((path as NSString).deletingLastPathComponent)
        }
        return result
    }

    func matches(canonicalPath path: String) -> Bool {
        if let database = location.databasePath, path == database || path == database + "-wal" { return true }
        return location.desktopFolders.contains { folder in
            path.hasPrefix(folder.hasSuffix("/") ? folder : folder + "/")
        }
    }

    @discardableResult
    func refreshAvailability() -> Bool {
        let available = exists
        let changed = available != (status != .missing)
        if available, status == .missing {
            status = .ready
            if let path = location.databasePath, FileStat.stat(path)?.isRegularFile == true {
                do {
                    _ = try DevinDatabase(path: path)
                } catch let error as SQLiteError {
                    status = error.code & 0xFF == 1 ? .unsupported : .unreadable
                } catch {
                    status = .unreadable
                }
            }
        }
        if !available { status = .missing }
        return changed
    }

    func seed(todayStartMs: Int64, deduper: inout Deduper) -> Update {
        sinceMs = max(0, todayStartMs - TranscriptSession.startupLookbackMs)
        lastRowId = 0
        needsDatabaseRead = true
        return refresh(force: true, deduper: &deduper)
    }

    func refreshIfChanged(deduper: inout Deduper) -> Update? {
        let stamp = currentStamp()
        guard needsDatabaseRead || stamp != lastStamp else { return nil }
        return refresh(deduper: &deduper)
    }

    func refresh(force: Bool = false, deduper: inout Deduper) -> Update {
        var update = Update()
        readDatabase(force: force, update: &update, deduper: &deduper)
        readDesktop(update: &update, deduper: &deduper)
        applyDesktopMetadata(update: &update)
        suppressMirrors(update: &update)
        var indexes: [String: Int] = [:]
        for (index, upsert) in update.upserts.enumerated() {
            if let previous = indexes[upsert.0.record.key] {
                update.upserts[previous].0.isCompletion = update.upserts[previous].0.isCompletion || upsert.0.isCompletion
                update.upserts[previous].0.record = upsert.0.record
            } else {
                indexes[upsert.0.record.key] = index
            }
        }
        update.upserts = indexes.values.sorted().map { update.upserts[$0] }
        update.agents = states.filter { id, state in
            !duplicateAgents.contains(id) && state.lastLineMs != nil
        }
        lastStamp = currentStamp()
        return update
    }

    private func applyDesktopMetadata(update: inout Update) {
        for (id, parser) in parsers {
            guard let title = parser.title else { continue }
            let matching = sessions.values.filter { $0.title?.trimmingCharacters(in: .whitespacesAndNewlines) == title }
            guard matching.count == 1, let session = matching.first else { continue }
            var state = states[id] ?? parser.state
            if state.project == nil, let cwd = session.workingDirectory {
                state.project = OmpParser.basename(cwd)
                state.cwdPath = cwd
            }
            let model = Self.sessionModel(session.model)
            if state.model == nil { state.model = model }
            states[id] = state
            guard let model else { continue }
            for (key, var record) in emitted where record.agentId == id && record.model == nil {
                record.model = model
                emitted[key] = record
            }
            for index in update.upserts.indices where update.upserts[index].0.record.agentId == id && update.upserts[index].0.record.model == nil {
                update.upserts[index].0.record.model = model
            }
        }
    }

    func activeSessionCount(todayStartMs: Int64) -> Int {
        let cli = sessions.values.filter { $0.lastActivityMs >= todayStartMs }.count
        let desktop = parsers.values.filter { $0.state.lastLineMs ?? 0 >= todayStartMs }.count
        let duplicates = parsers.values.filter { parser in
            guard parser.state.lastLineMs ?? 0 >= todayStartMs, let title = parser.title else { return false }
            let matching = sessions.values.filter { $0.title?.trimmingCharacters(in: .whitespacesAndNewlines) == title }
            return matching.count == 1 && matching[0].lastActivityMs >= todayStartMs
        }.count
        return max(0, cli + desktop - duplicates)
    }

    private func readDatabase(force: Bool, update: inout Update, deduper: inout Deduper) {
        guard let path = location.databasePath else {
            status = .missing
            return
        }
        guard let current = FileStat.stat(path), current.isRegularFile else {
            removeAgents(states.keys.filter { $0.hasPrefix(path + "#") }, update: &update)
            sessions = [:]
            usedCliSessions = []
            duplicateAgents = []
            status = .missing
            needsDatabaseRead = true
            return
        }
        if let previous = databaseIdentity, previous != current.identity {
            removeAgents(states.keys.filter { $0.hasPrefix(path + "#") }, update: &update)
            sessions = [:]
            usedCliSessions = []
            duplicateAgents = []
            lastRowId = 0
            needsDatabaseRead = true
        }
        databaseIdentity = current.identity
        let stamp = Stamp(database: current, wal: FileStat.stat(path + "-wal"), desktop: [:])
        if !force, !needsDatabaseRead, stamp.database == lastStamp?.database, stamp.wal == lastStamp?.wal { return }
        do {
            let database = try DevinDatabase(path: path)
            sessions = try database.sessions().reduce(into: [:]) { $0[$1.id] = $1 }
            for row in try database.messages(after: lastRowId, sinceMs: sinceMs) {
                lastRowId = max(lastRowId, row.rowId)
                guard let session = sessions[row.sessionId] else { continue }
                let id = path + "#" + row.sessionId
                var state = states[id] ?? AgentState(source: .devin)
                if state.project == nil, let cwd = session.workingDirectory {
                    state.project = OmpParser.basename(cwd)
                    state.cwdPath = cwd
                }
                if state.model == nil { state.model = Self.sessionModel(session.model) }
                state.noteLine(at: row.createdMs)
                if row.role == "invalid" {
                    state.malformedLines += 1
                    states[id] = state
                    continue
                }
                guard let data = row.metadata, let metadata = try? JSONParser.parse(data), metadata.object != nil else {
                    if row.role == "assistant" || row.role == "user" { state.malformedLines += 1 }
                    states[id] = state
                    continue
                }
                if row.role == "user" {
                    state.setPhase(.generating, at: row.createdMs, inFlightStartMs: row.createdMs)
                    states[id] = state
                    continue
                }
                guard row.role == "assistant" else {
                    states[id] = state
                    continue
                }
                state.setPhase(metadata["finish_reason"]?.string == "tool_calls" ? .tools : .idle, at: row.createdMs)
                states[id] = state
                guard let usage = metadata["metrics"], usage.object != nil else { continue }
                let tokens = DevinDesktopParser.cliTokens(usage)
                guard tokens.input > 0 || tokens.output > 0 else { continue }
                let model = Self.responseModel(metadata, session: session)
                state.model = model
                states[id] = state
                let request = metadata["request_id"]?.string?.trimmingCharacters(in: .whitespacesAndNewlines)
                let identity = request.flatMap { $0.isEmpty ? nil : "request:" + $0 } ?? "row:" + String(row.rowId)
                let key = "devin:" + row.sessionId + ":" + identity
                let start = DevinDesktopParser.duration(usage).map { row.createdMs - $0 }
                emit(ResponseRecord(key: key, source: .devin, agentId: id, model: model, startMs: start,
                                    endMs: row.createdMs, usage: tokens), cliSession: row.sessionId,
                     update: &update, deduper: &deduper)
            }
            status = .ready
            needsDatabaseRead = false
        } catch let error as SQLiteError {
            status = error.code & 0xFF == 1 ? .unsupported : .unreadable
        } catch {
            status = .unreadable
        }
    }

    private func readDesktop(update: inout Update, deduper: inout Deduper) {
        let files = DevinPaths.desktopFiles(in: location.desktopFolders)
        let paths = Set(files.map(\.path))
        for id in parsers.keys where !paths.contains(id) {
            readers.removeValue(forKey: id)
            parsers.removeValue(forKey: id)
            removeAgents([id], update: &update)
        }
        for file in files {
            if readers[file.path] == nil {
                readers[file.path] = TranscriptReader(path: file.path, start: sinceMs > 0 && file.stat.modifiedMs < sinceMs ? .end(file.stat.size) : .beginning)
                parsers[file.path] = DevinDesktopParser(agentId: file.path)
            }
            guard var reader = readers[file.path], var parser = parsers[file.path] else { continue }
            var records: [ResponseRecord] = []
            var line = 0
            _ = reader.read(onReset: {
                parser.reset()
                line = 0
            }, onMalformed: {
                parser.noteMalformedLine()
            }, onLine: { data in
                line += 1
                records.append(contentsOf: parser.consume(Data(data), lineNumber: line))
            })
            readers[file.path] = reader
            parsers[file.path] = parser
            states[file.path] = parser.state
            for record in records { emit(record, cliSession: nil, update: &update, deduper: &deduper) }
        }
    }

    private func emit(_ record: ResponseRecord, cliSession: String?, update: inout Update, deduper: inout Deduper) {
        if let parser = parsers[record.agentId], isDuplicate(parser) { return }
        guard emitted[record.key] != record, deduper.claim(record.key, for: fileID) != .otherFile else { return }
        let completion = emitted[record.key] == nil
        emitted[record.key] = record
        if let cliSession { usedCliSessions.insert(cliSession) }
        update.upserts.append((ResponseUpsert(record: record, isCompletion: completion), states[record.agentId]?.project ?? record.agentId))
    }

    private func suppressMirrors(update: inout Update) {
        for (id, parser) in parsers where isDuplicate(parser) {
            duplicateAgents.insert(id)
            update.removedAgents.append(id)
            removeResponses(agentId: id, update: &update)
        }
    }

    private func isDuplicate(_ parser: DevinDesktopParser) -> Bool {
        guard let title = parser.title else { return false }
        let matching = sessions.values.filter { $0.title?.trimmingCharacters(in: .whitespacesAndNewlines) == title }
        guard matching.count == 1, let session = matching.first else { return false }
        return usedCliSessions.contains(session.id)
    }

    private func removeAgents<S: Sequence>(_ ids: S, update: inout Update) where S.Element == String {
        for id in ids {
            states.removeValue(forKey: id)
            duplicateAgents.remove(id)
            update.removedAgents.append(id)
            removeResponses(agentId: id, update: &update)
        }
    }

    private func removeResponses(agentId: String, update: inout Update) {
        let keys = emitted.filter { $0.value.agentId == agentId }.map(\.key)
        for key in keys {
            emitted.removeValue(forKey: key)
            update.removedResponseKeys.append(key)
        }
    }

    private func currentStamp() -> Stamp {
        Stamp(database: location.databasePath.flatMap(FileStat.stat),
              wal: location.databasePath.flatMap { FileStat.stat($0 + "-wal") },
              desktop: DevinPaths.desktopFiles(in: location.desktopFolders).reduce(into: [:]) { $0[$1.path] = $1.stat })
    }

    private static func sessionModel(_ value: String?) -> String? {
        guard let model = value?.trimmingCharacters(in: .whitespacesAndNewlines), !model.isEmpty,
              model.lowercased() != "adaptive" else { return nil }
        return model
    }

    private static func responseModel(_ metadata: JSONValue, session: DevinSession) -> String? {
        if let model = metadata["generation_model"]?.string?.trimmingCharacters(in: .whitespacesAndNewlines), !model.isEmpty {
            return model
        }
        return sessionModel(session.model)
    }
}
