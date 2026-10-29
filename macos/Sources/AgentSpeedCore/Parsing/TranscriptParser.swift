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

    /// Nil for sources that are not read from transcript files: OpenCode, ZCode and OpenClaw keep databases
    /// (`OpencodeSource`, `ZcodeSource`, `OpenClawSource`); Hermes Agent's log sets share one state per folder
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
        case .opencode, .zcode, .openclaw, .hermes: return nil
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

/// A family-agnostic per-file parser. Exactly one of the machines is set.
struct TranscriptParser: Sendable {
    let family: TranscriptFamily
    let fileID: FileID
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
    init(family: TranscriptFamily, agentId: String, fileID: FileID, root: String? = nil) {
        self.family = family
        self.fileID = fileID
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

    /// `tools`: the line's tool calls and results (METRICS 6.10).
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

    /// A live line: its responses and its tool calls. `onToolResult` receives the calls whose first result it holds
    /// (METRICS 6.10), after its responses. `offset` is the line's position in the file (Factory Droid only).
    mutating func consume(
        rawLine bytes: UnsafeRawBufferPointer,
        offset: Int64 = 0,
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void,
        onToolResult: (ToolCompletion) -> Void = { _ in }
    ) {
        guard let line = decode(bytes, offset: offset) else { return }
        let tools = ToolEventExtractor.reads(family) ? ToolEventExtractor.events(family, bytes) : []
        consume(line, tools: tools, deduper: &deduper, emit: emit)
        drainToolCompletions(onToolResult)
    }

    /// Decoded lines with the tool events the decoding thread read for them (`tools[i]` belongs to line `i`; empty
    /// when the family has none). Completions follow each line's responses, in order.
    mutating func consume(
        _ lines: DecodedLines,
        tools: [[ToolEvent]] = [],
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void,
        onToolResult: (ToolCompletion) -> Void = { _ in }
    ) {
        func toolsOf(_ index: Int) -> [ToolEvent] { index < tools.count ? tools[index] : [] }
        switch lines {
        case .codex(let extractions):
            for (index, extraction) in extractions.enumerated() {
                codex?.consume(extraction, tools: toolsOf(index), deduper: &deduper, emit: emit)
                drainToolCompletions(onToolResult)
            }
        case .claude(let extractions):
            for (index, extraction) in extractions.enumerated() {
                claude?.consume(extraction, tools: toolsOf(index), deduper: &deduper, emit: emit)
                drainToolCompletions(onToolResult)
            }
        case .dsh(let extractions):
            for extraction in extractions { dsh?.consume(extraction, deduper: &deduper, emit: emit) }
        case .gemini(let extractions):
            for extraction in extractions { gemini?.consume(extraction, deduper: &deduper, emit: emit) }
        case .omp(let extractions):
            for (index, extraction) in extractions.enumerated() {
                omp?.consume(extraction, tools: toolsOf(index), deduper: &deduper, emit: emit)
                drainToolCompletions(onToolResult)
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
                pi?.consume(extraction, tools: toolsOf(index), deduper: &deduper, emit: emit)
                drainToolCompletions(onToolResult)
            }
        case .droid(let lines, let start):
            droid?.noteReadStart(start)
            for (index, line) in lines.enumerated() {
                droid?.consume(line, tools: toolsOf(index), deduper: &deduper)
                drainToolCompletions(onToolResult)
            }
        }
    }

    // MARK: - Actions (METRICS 6.10)

    /// Lines read while seeding add seeded actions; their results are not live.
    mutating func setSeeding(_ seeding: Bool) {
        codex?.setSeeding(seeding)
        claude?.setSeeding(seeding)
        omp?.setSeeding(seeding)
        pi?.setSeeding(seeding)
        droid?.setSeeding(seeding)
    }

    /// The history analysis keeps no stored actions, only the completions of whole files.
    mutating func keepCompletionsOnly() {
        codex?.keepCompletionsOnly()
        claude?.keepCompletionsOnly()
        omp?.keepCompletionsOnly()
        pi?.keepCompletionsOnly()
        droid?.keepCompletionsOnly()
    }

    private mutating func drainToolCompletions(_ body: (ToolCompletion) -> Void) {
        let completions = codex?.takeToolCompletions() ?? claude?.takeToolCompletions() ?? omp?.takeToolCompletions()
            ?? pi?.takeToolCompletions() ?? droid?.takeToolCompletions() ?? []
        for completion in completions { body(completion) }
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

/// Reads a whole transcript (from the reader's position) and decodes its lines, off the state-machine thread. With
/// `tools` it also reads each line's tool events without hashing their inputs (METRICS 6.10): `tools[i]` belongs to
/// line `i`.
enum TranscriptDecoder {
    struct Output: Sendable {
        var lines: DecodedLines
        var read: FileTailer.ReadResult
        /// Empty without `tools` or for a family without tool calls.
        var tools: [[ToolEvent]] = []
    }

    static func decode(_ family: TranscriptFamily, reader: inout TranscriptReader) -> (lines: DecodedLines, read: FileTailer.ReadResult) {
        let output = decode(family, reader: &reader, tools: false)
        return (output.lines, output.read)
    }

    static func decode(_ family: TranscriptFamily, reader: inout TranscriptReader, tools: Bool) -> Output {
        let readsTools = tools && ToolEventExtractor.reads(family)
        switch family {
        case .codex:
            return extract(CodexLineExtractor.extract, reader: &reader, family: family, tools: readsTools, wrap: DecodedLines.codex)
        case .claude:
            return extract(ClaudeLineExtractor.extract, reader: &reader, family: family, tools: readsTools, wrap: DecodedLines.claude)
        case .dsh:
            return extract(DshLineExtractor.extract, reader: &reader, family: family, tools: false, wrap: DecodedLines.dsh)
        case .gemini:
            let extractor = reader.isDocument ? GeminiLineExtractor.extractDocument : GeminiLineExtractor.extract
            return extract(extractor, reader: &reader, family: family, tools: false, wrap: DecodedLines.gemini)
        case .omp:
            return extract(OmpLineExtractor.extract, reader: &reader, family: family, tools: readsTools, wrap: DecodedLines.omp)
        case .antigravity:
            let (batch, read) = reader.readDatabase()
            return Output(lines: .antigravity(batch), read: read)
        case .workbuddy, .workbuddyAi:
            return extract(WorkBuddyLineExtractor.extract, reader: &reader, family: family, tools: false, wrap: DecodedLines.workbuddy)
        case .kimi:
            return extract(KimiLineExtractor.extract, reader: &reader, family: family, tools: false, wrap: DecodedLines.kimi)
        case .grokBuild:
            return extract(GrokBuildLine.extract, reader: &reader, family: family, tools: false, wrap: DecodedLines.grokBuild)
        case .pi:
            return extract(PiLineExtractor.extract, reader: &reader, family: family, tools: readsTools, wrap: DecodedLines.pi)
        case .droid:
            var lines: [DroidLineRead] = []
            var toolLines: [[ToolEvent]] = []
            let read = reader.read(onLineAt: { bytes, offset in
                let extraction = DroidLineExtractor.extract(bytes)
                if case .blank = extraction { return }
                lines.append(DroidLineRead(extraction: extraction, offset: offset, length: bytes.count))
                guard readsTools else { return }
                if case .line = extraction {
                    toolLines.append(ToolEventExtractor.events(.droid, bytes, hashes: false))
                } else {
                    toolLines.append([])
                }
            })
            return Output(lines: .droid(lines, start: read.startOffset), read: read, tools: toolLines)
        }
    }

    private static func extract<Line: Sendable>(
        _ extractor: (UnsafeRawBufferPointer) -> LineExtraction<Line>,
        reader: inout TranscriptReader,
        family: TranscriptFamily,
        tools: Bool,
        wrap: ([LineExtraction<Line>]) -> DecodedLines
    ) -> Output {
        var lines: [LineExtraction<Line>] = []
        var toolLines: [[ToolEvent]] = []
        let read = reader.read(onMalformed: {
            lines.append(.malformed)
            if tools { toolLines.append([]) }
        }) { bytes in
            let extraction = extractor(bytes)
            if case .blank = extraction { return }
            lines.append(extraction)
            guard tools else { return }
            if case .line = extraction {
                toolLines.append(ToolEventExtractor.events(family, bytes, hashes: false))
            } else {
                toolLines.append([])
            }
        }
        return Output(lines: wrap(lines), read: read, tools: toolLines)
    }
}
