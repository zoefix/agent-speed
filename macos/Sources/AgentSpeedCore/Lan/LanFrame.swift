import Foundation

/// Agent counts of METRICS 11.2 (`live.agents`), named `sessions` in the frame (LAN 3.1).
public struct LanSessions: Equatable, Sendable {
    public var active: Int
    public var generating: Int
    public var tools: Int

    public init(active: Int = 0, generating: Int = 0, tools: Int = 0) {
        self.active = active
        self.generating = generating
        self.tools = tools
    }
}

/// One set of unrounded live values: overall or of one source.
public struct LanValues: Equatable, Sendable {
    public var outputTokS: Double
    public var estimatedShare: Double
    public var sessions: LanSessions

    public init(outputTokS: Double = 0, estimatedShare: Double = 0, sessions: LanSessions = LanSessions()) {
        self.outputTokS = outputTokS
        self.estimatedShare = estimatedShare
        self.sessions = sessions
    }
}

/// The tick's live values (METRICS 11.2): every enabled source on every host, never an orb's subset.
public struct LanLiveValues: Equatable, Sendable {
    public var overall: LanValues
    /// Enabled sources only.
    public var sources: [SourceKind: LanValues]

    public init(overall: LanValues = LanValues(), sources: [SourceKind: LanValues] = [:]) {
        self.overall = overall
        self.sources = sources
    }

    /// The same values the report carries, from its sample.
    public init(sample: ReportSample) {
        var sources: [SourceKind: LanValues] = [:]
        var total = LanSessions()
        for counts in sample.facts.agents.values {
            total.active += counts.active
            total.generating += counts.generating
            total.tools += counts.tools
        }
        for (kind, source) in sample.sources {
            let counts = sample.facts.agents[kind] ?? .zero
            let share = source.live.output > 0 ? source.liveEstimated.output / source.live.output : 0
            sources[kind] = LanValues(outputTokS: source.live.output, estimatedShare: share,
                                      sessions: LanSessions(active: counts.active, generating: counts.generating,
                                                            tools: counts.tools))
        }
        self.init(overall: LanValues(outputTokS: sample.live.output, estimatedShare: sample.estimatedShare,
                                     sessions: total),
                  sources: sources)
    }
}

/// Who sends the frame: the settings instance, the computer's name and the launch's random run id (LAN 3.1).
public struct LanIdentity: Equatable, Sendable {
    public var instance: String
    public var name: String
    public var run: String

    public init(instance: String, name: String, run: String) {
        self.instance = instance
        self.name = LanFrame.computerName(name)
        self.run = run
    }

    /// 16 lowercase hex digits from the cryptographic generator, new at every launch.
    public static func newRun() -> String {
        var generator = SystemRandomNumberGenerator()
        return String(format: "%016llx", generator.next() as UInt64)
    }
}

/// The frame of LAN 3: built from the tick's live values, written byte-exact as reports write their values.
public enum LanFrame {
    public static let schema = 1
    public static let historyStepMs = 1000
    public static let byteLimit = 16_384
    static let defaultName = "Agent Speed"

    /// The frame as an ordered document; `history` holds rounded samples, oldest first.
    public static func build(identity: LanIdentity, seq: Int64, atMs: Int64, live: LanLiveValues,
                             history: [Double]) -> ReportJSON {
        let overall = values(live.overall)
        var sources: [ReportJSON] = []
        for kind in SourceKind.allCases {
            guard let given = live.sources[kind] else { continue }
            let entry = values(given)
            guard entry.sessions.active > 0 || entry.outputTokS > 0 else { continue }
            sources.append(.object([ReportJSON.Member("source", .string(kind.rawValue))] + entry.members))
        }
        return .object([
            .init("schema", .int(schema)),
            .init("type", .string("live")),
            .init("instance", .string(identity.instance)),
            .init("name", .string(identity.name)),
            .init("run", .string(identity.run)),
            .init("seq", .int(seq)),
            .init("at", .string(ReportInstant.format(unixMs: atMs))),
        ] + overall.members.prefix(3) + [
            .init("estimated", .bool(overall.estimatedShare > 0)),
            overall.members[3],
            .init("sources", .array(sources)),
            .init("history", .object([
                "stepMs": .int(historyStepMs),
                "outputTokS": .array(history.map { .decimal($0, decimals: 1) }),
            ])),
        ])
    }

