import Foundation

/// Which family of transcript file a file belongs to. A new file-based source adds a case here, a line extractor and
/// parser (`DecodedLine`, `TranscriptParser`, `TranscriptDecoder`) and its location (`TranscriptLocations`).
enum TranscriptFamily: String, CaseIterable, Sendable {
    case codex
    case claude
    /// DeepSeek Harness (dsh).
    case dsh
    /// Gemini CLI.
    case gemini
    /// omp (oh-my-pi).
    case omp
    /// Antigravity: one SQLite database per conversation, read as a whole unit per change (METRICS 4.8, 5.4).
    case antigravity
    /// WorkBuddy (China edition) and WorkBuddy AI (international edition): one format, one root each (METRICS 4.9).
    case workbuddy
    case workbuddyAi
    case kimi
    case grokBuild
    case pi
    /// Factory Droid: a session transcript read with its settings file (METRICS 4.16).
    case droid

    /// Nil for sources that are not read from transcript files: OpenCode, ZCode, Trae and OpenClaw keep databases
    /// (`OpencodeSource`, `ZcodeSource`, `TraeSource`, `OpenClawSource`); Hermes Agent's log sets share one state per folder
    /// (`HermesSource`).
    init?(_ source: SourceKind) {
        switch source {
        case .codex: self = .codex
        case .claudeCode, .claudeDesktop: self = .claude
        case .deepseek: self = .dsh
        case .gemini: self = .gemini
        case .omp: self = .omp
        case .antigravity: self = .antigravity
        case .workbuddy: self = .workbuddy
        case .workbuddyAi: self = .workbuddyAi
        case .kimi: self = .kimi
        case .grokBuild: self = .grokBuild
        case .pi: self = .pi
        case .droid: self = .droid
        case .opencode, .zcode, .openclaw, .hermes, .trae: return nil
        }
    }

    /// Sources whose transcripts belong to this family.
    var sources: [SourceKind] {
        switch self {
        case .codex: [.codex]
        case .claude: [.claudeCode, .claudeDesktop]
        case .dsh: [.deepseek]
        case .gemini: [.gemini]
        case .omp: [.omp]
        case .antigravity: [.antigravity]
        case .workbuddy: [.workbuddy]
        case .workbuddyAi: [.workbuddyAi]
        case .kimi: [.kimi]
        case .grokBuild: [.grokBuild]
        case .pi: [.pi]
        case .droid: [.droid]
        }
    }

    /// File names (and for Gemini CLI, omp, Antigravity, WorkBuddy and Pi, folder layout) of this family's transcripts
    /// (METRICS 2, 4.6 – 4.13).
    var names: TranscriptNames {
        switch self {
        case .codex: .codexSession
        case .claude: .jsonl
        case .dsh: .dshSession
        case .gemini: .geminiSession
        case .omp: .ompSession
        case .antigravity: .antigravityDatabase
        case .workbuddy, .workbuddyAi: .workbuddySession
        case .kimi: .kimiSession
        case .grokBuild: .grokBuildSession
        case .pi: .piSession
        case .droid: .droidSession
        }
    }

    /// An Antigravity database is one SQLite file plus its `-wal`; every other family is a single file.
    var isDatabase: Bool { self == .antigravity }

    /// The two WorkBuddy editions.
    var isWorkBuddy: Bool { self == .workbuddy || self == .workbuddyAi }

    /// The stat that identifies one transcript of this family; nil unless it is a regular file.
    func stat(_ path: String) -> FileStat? {
        if isDatabase { return FileStat.databaseStat(path) }
        if self == .grokBuild { return GrokBuildParser.stat(path) }
        if self == .droid { return DroidPaths.stat(path) }
        guard let stat = FileStat.lstat(path), stat.isRegularFile else { return nil }
        return stat
    }

