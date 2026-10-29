import Foundation

/// The performance score's dump rows in `reference.py`'s format (METRICS 6.9 *Dumps*): compact JSON objects with the
/// reference's field names, numbers written to round-trip (`compare_dumps.py` compares them within 1e-9).
public enum PerfDump {
    /// One agent of a dump: the parser's phase (before the staleness rule) and its resolved parent.
    public struct Agent: Sendable {
        public var id: String
        public var source: SourceKind
        public var phase: AgentPhase
        public var lastLineMs: Int64?
        public var parentId: String?
        public var actions: [ToolAction]

        public init(id: String, source: SourceKind, phase: AgentPhase, lastLineMs: Int64?, parentId: String?,
                    actions: [ToolAction]) {
            self.id = id
            self.source = source
            self.phase = phase
            self.lastLineMs = lastLineMs
            self.parentId = parentId
            self.actions = actions
        }
    }

    static let staleMs: Int64 = 300_000
    static let listedMs: Int64 = 600_000

    /// The `perf` rows of the agents with a sample response and the `perfSets` row at `nowMs`, as JSON lines.
    public static func todayRows(_ agents: [Agent], responses: [ResponseRecord], nowMs: Int64) -> [String] {
        var byAgent: [OrdinalKey: [ResponseRecord]] = [:]
        for response in responses { byAgent[OrdinalKey(response.agentId), default: []].append(response) }
        var lines: [String] = []
        var values: [OrdinalKey: PerfSetValue] = [:]
        for agent in agents {
            let evaluation = PerfEvaluation.evaluate(source: agent.source, responses: byAgent[OrdinalKey(agent.id)] ?? [],
                                                     actions: agent.actions, nowMs: nowMs)
            let phase = effectivePhase(agent, nowMs: nowMs)
            values[OrdinalKey(agent.id)] = evaluation.value(agentId: agent.id, lastLineMs: agent.lastLineMs,
                                                             idle: phase == .idle)
            guard evaluation.n > 0 else { continue }
            var row = Object()
            row.add("agentId", agent.id)
            row.add("source", agent.source.rawValue)
            row.add("phase", phase.rawValue)
            row.add("lastLineMs", agent.lastLineMs)
            row.add("parentId", agent.parentId)
            row.add("form", evaluation.value(agentId: agent.id, lastLineMs: nil, idle: phase == .idle).form.rawValue)
            row.add("score", evaluation.score)
            row.add("shown", evaluation.shown.map(Int64.init))
            row.add("provisional", evaluation.provisional)
            row.add("n", Int64(evaluation.n))
            row.add("tools", Int64(evaluation.tools))
            row.add("activeMs", evaluation.activeMs)
            row.add("spanStartMs", evaluation.spanStartMs)
            row.add("model", evaluation.model)
            row.add("components", raw: array(evaluation.components.map(component)))
            lines.append("{\"perf\":" + row.text + "}")
        }
        lines.append("{\"perfSets\":" + sets(agents, values: values, nowMs: nowMs) + "}")
        return lines
    }

    /// One `perf` row per history record (METRICS 6.11), in the records' order.
    public static func historyRows(_ records: [PerfDayRecord]) -> [String] {
        records.map { record in
            var row = Object()
            row.add("day", record.day)
            row.add("source", record.source.rawValue)
            row.add("model", record.model)
            let value = record.record
            row.add("n", Int64(value.n))
            row.add("outputSum", value.outputSum)
            row.add("durationMsSum", value.durationMsSum)
            row.add("latencyBins", raw: array(value.latencyBins.map(String.init)))
            row.add("reasonN", Int64(value.reasonN))
            row.add("reasonOutput", value.reasonOutput)
            row.add("reasonDurationMsSum", value.reasonDurationMsSum)
            row.add("cacheN", Int64(value.cacheN))
            row.add("cacheInput", value.cacheInput)
            row.add("cacheCached", value.cacheCached)
            row.add("toolN", Int64(value.toolN))
            row.add("toolLnSum", value.toolLnSum)
            row.add("toolFailed", Int64(value.toolFailed))
            row.add("activeMs", value.activeMs)
            return "{\"perf\":" + row.text + "}"
        }
    }

    // MARK: - Sets (METRICS 6.9 *Sets and groups*)

    static func effectivePhase(_ agent: Agent, nowMs: Int64) -> AgentPhase {
        if agent.phase != .idle, let last = agent.lastLineMs, nowMs - last > staleMs { return .idle }
        return agent.phase
    }