    /// A frame is changed when any member other than `seq` and `at` differs from the previous one (LAN 3.3).
    public static func changed(_ previous: ReportJSON?, _ frame: ReportJSON) -> Bool {
        guard let previous else { return true }
        return stripped(previous) != stripped(frame)
    }

    /// LAN 3.1: trimmed, cut to 64 scalars, trimmed again; empty becomes `Agent Speed`.
    public static func computerName(_ raw: String?) -> String {
        var scalars = String.UnicodeScalarView()
        let trimmed = (raw ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        scalars.append(contentsOf: trimmed.unicodeScalars.prefix(LanDocument.nameLimit))
        let name = String(scalars).trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? defaultName : name
    }

    /// `generating`, else `tools`, else `idle`.
    public static func phase(_ sessions: LanSessions) -> String {
        sessions.generating > 0 ? "generating" : sessions.tools > 0 ? "tools" : "idle"
    }

    /// Tok/s rounded to 0.1 and clamped as reports do.
    public static func tokS(_ value: Double) -> Double {
        ReportBody.rounded(min(max(value.isFinite ? value : 0, 0), ReportBody.maxTokS), 1)
    }

    static func share(_ value: Double) -> Double {
        ReportBody.rounded(min(max(value.isFinite ? value : 0, 0), 1), 3)
    }

    static func count(_ value: Int) -> Int { min(max(value, 0), ReportBody.maxCount) }

    private struct Rounded {
        var outputTokS: Double
        var estimatedShare: Double
        var sessions: LanSessions

        /// `phase`, `outputTokS`, `estimatedShare`, `sessions`.
        var members: [ReportJSON.Member] {
            [
                .init("phase", .string(LanFrame.phase(sessions))),
                .init("outputTokS", .decimal(outputTokS, decimals: 1)),
                .init("estimatedShare", .decimal(estimatedShare, decimals: 3)),
                .init("sessions", .object([
                    "active": .int(sessions.active), "generating": .int(sessions.generating),
                    "tools": .int(sessions.tools),
                ])),
            ]
        }
    }

    private static func values(_ given: LanValues) -> Rounded {
        Rounded(outputTokS: tokS(given.outputTokS), estimatedShare: share(given.estimatedShare),
                sessions: LanSessions(active: count(given.sessions.active),
                                      generating: count(given.sessions.generating),
                                      tools: count(given.sessions.tools)))
    }

    private static func stripped(_ frame: ReportJSON) -> ReportJSON {
        guard case .object(let members) = frame else { return frame }
        return .object(members.filter { $0.key != "seq" && $0.key != "at" })
    }
}

/// LAN 3.4: one sample of the rounded output speed per second on the monotonic clock, the newest 60.
public struct LanHistory: Equatable, Sendable {
    public static let length = 60
    public static let restartMs: Int64 = 60_000

    private var start: Int64?
    private var next: Int64 = 0
    private var lastSampleMs: Int64 = 0
    public private(set) var samples: [Double] = []

    public init() {}

    /// Records the tick at monotonic `nowMs` and returns the samples.
    @discardableResult
    public mutating func tick(nowMs: Int64, outputTokS: Double) -> [Double] {
        let value = LanFrame.tokS(outputTokS)
        guard let start, nowMs - lastSampleMs < Self.restartMs else {
            start = nowMs
            next = 1
            lastSampleMs = nowMs
            samples = [value]
            return samples
        }
        while nowMs >= start + next * Int64(LanFrame.historyStepMs) {
            samples.append(value)
            next += 1
            lastSampleMs = nowMs
        }
        if samples.count > Self.length { samples.removeFirst(samples.count - Self.length) }
        return samples
    }
}