    /// The transcript a changed path belongs to: an Antigravity `<id>.db-wal` stands for `<id>.db`, a Factory Droid
    /// `<id>.settings.json` for `<id>.jsonl`.
    func transcriptPath(forChanged path: String) -> String {
        if self == .droid { return DroidPaths.transcriptPath(ofSidecar: path) ?? path }
        if self == .grokBuild, (path as NSString).lastPathComponent == "summary.json" {
            return ((path as NSString).deletingLastPathComponent as NSString).appendingPathComponent("updates.jsonl")
        }
        guard isDatabase, path.hasSuffix(".db-wal") else { return path }
        return String(path.dropLast(4))
    }
}

enum DecodedLine: Sendable {
    case codex(LineExtraction<CodexLine>)
    case claude(LineExtraction<ClaudeLine>)
    case dsh(LineExtraction<DshLine>)
    case gemini(LineExtraction<GeminiLine>)
    case omp(LineExtraction<OmpLine>)
    case workbuddy(LineExtraction<WorkBuddyLine>)
    case kimi(LineExtraction<KimiLine>)
    case grokBuild(LineExtraction<GrokBuildLine>)
    case pi(LineExtraction<PiLine>)
    case droid(DroidLineRead)
}

/// Lines decoded ahead of time (off the state-machine thread) so seeding can extract in parallel. An Antigravity read
/// is one batch of database rows.
enum DecodedLines: Sendable {
    case codex([LineExtraction<CodexLine>])
    case claude([LineExtraction<ClaudeLine>])
    case dsh([LineExtraction<DshLine>])
    case gemini([LineExtraction<GeminiLine>])
    case omp([LineExtraction<OmpLine>])
    case antigravity(AntigravityBatch?)
    case workbuddy([LineExtraction<WorkBuddyLine>])
    case kimi([LineExtraction<KimiLine>])
    case grokBuild([LineExtraction<GrokBuildLine>])
    case pi([LineExtraction<PiLine>])
    /// With where the reader began (METRICS 4.16 Allocation).
    case droid([DroidLineRead], start: Int64?)
}

/// Whether a parser composes the task stream (METRICS 6.9), and how much of it it keeps.
enum TaskStreamMode: Sendable {
    case off
    /// The live engine: the current task only.
    case live
    /// The history analysis: every task, with the model notes of 6.11.
    case history
}

/// A family-agnostic per-file parser. Exactly one of the machines is set.
struct TranscriptParser: Sendable {
    let family: TranscriptFamily
    let fileID: FileID
    /// The per-file half of the task composer; seeding and history run it on the decoding threads.
    var taskReader: TaskLineReader?
    /// The agent's task stream (METRICS 6.9), for a scored source.
    private(set) var tasks: TaskComposer?
    private var notedModes: (modes: ExecutionModes, effort: String?)?
    private var codex: CodexParser?
    private var claude: ClaudeParser?
    private var dsh: DshParser?
    private var gemini: GeminiParser?
    private var omp: OmpParser?
    private var antigravity: AntigravityParser?
    private var workbuddy: WorkBuddyParser?
    private var kimi: KimiParser?
    private var grokBuild: GrokBuildParser?
    private var pi: PiParser?
    private var droid: DroidParser?

    /// `root` is the root folder the file was discovered under (omp and WorkBuddy derive `isSubagent` from it,
    /// METRICS 4.7, 4.9).
    init(family: TranscriptFamily, agentId: String, fileID: FileID, root: String? = nil, tasks mode: TaskStreamMode = .off) {
        self.family = family
        self.fileID = fileID
        if mode != .off, let source = family.sources.first, let reader = TaskLineReader(source: source) {
            taskReader = reader
            tasks = TaskComposer(source: reader.source, keepsEveryTask: mode == .history)
        }
        switch family {
        case .codex: codex = CodexParser(agentId: agentId, fileID: fileID)
        case .claude: claude = ClaudeParser(agentId: agentId, fileID: fileID)
        case .dsh: dsh = DshParser(agentId: agentId, fileID: fileID)
        case .gemini: gemini = GeminiParser(agentId: agentId, fileID: fileID)
        case .omp: omp = OmpParser(agentId: agentId, fileID: fileID, root: root)
        case .antigravity: antigravity = AntigravityParser(agentId: agentId, fileID: fileID)
        case .workbuddy, .workbuddyAi:
            workbuddy = WorkBuddyParser(source: family.sources[0], agentId: agentId, fileID: fileID, root: root)
        case .kimi: kimi = KimiParser(agentId: agentId, fileID: fileID)
        case .grokBuild: grokBuild = GrokBuildParser(agentId: agentId, fileID: fileID)
        case .pi: pi = PiParser(agentId: agentId, fileID: fileID)
        case .droid: droid = DroidParser(agentId: agentId, fileID: fileID)
        }
    }