    /// The agents list (6.7 steps 1–4), the highest value of every listed set and of every board group.
    private static func sets(_ agents: [Agent], values: [OrdinalKey: PerfSetValue], nowMs: Int64) -> String {
        var byId: [OrdinalKey: Agent] = [:]
        for agent in agents where byId[OrdinalKey(agent.id)] == nil { byId[OrdinalKey(agent.id)] = agent }
        var chosen = Set<OrdinalKey>()
        for agent in agents where effectivePhase(agent, nowMs: nowMs) == .generating
            || agent.lastLineMs.map({ nowMs - $0 <= listedMs }) == true {
            chosen.insert(OrdinalKey(agent.id))
        }
        for id in Array(chosen) {
            var seen: Set<OrdinalKey> = [id]
            var current = byId[id]?.parentId.map(OrdinalKey.init)
            while let next = current, byId[next] != nil, !seen.contains(next) {
                chosen.insert(next)
                seen.insert(next)
                current = byId[next]?.parentId.map(OrdinalKey.init)
            }
        }
        let shown = chosen.compactMap { id -> (agent: Agent, parentId: String?)? in
            guard let agent = byId[id] else { return nil }
            return (agent, agent.parentId.flatMap { chosen.contains(OrdinalKey($0)) ? $0 : nil })
        }.sorted { AgentParents.ordinal($0.agent.id, precedes: $1.agent.id) }
        func value(_ members: [Agent]) -> PerfSetValue {
            PerfSetValue.highest(members.compactMap { values[OrdinalKey($0.id)] })
        }
        var sets = [setJSON(value(shown.map(\.agent)), sources: nil)]
        for source in SourceKind.allCases {
            let members = shown.map(\.agent).filter { $0.source == source }
            if !members.isEmpty { sets.append(setJSON(value(members), sources: [source])) }
        }
        let groups = PerfGroups.boardGroups(shown.map { ($0.agent.id, $0.parentId) })
            .sorted { AgentParents.ordinal($0.root, precedes: $1.root) }
        var groupsObject = Object()
        for group in groups {
            var object = valueObject(value(group.members.compactMap { byId[OrdinalKey($0)] }))
            object.add("members", raw: array(group.members.map(quote)))
            groupsObject.add(group.root, raw: object.text)
        }
        var row = Object()
        row.add("listed", raw: array(shown.map { quote($0.agent.id) }))
        row.add("sets", raw: array(sets))
        row.add("groups", raw: groupsObject.text)
        return row.text
    }

    private static func valueObject(_ value: PerfSetValue) -> Object {
        var object = Object()
        object.add("form", value.form.rawValue)
        object.add("score", value.score)
        object.add("shown", value.shown.map(Int64.init))
        object.add("agent", value.agentId)
        return object
    }

    private static func setJSON(_ value: PerfSetValue, sources: [SourceKind]?) -> String {
        var object = Object()
        object.add("sources", raw: sources.map { array($0.map { quote($0.rawValue) }) } ?? "null")
        object.add("form", value.form.rawValue)
        object.add("score", value.score)
        object.add("shown", value.shown.map(Int64.init))
        object.add("agent", value.agentId)
        return object.text
    }

    private static func component(_ component: PerfComponent) -> String {
        var object = Object()
        object.add("id", component.id.rawValue)
        object.add("present", component.isPresent)
        object.add("reason", component.reason?.rawValue)
        object.add("samples", Int64(component.samples))
        object.add("measure", component.measure)
        object.add("ratio", component.ratio)
        object.add("subScore", component.subScore)
        return object.text
    }

    // MARK: - JSON

    /// One object with members in insertion order.
    struct Object {
        private var parts: [String] = []

        mutating func add(_ key: String, raw: String) { parts.append(PerfDump.quote(key) + ":" + raw) }
        mutating func add(_ key: String, _ value: String?) { add(key, raw: value.map(PerfDump.quote) ?? "null") }
        mutating func add(_ key: String, _ value: Int64?) { add(key, raw: value.map { String($0) } ?? "null") }
        mutating func add(_ key: String, _ value: Double?) { add(key, raw: value.map(PerfDump.number) ?? "null") }
        mutating func add(_ key: String, _ value: Bool) { add(key, raw: value ? "true" : "false") }

        var text: String { "{" + parts.joined(separator: ",") + "}" }
    }

    static func array(_ items: [String]) -> String { "[" + items.joined(separator: ",") + "]" }

    /// The shortest text that reads back, as Python's `repr` writes a float.
    static func number(_ value: Double) -> String { value.isFinite ? value.description : "null" }

    static func quote(_ text: String) -> String {
        var result = "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": result += "\\\""
            case "\\": result += "\\\\"
            case "\n": result += "\\n"
            case "\r": result += "\\r"
            case "\t": result += "\\t"
            case _ where scalar.value < 0x20: result += String(format: "\\u%04x", scalar.value)
            default: result.unicodeScalars.append(scalar)
            }
        }
        return result + "\""
    }
}
