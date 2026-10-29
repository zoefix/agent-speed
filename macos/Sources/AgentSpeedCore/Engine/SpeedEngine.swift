import Foundation

/// Pure, deterministic speed computation (METRICS 6). Every time-dependent call takes an explicit `nowMs`.
struct SpeedEngine: Sendable {
    static let liveWindowMs: Int64 = 3000
    static let recentWindowMs: Int64 = 10 * 60_000
    static let recentCount = 3
    static let fallbackWindowMs: Int64 = 30 * 60_000
    static let fallbackCount = 10
    /// Long enough for the 30-minute fallback windows; history and live only need 15 minutes.
    static let retentionMs: Int64 = fallbackWindowMs
    static let staleMs: Int64 = 300_000
    static let agentListWindowMs: Int64 = 600_000
    static let historySeconds = 300
    static let newPeakMinimum = 20.0
    static let newPeakMargin = 1.0
    static let newPeakCooldownMs: Int64 = 60_000
    /// Only responses that ended this recently announce their completion (METRICS 6.4).
    static let completionEventMaxAgeMs: Int64 = 120_000

    private(set) var agents: [String: EngineAgent] = [:]
    private var responses: [String: ResponseRecord] = [:]
    private var projects: [String: String] = [:]
    private var ledger: DayLedger?
    private var lastResponse: ResponseSummary?
    private var observedPeak = Rate.zero
    private var observedPeakAtMs: Int64?
    private var previousPeakOutput = 0.0
    private var lastNewPeakMs: Int64?
    /// Prices responses as they arrive (METRICS 9.3, 9.7); without it, records keep the cost they carry.
    private var pricing: PriceBook?
    /// The current table version the stored responses were last priced with.
    private var pricedVersion: Int?
    private var dayLabeler = DayLabeler(timeZone: .current)
    /// What live reporting counts (METRICS 11.4); fed whatever reporting does, so nothing depends on its settings.
    private var reportLedger = ReportLedger()
    /// The provider of each agent's latest response, for the model id of a generating agent (METRICS 11.2).
    private var agentProviders: [String: String] = [:]
    /// Completed watched tasks for live reporting (METRICS 11.10), fed whatever reporting does.
    private var taskCompletions = ReportTaskCompletionLog()

    init(pricing: PriceBook? = nil) {
        self.pricing = pricing
        pricedVersion = pricing?.currentVersion
    }

    // MARK: - Inputs

    mutating func setAgent(_ agent: EngineAgent) {
        agents[agent.id] = agent
    }

    mutating func removeAgent(id: String) {
        agents[id] = nil
    }

    /// Replaces the parent link of every agent (METRICS 6.7).
    mutating func setParents(_ parents: [String: String]) {
        for (id, agent) in agents where agent.parentId != parents[id] {
            agents[id]?.parentId = parents[id]
        }
    }

    /// Stores (or replaces) a response. Returns a `responseCompleted` event for live completions that ended within
    /// `completionEventMaxAgeMs` of `nowMs`; older ones (a whole-file source read late) update totals silently.
    /// Without `nowMs` the age is not checked.
    @discardableResult
    mutating func upsert(
        _ record: ResponseRecord,
        project: String,
        isCompletion: Bool,
        emitsEvents: Bool,
        nowMs: Int64? = nil
    ) -> SpeedEvent? {
        var record = record
        if let pricing {
            record.costMicroUsd = DayLabeler.hasDay(record.endMs)
                ? pricing.cost(of: record, day: dayLabeler.label(forMs: record.endMs)) : nil
        }
        let old = responses.updateValue(record, forKey: record.key)
        projects[record.key] = project
        ledger?.replace(old, with: record)
        if let provider = record.provider { agentProviders[record.agentId] = provider }
        let isLiveCompletion = isCompletion && emitsEvents && nowMs.map { record.endMs >= $0 - Self.completionEventMaxAgeMs } ?? false
        if isLiveCompletion {
            reportLedger.count(record, modelId: reportModelId(of: record))
        } else if reportLedger.isCounted(record.key) {
            reportLedger.grow(record, modelId: reportModelId(of: record))
        }
        let summary = Self.summary(of: record, project: project)
        if lastResponse.map({ summary.endedAt >= $0.endedAt }) ?? true {
            lastResponse = summary
        }
        guard isCompletion, emitsEvents else { return nil }
        if let nowMs, record.endMs < nowMs - Self.completionEventMaxAgeMs { return nil }
        return .responseCompleted(summary)
    }