    var state: AgentState {
        codex?.state ?? claude?.state ?? dsh?.state ?? gemini?.state ?? omp?.state ?? workbuddy?.state ?? kimi?.state ?? grokBuild?.state ?? pi?.state ?? droid?.state ?? antigravity!.state
    }

    /// An Antigravity database without the expected tables (`unsupportedFormat`).
    var isUnsupported: Bool { antigravity?.isUnsupported ?? false }

    /// Decodes one unit of input: a line, or a whole document for Gemini CLI legacy `.json` files. Antigravity
    /// databases are never fed bytes; their reads arrive as batches. `offset` is the line's position in the file
    /// (Factory Droid only).
    func decode(_ bytes: UnsafeRawBufferPointer, offset: Int64 = 0) -> DecodedLine? {
        switch family {
        case .codex: .codex(CodexLineExtractor.extract(bytes))
        case .claude: .claude(ClaudeLineExtractor.extract(bytes))
        case .dsh: .dsh(DshLineExtractor.extract(bytes))
        case .gemini:
            .gemini(gemini?.isDocument == true ? GeminiLineExtractor.extractDocument(bytes) : GeminiLineExtractor.extract(bytes))
        case .omp: .omp(OmpLineExtractor.extract(bytes))
        case .workbuddy, .workbuddyAi: .workbuddy(WorkBuddyLineExtractor.extract(bytes))
        case .antigravity: nil
        case .kimi: .kimi(KimiLineExtractor.extract(bytes))
        case .grokBuild: .grokBuild(GrokBuildLine.extract(bytes))
        case .pi: .pi(PiLineExtractor.extract(bytes))
        case .droid: .droid(DroidLineRead(extraction: DroidLineExtractor.extract(bytes), offset: offset, length: bytes.count))
        }
    }

    var hasPartialHistory: Bool { grokBuild?.partialHistory ?? false }

    mutating func refreshMetadata(deduper: inout Deduper, emit: (ResponseUpsert) -> Void) -> Bool {
        grokBuild?.refreshMetadata(deduper: &deduper, emit: emit) ?? false
    }

    /// Counts a unit of input that could not be decoded at all, such as a corrupt zstd frame (METRICS 5.1).
    mutating func noteMalformedLine() {
        codex?.noteMalformedLine()
        claude?.noteMalformedLine()
        dsh?.noteMalformedLine()
        gemini?.noteMalformedLine()
        omp?.noteMalformedLine()
        antigravity?.noteMalformedLine()
        workbuddy?.noteMalformedLine()
        kimi?.noteMalformedLine()
        grokBuild?.noteMalformedLine()
        pi?.noteMalformedLine()
        droid?.noteMalformedLine()
    }

    /// `tools`: the line's tool calls and results, read only from live lines (METRICS 6.10).
    mutating func consume(_ line: DecodedLine, tools: [ToolEvent] = [], deduper: inout Deduper, emit: (ResponseUpsert) -> Void) {
        switch line {
        case .codex(let extraction): codex?.consume(extraction, tools: tools, deduper: &deduper, emit: emit)
        case .claude(let extraction): claude?.consume(extraction, tools: tools, deduper: &deduper, emit: emit)
        case .dsh(let extraction): dsh?.consume(extraction, deduper: &deduper, emit: emit)
        case .gemini(let extraction): gemini?.consume(extraction, deduper: &deduper, emit: emit)
        case .omp(let extraction): omp?.consume(extraction, tools: tools, deduper: &deduper, emit: emit)
        case .workbuddy(let extraction): workbuddy?.consume(extraction, deduper: &deduper, emit: emit)
        case .kimi(let extraction): kimi?.consume(extraction, deduper: &deduper, emit: emit)
        case .grokBuild(let extraction): grokBuild?.consume(extraction, deduper: &deduper, emit: emit)
        case .pi(let extraction): pi?.consume(extraction, tools: tools, deduper: &deduper, emit: emit)
        case .droid(let line): droid?.consume(line, tools: tools, deduper: &deduper)
        }
    }

    /// A live line: its responses and its tool calls. Seeding and history decode ahead and read no tools. `offset` is
    /// the line's position in the file (Factory Droid only).
    mutating func consume(
        rawLine bytes: UnsafeRawBufferPointer,
        offset: Int64 = 0,
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        guard let line = decode(bytes, offset: offset) else { return }
        let taskLine = taskReader?.read(bytes, line)
        consume(line, tools: ToolEventExtractor.events(family, bytes), deduper: &deduper, emit: emit)
        if let taskLine { feedTask(taskLine, deduper: deduper) }
    }

    /// Decoded lines, with the task lines the decoding thread prepared for them (`taskLines[i]` belongs to line `i`).
    mutating func consume(_ lines: DecodedLines, taskLines: [TaskLine] = [], deduper: inout Deduper,
                          emit: (ResponseUpsert) -> Void) {
        let composes = tasks != nil && !taskLines.isEmpty
        switch lines {
        case .codex(let extractions):
            for (index, extraction) in extractions.enumerated() {
                codex?.consume(extraction, deduper: &deduper, emit: emit)
                if composes, index < taskLines.count { feedTask(taskLines[index], deduper: deduper) }
            }
        case .claude(let extractions):
            for (index, extraction) in extractions.enumerated() {
                claude?.consume(extraction, deduper: &deduper, emit: emit)
                if composes, index < taskLines.count { feedTask(taskLines[index], deduper: deduper) }
            }
        case .dsh(let extractions):
            for extraction in extractions { dsh?.consume(extraction, deduper: &deduper, emit: emit) }
        case .gemini(let extractions):
            for extraction in extractions { gemini?.consume(extraction, deduper: &deduper, emit: emit) }
        case .omp(let extractions):
            for (index, extraction) in extractions.enumerated() {
                omp?.consume(extraction, deduper: &deduper, emit: emit)
                if composes, index < taskLines.count { feedTask(taskLines[index], deduper: deduper) }
            }
        case .antigravity(let batch):
            if let batch { antigravity?.consume(batch, deduper: &deduper, emit: emit) }
        case .workbuddy(let extractions):
            for extraction in extractions { workbuddy?.consume(extraction, deduper: &deduper, emit: emit) }
        case .kimi(let extractions):
            for extraction in extractions { kimi?.consume(extraction, deduper: &deduper, emit: emit) }
        case .grokBuild(let extractions):
            for extraction in extractions { grokBuild?.consume(extraction, deduper: &deduper, emit: emit) }
        case .pi(let extractions):
            for (index, extraction) in extractions.enumerated() {
                pi?.consume(extraction, deduper: &deduper, emit: emit)
                if composes, index < taskLines.count { feedTask(taskLines[index], deduper: deduper) }
            }
        case .droid(let lines, let start):
            droid?.noteReadStart(start)
            for (index, line) in lines.enumerated() {
                droid?.consume(line, deduper: &deduper)
                if composes, index < taskLines.count { feedTask(taskLines[index], deduper: deduper) }
            }
        }
    }