    /// Responses currently held (today's and the retention window), unordered.
    var storedResponses: [ResponseRecord] { Array(responses.values) }

    // MARK: - Computation

    /// `subsets` are the source sets whose measured peak today is reported in `subsetPeaks` (METRICS 6.8).
    mutating func advance(nowMs: Int64, todayStartMs: Int64, emitsEvents: Bool, subsets: Set<SourceSet> = []) -> EngineOutput {
        rollOverIfNeeded(todayStartMs: todayStartMs)
        prune(nowMs: nowMs, todayStartMs: todayStartMs)
        repriceIfNeeded()

        let timed = recentTimedResponses(nowMs: nowMs)
        let measured = timed.filter(\.record.isFinal)
        let unmeasured = timed.filter { !$0.record.isFinal }
        let references = SpeedReferences(timed: referenceResponses(measured: measured, nowMs: nowMs), nowMs: nowMs)
        let window = LiveWindow(
            nowMs: nowMs, measured: measured, unmeasured: unmeasured, agents: agents,
            latestEnds: latestEndsByAgent(), references: references, providers: agentProviders
        )
        let history = Self.history(nowMs: nowMs, measured: measured, estimates: window.estimates)

        let live = window.total
        if live.output > observedPeak.output {
            observedPeak.output = live.output
            observedPeakAtMs = nowMs
        }
        observedPeak.total = max(observedPeak.total, live.total)

        let measuredPeak = ledger?.measuredPeak() ?? DayLedger.MeasuredPeak()
        let peak = Rate(
            output: max(measuredPeak.output, observedPeak.output),
            total: max(measuredPeak.total, observedPeak.total)
        )
        let peakAt = measuredPeak.output >= observedPeak.output ? measuredPeak.outputAtMs : observedPeakAtMs

        var events: [SpeedEvent] = []
        if emitsEvents,
           live.output >= Self.newPeakMinimum,
           live.output >= previousPeakOutput + Self.newPeakMargin,
           lastNewPeakMs.map({ nowMs - $0 >= Self.newPeakCooldownMs }) ?? true {
            events.append(.newPeak(live.output))
            lastNewPeakMs = nowMs
        }
        previousPeakOutput = peak.output

        let listing = agentList(nowMs: nowMs, window: window, references: references)
        var report = reportFacts(nowMs: nowMs)
        report.task = taskFacts(listing.agents, window: window, all: listing.intelligence)

        var subsetPeaks = [SourceSet: Double](minimumCapacity: subsets.count)
        for set in subsets {
            subsetPeaks[set] = ledger?.measuredPeakOutput(for: set) ?? 0
        }

        return EngineOutput(
            nowMs: nowMs,
            live: live,
            liveEstimatedShare: window.estimatedShare,
            sourceLive: window.bySource,
            sourceLiveEstimated: window.estimatedBySource,
            liveCost: window.cost,
            sourceLiveCost: window.costBySource,
            intelligence: listing.intelligence,
            sourceIntelligence: listing.sourceIntelligence,
            agents: listing.agents,
            history: history,
            today: ledger?.totals ?? TokenTotals(),
            todayBySource: ledger?.bySource ?? [:],
            averageToday: ledger?.average,
            unfinalizedToday: ledger?.unfinalized ?? 0,
            todayCost: ledger?.cost ?? .zero,
            todayCostBySource: ledger?.costBySource ?? [:],
            todayCostByModel: ledger?.costByModel ?? [:],
            todayUnpricedFacts: (ledger?.unpricedFacts ?? [:]).compactMapValues { tally in
                let facts = UnpricedFact.list(tally)
                return facts.isEmpty ? nil : facts
            },
            peakToday: peak,
            peakTodayAtMs: peak.output > 0 ? peakAt : nil,
            subsetPeaks: subsetPeaks,
            todayStartMs: todayStartMs,
            lastResponse: lastResponse,
            events: events,
            report: report
        )
    }

    // MARK: - Live reporting

    /// The current table: it names models for live reporting (METRICS 11.3) and classes them (6.9).
    private var currentTable: PriceTable { pricing?.current ?? PriceBook.bundled }

    private func reportModelId(of record: ResponseRecord) -> String {
        currentTable.reportModelId(model: record.model, provider: record.provider)
    }

    /// Agents of the 6.7 base set per source, generating agents per model, and the report ledger (METRICS 11.2).
    private func reportFacts(nowMs: Int64) -> ReportLiveFacts {
        var facts = ReportLiveFacts(counters: reportLedger.counters)
        let table = currentTable
        for agent in agents.values {
            let isFresh = agent.lastLineMs.map { nowMs - $0 <= Self.staleMs } ?? false
            let phase = isFresh ? agent.phase : .idle
            let isRecent = agent.lastLineMs.map { $0 >= nowMs - Self.agentListWindowMs } ?? false
            guard isRecent || phase == .generating else { continue }
            var counts = facts.agents[agent.source] ?? .zero
            counts.active += 1
            switch phase {
            case .generating:
                counts.generating += 1
                let id = table.reportModelId(model: agent.model, provider: agentProviders[agent.id])
                facts.generatingModels[ReportModelKey(modelId: id, source: agent.source), default: 0] += 1
            case .tools:
                counts.tools += 1
            case .idle:
                break
            }
            facts.agents[agent.source] = counts
        }
        return facts
    }

    /// The model sums of the live window at `nowMs` (METRICS 6.9 *Model sums*), as `advance` computes them.
    func liveModelSums(nowMs: Int64) -> [LiveModelKey: IntelligenceSums] {
        let timed = recentTimedResponses(nowMs: nowMs)
        let measured = timed.filter(\.record.isFinal)
        return LiveWindow(
            nowMs: nowMs, measured: measured, unmeasured: timed.filter { !$0.record.isFinal }, agents: agents,
            latestEnds: latestEndsByAgent(), references: SpeedReferences(timed: measured, nowMs: nowMs),
            providers: agentProviders
        ).intelligenceByModel
    }

    /// Counts a completed watched task (METRICS 11.10 *Counting*) under the resolved id of its model and its agent's
    /// source; a task without actions adds nothing.
    mutating func recordTaskCompletion(_ completion: TaskCompletion, agentId: String, source: SourceKind) {
        guard let row = ReportTaskRow(completion.record) else { return }
        let id = currentTable.reportModelId(model: completion.model, provider: agentProviders[agentId])
        taskCompletions.append(epoch: completion.epoch, modelId: id, source: source, row: row)
    }

    /// The task score of all enabled sources and of each resolved model id: the set value of the listed agents whose
    /// current model resolves to it, and the model sums of the raw models that do (METRICS 11.10 *Body*).
    private func taskFacts(_ listed: [EngineAgentStatus], window: LiveWindow, all: IntelligenceSums) -> ReportTaskFacts {
        var facts = ReportTaskFacts(value: all.score, thinking: all.thinking, completions: taskCompletions)
        let table = currentTable
        var resolved: [LiveModelKey: String] = [:]
        func id(_ key: LiveModelKey) -> String {
            if let id = resolved[key] { return id }
            let id = table.reportModelId(model: key.model, provider: key.provider)
            resolved[key] = id
            return id
        }
        for (key, sums) in window.intelligenceByModel where sums.thinking != 0 {
            facts.models[id(key), default: ReportTaskModel()].thinking += sums.thinking
        }
        for row in listed where row.intelligence.score.form != .none {
            let modelId = id(LiveModelKey(model: row.agent.model, provider: agentProviders[row.agent.id]))
            facts.models[modelId, default: ReportTaskModel()].value.merge(row.intelligence.score)
            if row.intelligence.score.form == .live { facts.models[modelId]?.liveSources.insert(row.agent.source) }
        }
        return facts
    }

    // MARK: - Day and retention