    // MARK: - Task stream (METRICS 6.9)

    /// Applies a line's task operations right after the line was parsed, then notes the model and effort after it: in
    /// history (6.11 *Task records*), and live while the report watch is on, which takes a completed task's model from
    /// them (METRICS 11.10; a watched task starts at a line read then, so earlier notes are never needed).
    private mutating func feedTask(_ line: TaskLine, deduper: Deduper) {
        guard tasks != nil else { return }
        let fileID = self.fileID
        guard let t = tasks!.feed(line, isDuplicate: { deduper.isOwnedByOtherFile($0, for: fileID) }),
              tasks!.keepsEveryTask || tasks!.watch != nil else { return }
        let state = self.state
        let effort: String?
        if let noted = notedModes, noted.modes == state.modes {
            effort = noted.effort
        } else {
            let attribution = state.modes.attribution(for: .effort)
            effort = attribution.isUnknown ? nil : attribution.mode
            notedModes = (state.modes, effort)
        }
        tasks!.note(t: t, model: state.model, effort: effort)
    }

    // MARK: - Live reporting (METRICS 11.10)

    /// The watch for the lines read next; nil while it is off or seeding runs.
    mutating func setTaskWatch(_ context: ReportTaskWatchContext?) {
        tasks?.watch = context
    }

    var hasWatchedTasks: Bool { tasks?.hasWatchedTasks ?? false }

    /// The agent has been quiet for 30 minutes: its watched tasks complete.
    mutating func completeWatchedTasks() {
        tasks?.completeWatchedTasks()
    }

    /// A break: every task is unwatched.
    mutating func clearTaskWatch() {
        tasks?.clearWatch()
    }

    mutating func takeTaskCompletions() -> [TaskCompletion] {
        tasks?.takeCompletions() ?? []
    }

    // MARK: - Factory Droid read cycles (METRICS 4.16)

    /// Where the reader began, for a live read of new lines.
    mutating func noteReadStart(_ offset: Int64?) {
        droid?.noteReadStart(offset)
    }

    /// After a read's lines: reads a changed sidecar and emits the calls its growth covers. True when anything changed.
    mutating func finishReadCycle(emit: (ResponseUpsert) -> Void) -> Bool {
        droid?.finishReadCycle(emit: emit) ?? false
    }

    /// A Factory Droid session whose transcript is read from its end takes its sidecar's counters as a baseline.
    mutating func takeBaseline(_ snapshot: DroidSidecar.Snapshot) {
        droid?.takeBaseline(snapshot)
    }

    /// History only: a Factory Droid session's counters left without a call.
    mutating func finishHistory(emit: (ResponseUpsert) -> Void) {
        droid?.finishHistory(emit: emit)
    }

    /// A Factory Droid sidecar that is not the version last looked at.
    var sidecarHasChanged: Bool { droid?.sidecarHasChanged ?? false }

    /// A Factory Droid session's counter growth not yet allocated (P).
    var droidPending: DroidCounters? { droid?.pending }

    /// The last read of a Factory Droid session could not tell its own counters: history does not store it.
    var droidOwnCountersUnknown: Bool { droid?.ownCountersUnknown ?? false }

    /// The parent sidecar a Factory Droid session last found unreadable, kept between history analyses.
    var droidUnreadableParent: DroidPaths.Signature? {
        get { droid?.unreadableParent }
        set { droid?.unreadableParent = newValue }
    }
}

/// Reads a whole transcript (from the reader's position) and decodes its lines, off the state-machine thread. With a
/// task reader (a scored source, METRICS 6.9) it also prepares each line's task line: `tasks[i]` belongs to line `i`.
enum TranscriptDecoder {
    struct Output: Sendable {
        var lines: DecodedLines
        var read: FileTailer.ReadResult
        /// Empty without a task reader.
        var tasks: [TaskLine] = []
    }