    /// A new current price table prices the responses no table could price before (METRICS 9.7); a day's bound
    /// table, and so every known cost, stays as it was.
    private mutating func repriceIfNeeded() {
        guard let pricing, pricing.currentVersion != pricedVersion else { return }
        pricedVersion = pricing.currentVersion
        for (key, record) in responses where record.costMicroUsd == nil {
            guard DayLabeler.hasDay(record.endMs),
                  let cost = pricing.cost(of: record, day: dayLabeler.label(forMs: record.endMs)) else { continue }
            var priced = record
            priced.costMicroUsd = cost
            responses[key] = priced
            ledger?.replace(record, with: priced)
            if reportLedger.isCounted(key) { reportLedger.grow(priced, modelId: reportModelId(of: priced)) }
        }
    }

    private mutating func rollOverIfNeeded(todayStartMs: Int64) {
        guard ledger?.dayStartMs != todayStartMs else { return }
        dayLabeler = DayLabeler(timeZone: .current)
        let isNewDay = ledger != nil
        var fresh = DayLedger(dayStartMs: todayStartMs)
        for record in responses.values where fresh.contains(record) {
            fresh.replace(nil, with: record)
        }
        ledger = fresh
        if isNewDay {
            observedPeak = .zero
            observedPeakAtMs = nil
            previousPeakOutput = 0
        }
    }

    private mutating func prune(nowMs: Int64, todayStartMs: Int64) {
        let cutoff = min(todayStartMs, nowMs - Self.retentionMs)
        guard responses.values.contains(where: { $0.endMs < cutoff }) else { return }
        responses = responses.filter { $0.value.endMs >= cutoff }
        projects = projects.filter { responses[$0.key] != nil }
        reportLedger.prune(before: cutoff)
        agentProviders = agentProviders.filter { agents[$0.key] != nil }
    }

    /// Latest end of any stored response (valid or not) per agent.
    private func latestEndsByAgent() -> [String: Int64] {
        var ends: [String: Int64] = [:]
        for record in responses.values {
            ends[record.agentId] = max(ends[record.agentId] ?? record.endMs, record.endMs)
        }
        return ends
    }

    /// Some sources persist turn totals only after tools finish. Independently completed model calls may seed
    /// estimates sooner, without inventing usage, completion events, measured history, or measured peaks.
    private mutating func referenceResponses(measured: [TimedResponse], nowMs: Int64) -> [TimedResponse] {
        var records = Dictionary(uniqueKeysWithValues: measured.map { ($0.record.key, $0) })
        for agent in agents.values where agent.phase != .idle {
            guard let lastLine = agent.lastLineMs, lastLine <= nowMs, nowMs - lastLine <= Self.staleMs else { continue }
            for var record in agent.referenceResponses where record.agentId == agent.id && record.source == agent.source
                && record.endMs <= nowMs && record.endMs >= nowMs - Self.fallbackWindowMs
                && record.isFinal && responses[record.key] == nil && records[record.key] == nil {
                if let pricing, DayLabeler.hasDay(record.endMs) {
                    record.costMicroUsd = pricing.cost(of: record, day: dayLabeler.label(forMs: record.endMs))
                }
                if let timed = TimedResponse(record) { records[record.key] = timed }
            }
        }
        return records.values.sorted {
            $0.endMs != $1.endMs ? $0.endMs > $1.endMs : $0.record.key < $1.record.key
        }
    }

    /// Valid responses ending within the retention window, most recent first.
    private func recentTimedResponses(nowMs: Int64) -> [TimedResponse] {
        let cutoff = nowMs - Self.retentionMs
        var timed: [TimedResponse] = []
        for record in responses.values where record.endMs >= cutoff {
            if let response = TimedResponse(record) { timed.append(response) }
        }
        timed.sort { lhs, rhs in
            lhs.endMs != rhs.endMs ? lhs.endMs > rhs.endMs : lhs.record.key < rhs.record.key
        }
        return timed
    }

    // MARK: - History

    private static func history(nowMs: Int64, measured timed: [TimedResponse], estimates: [InFlightEstimate]) -> [HistoryPoint] {
        let lastSecond = ResponseTiming.second(of: nowMs)
        let firstSecond = lastSecond - Int64(historySeconds) + 1
        var measured = Array(repeating: Rate.zero, count: historySeconds)
        var estimated = Array(repeating: Rate.zero, count: historySeconds)
        for response in timed where response.endMs > firstSecond * 1000 {
            response.distribute(firstSecond: firstSecond, lastSecond: lastSecond) { second, output, total in
                let index = Int(second - firstSecond)
                measured[index].output += output
                measured[index].total += total
            }
        }
        for estimate in estimates {
            let end = min(estimate.endMs ?? nowMs, nowMs)
            let from = max(firstSecond, ResponseTiming.second(of: estimate.startMs))
            let to = min(lastSecond, ResponseTiming.second(of: end))
            guard from <= to else { continue }
            for second in from...to {
                let overlap = ResponseTiming.overlap(second * 1000, (second + 1) * 1000, estimate.startMs, end)
                guard overlap > 0 else { continue }
                let index = Int(second - firstSecond)
                estimated[index].output += estimate.speed.output * Double(overlap) / 1000
                estimated[index].total += estimate.speed.total * Double(overlap) / 1000
            }
        }
        return (0..<historySeconds).map { index in
            HistoryPoint(
                time: Date(timeIntervalSince1970: TimeInterval(firstSecond + Int64(index))),
                measured: measured[index],
                estimated: estimated[index]
            )
        }
    }

    // MARK: - Agents list

    /// The agents list (METRICS 6.7) with each agent's task score value, and the window's thinking sums with the highest
    /// value of the listed agents, in total and per source (6.9 *Sets and groups*).
    private func agentList(nowMs: Int64, window: LiveWindow, references: SpeedReferences)
        -> (agents: [EngineAgentStatus], intelligence: IntelligenceSums, sourceIntelligence: [SourceKind: IntelligenceSums]) {
        func status(of agent: EngineAgent) -> EngineAgentStatus {
            let isFresh = agent.lastLineMs.map { nowMs - $0 <= Self.staleMs } ?? false
            let phase = isFresh ? agent.phase : .idle
            var intelligence = window.intelligenceByAgent[agent.id] ?? .zero
            if let task = agent.task {
                intelligence.score = TaskSetValue(id: agent.id, lastLineMs: agent.lastLineMs,
                                                  value: task.value(idle: phase == .idle))
            }
            return EngineAgentStatus(
                agent: agent,
                phase: phase,
                live: window.byAgent[agent.id] ?? .zero,
                recentSpeed: references.recent(for: agent.id)?.output,
                intelligence: intelligence,
                signals: WorkSignals(agent.actions, nowMs: nowMs)
            )
        }
        var listed: [String: EngineAgentStatus] = [:]
        for agent in agents.values {
            let row = status(of: agent)
            let isRecent = agent.lastLineMs.map { $0 >= nowMs - Self.agentListWindowMs } ?? false
            if isRecent || row.phase == .generating { listed[agent.id] = row }
        }
        // Ancestors of a listed agent are listed too, even when inactive.
        for id in Array(listed.keys) {
            var next = agents[id]?.parentId
            while let parentId = next, listed[parentId] == nil, let parent = agents[parentId] {
                listed[parentId] = status(of: parent)
                next = parent.parentId
            }
        }
        var bySource = window.intelligenceBySource
        var all = window.intelligence
        for row in listed.values where row.intelligence.score.form != .none {
            bySource[row.agent.source, default: .zero].score.merge(row.intelligence.score)
            all.score.merge(row.intelligence.score)
        }
        return (AgentListOrder.arrange(listed), all, bySource)
    }

    // MARK: - Summaries

    static func summary(of record: ResponseRecord, project: String) -> ResponseSummary {
        let timed = TimedResponse(record)
        let duration = timed.map { Double($0.durationMs) / 1000 }
        return ResponseSummary(
            source: record.source,
            project: project,
            model: record.model,
            outputTokens: record.usage.output,
            totalTokens: record.usage.total,
            duration: duration,
            speed: duration.map { Double(record.usage.output) / $0 },
            endedAt: Date(timeIntervalSince1970: TimeInterval(record.endMs) / 1000)
        )
    }
}