    static func decode(_ family: TranscriptFamily, reader: inout TranscriptReader) -> (lines: DecodedLines, read: FileTailer.ReadResult) {
        var none: TaskLineReader?
        let output = decode(family, reader: &reader, tasks: &none)
        return (output.lines, output.read)
    }

    static func decode(_ family: TranscriptFamily, reader: inout TranscriptReader, tasks: inout TaskLineReader?) -> Output {
        switch family {
        case .codex:
            return extract(CodexLineExtractor.extract, reader: &reader, tasks: &tasks, wrap: DecodedLines.codex) { task, bytes, line in
                task.codex(bytes, line: line)
            }
        case .claude:
            return extract(ClaudeLineExtractor.extract, reader: &reader, tasks: &tasks, wrap: DecodedLines.claude) { task, bytes, line in
                task.claude(bytes, line: line)
            }
        case .dsh:
            return extract(DshLineExtractor.extract, reader: &reader, tasks: &tasks, wrap: DecodedLines.dsh, task: nil)
        case .gemini:
            let extractor = reader.isDocument ? GeminiLineExtractor.extractDocument : GeminiLineExtractor.extract
            return extract(extractor, reader: &reader, tasks: &tasks, wrap: DecodedLines.gemini, task: nil)
        case .omp:
            return extract(OmpLineExtractor.extract, reader: &reader, tasks: &tasks, wrap: DecodedLines.omp) { task, bytes, _ in
                task.piFamily(bytes)
            }
        case .antigravity:
            let (batch, read) = reader.readDatabase()
            return Output(lines: .antigravity(batch), read: read)
        case .workbuddy, .workbuddyAi:
            return extract(WorkBuddyLineExtractor.extract, reader: &reader, tasks: &tasks, wrap: DecodedLines.workbuddy, task: nil)
        case .kimi:
            return extract(KimiLineExtractor.extract, reader: &reader, tasks: &tasks, wrap: DecodedLines.kimi, task: nil)
        case .grokBuild:
            return extract(GrokBuildLine.extract, reader: &reader, tasks: &tasks, wrap: DecodedLines.grokBuild, task: nil)
        case .pi:
            return extract(PiLineExtractor.extract, reader: &reader, tasks: &tasks, wrap: DecodedLines.pi) { task, bytes, _ in
                task.piFamily(bytes)
            }
        case .droid:
            var lines: [DroidLineRead] = []
            var taskLines: [TaskLine] = []
            let read = reader.read(onLineAt: { bytes, offset in
                let extraction = DroidLineExtractor.extract(bytes)
                if case .blank = extraction { return }
                lines.append(DroidLineRead(extraction: extraction, offset: offset, length: bytes.count))
                if tasks != nil { taskLines.append(extraction.isLine ? tasks!.droid(bytes) : .none) }
            })
            return Output(lines: .droid(lines, start: read.startOffset), read: read, tasks: taskLines)
        }
    }

    private static func extract<Line: Sendable>(
        _ extractor: (UnsafeRawBufferPointer) -> LineExtraction<Line>,
        reader: inout TranscriptReader,
        tasks: inout TaskLineReader?,
        wrap: ([LineExtraction<Line>]) -> DecodedLines,
        task: ((inout TaskLineReader, UnsafeRawBufferPointer, Line) -> TaskLine)?
    ) -> Output {
        var lines: [LineExtraction<Line>] = []
        var taskLines: [TaskLine] = []
        let composes = tasks != nil && task != nil
        let read = reader.read(onMalformed: {
            lines.append(.malformed)
            if composes { taskLines.append(.none) }
        }) { bytes in
            let extraction = extractor(bytes)
            if case .blank = extraction { return }
            lines.append(extraction)
            guard composes else { return }
            if case .line(let line) = extraction {
                taskLines.append(task!(&tasks!, bytes, line))
            } else {
                taskLines.append(.none)
            }
        }
        return Output(lines: wrap(lines), read: read, tasks: taskLines)
    }
}
