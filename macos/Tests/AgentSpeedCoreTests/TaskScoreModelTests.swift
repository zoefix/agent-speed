import Foundation
import Testing
@testable import AgentSpeedCore

/// The places where the Swift model takes a faster path than `task_score.py` (loop marks, fix features, the current
/// task) checked against direct ports of the reference's code on random streams, and Python's numeric and string
/// semantics at their edges.
@Suite("Task score model")
struct TaskScoreModelTests {
    /// A deterministic generator (SplitMix64).
    struct Random: RandomNumberGenerator {
        var state: UInt64

        mutating func next() -> UInt64 {
            state &+= 0x9E37_79B9_7F4A_7C15
            var z = state
            z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
            z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
            return z ^ (z >> 31)
        }

        mutating func int(_ bound: Int) -> Int { Int(next() % UInt64(bound)) }
        mutating func chance(_ percent: Int) -> Bool { int(100) < percent }
    }

    static func digest(_ value: Int) -> TaskDigest { TaskDigest(value: UInt64(value) &* 0x1_0000_0001 &+ 7) }

    /// A stream with few distinct tools, digests, keys, suites and texts, so repeats, loops, checks, episodes and
    /// oscillations all happen.
    static func stream(_ random: inout Random, count: Int) -> [TaskEvent] {
        var events: [TaskEvent] = []
        var t: Int64 = 1_000_000
        for _ in 0..<count {
            t += Int64(random.int(3) * 500)
            if random.chance(6) {
                events.append(.prompt(t, continuation: random.chance(50)))
                continue
            }
            let kinds: [TaskActionKind] = [.read, .search, .edit, .command, .command, .command, .other, .web]
            let kind = kinds[random.int(kinds.count)]
            var command: TaskCommand?
            var hunks: [TaskHunk]?
            if kind == .command, random.chance(90) {
                let classes: [TaskCommandClass] = [.test, .test, .build, .check, .other]
                let suites: [String?] = [nil, "cargo test", "pytest", "swift test"]
                command = TaskCommand(commandClass: classes[random.int(classes.count)], suite: suites[random.int(4)],
                                      targeted: random.chance(35), key: digest(random.int(5)))
            }
            if kind == .edit, random.chance(85) {
                hunks = (0..<(1 + random.int(2))).map { _ in
                    TaskHunk(file: digest(random.int(2)), old: random.chance(25) ? nil : digest(100 + random.int(4)),
                             new: digest(100 + random.int(4)))
                }
            }
            var outcome: TaskOutcome?
            var failures: TaskRunFailures?
            if random.chance(85) {
                let failed: Bool? = random.chance(15) ? nil : random.chance(45)
                outcome = TaskOutcome(exitCode: nil, failed: failed, t: random.chance(10) ? nil : t + Int64(random.int(4) * 400))
                if command != nil {
                    let fingerprints = (0..<random.int(4)).map { _ in digest(200 + random.int(6)) }
                    failures = TaskRunFailures(fingerprints: random.chance(80) ? Array(Set(fingerprints)).sorted() : fingerprints,
                                               failedCount: random.chance(40) ? Int64(random.int(5)) : nil,
                                               passedCount: random.chance(40) ? Int64(random.int(3)) : nil, framework: nil)
                }
            }
            let tools = ["Bash", "Read", "Edit", "Grep"]
            events.append(.action(t, TaskAction(kind: kind, tool: tools[random.int(tools.count)],
                                                digest: digest(random.int(4)), command: command, outcome: outcome,
                                                failures: failures, hunks: hunks)))
        }
        return events
    }

    // MARK: Loop marks

    /// `loop_marks` exactly as the reference scans.
    static func referenceMarks(_ keys: [Int]) -> [Bool] {
        let n = keys.count
        var marked = [Bool](repeating: false, count: n)
        for i in 0..<n {
            for b in 1...3 {
                if i + 3 * b > n { continue }
                let block = keys[i..<(i + b)]
                var repeats = 1
                while i + (repeats + 1) * b <= n, keys[(i + repeats * b)..<(i + (repeats + 1) * b)].elementsEqual(block) {
                    repeats += 1
                }
                if repeats >= 3 {
                    for p in (i + b)..<(i + repeats * b) { marked[p] = true }
                }
            }
        }
        return marked
    }

    @Test func loopMarksMatchTheReferenceScan() {
        var random = Random(state: 11)
        for round in 0..<3000 {
            let alphabet = 1 + random.int(4)
            let keys = (0..<random.int(40)).map { _ in random.int(alphabet) }
            #expect(TaskLoops.marks(keys) == Self.referenceMarks(keys), "round \(round): \(keys)")
        }
        #expect(TaskLoops.marks([1, 1, 1]) == [false, true, true])
        #expect(TaskLoops.marks([1, 2, 1, 2, 1, 2, 3]) == [false, false, true, true, true, true, false])
        #expect(TaskLoops.marks([1, 1]) == [false, false])
    }

    // MARK: The evaluation

    @Test func theEvaluationMatchesALiteralPortOfTheReference() {
        var random = Random(state: 97)
        var states: [TaskState: Int] = [:]
        for round in 0..<1500 {
            let events = Self.stream(&random, count: 1 + random.int(90))
            let now: Int64? = random.chance(30) ? (events.last?.t ?? 0) - Int64(random.int(8) * 500) : nil
            let got = TaskResult(TaskEvaluation(events, now: now))
            let want = ReferenceEvaluation(events, now: now)
            let label = "round \(round)"
            #expect(got.state.rawValue == want.state, "\(label) state")
            #expect(got.score?.bitPattern == want.score?.bitPattern, "\(label) score \(got.score ?? -1) vs \(want.score ?? -1)")
            #expect(got.dims == TaskDimensions(v: want.v, c: want.c, r: want.r, e: want.e, k: want.k), "\(label) dims")
            #expect(got.ig == want.ig && got.loop == want.loop && got.osc == want.osc, "\(label) shares")
            #expect(got.counts == want.countsValue, "\(label) counts")
            #expect(got.firstPassMs == want.firstPassMs, "\(label) first pass")
            #expect(got.stallEnds == want.stallEnds, "\(label) stall ends")
            #expect(got.vHeld == want.vHeld, "\(label) vHeld")
            #expect(got.openFailure == want.openFailure, "\(label) open failure")
            #expect(got.lastPass == want.lastPass, "\(label) last pass")
            states[got.state, default: 0] += 1
        }
        for state in TaskState.allCases { #expect((states[state] ?? 0) > 20, "\(state) occurs") }
    }

    // MARK: Fix features

    /// `fix_features`: the events up to and including the run evaluated again.
    static func referenceFixFeatures(_ events: [TaskEvent], through event: Int, now: Int64?) -> TaskFixFeatures {
        let prefix = TaskEvaluation(events.prefix(event + 1), now: now)
        let index = prefix.acts.count - 1
        let run = prefix.acts[index]
        let seq = prefix.checks[run.check].seq
        let episode = prefix.episodes.first { $0.check == run.check && $0.fails.contains(index) }!
        let attempts = episode.fails.firstIndex(of: index)! + 1
        let peak = seq.map { prefix.acts[$0].errors }.max()!
        return TaskFixFeatures(attempts: min(1.0, Double(attempts - 1) / 4), errors: min(1.0, log2(Double(1 + run.errors)) / 8),
                               trend: Double(run.errors) / Double(peak), osc: prefix.osc, ig: prefix.ig)
    }

    @Test func fixFeaturesMatchAPrefixEvaluation() {
        var random = Random(state: 23)
        var compared = 0
        for _ in 0..<400 {
            let events = Self.stream(&random, count: 10 + random.int(70))
            let now: Int64? = random.chance(30) ? (events.last?.t ?? 0) - Int64(random.int(6) * 500) : nil
            guard let task = TaskSplit.current(events, now: now) else { continue }
            let taskEvents = Array(task.events)
            let evaluation = TaskEvaluation(taskEvents, now: now)
            for (run, features) in evaluation.allFixFeatures() {
                let reference = Self.referenceFixFeatures(taskEvents, through: evaluation.acts[run].event, now: now)
                #expect(features == reference)
                #expect(evaluation.fixFeatures(run: run) == features)
                compared += 1
            }
        }
        #expect(compared > 500)
    }

    // MARK: Current task

    /// `current_task`: the visible events split into tasks, the last one.
    static func referenceCurrent(_ events: [TaskEvent], now: Int64?) -> TaskSpan? {
        TaskSplit.split(events.filter { now == nil || $0.t <= now! }).last
    }

    @Test func theCurrentTaskMatchesSplittingTheVisibleEvents() {
        var random = Random(state: 5)
        for _ in 0..<2000 {
            var events = Self.stream(&random, count: random.int(30))
            if random.chance(30) { events.shuffle(using: &random) }
            let now: Int64? = random.chance(60) ? 1_000_000 + Int64(random.int(40) * 500) : nil
            let got = TaskSplit.current(events, now: now)
            let want = Self.referenceCurrent(events, now: now)
            #expect(got?.startMs == want?.startMs)
            #expect(got.map { Array($0.events) } == want.map { Array($0.events) })
        }
    }

    @Test func splittingFollowsPromptsAndContinuations() {
        let read = TaskAction(kind: .read, tool: "Read", digest: Self.digest(1))
        let events: [TaskEvent] = [.prompt(0, continuation: true), .action(1, read), .prompt(2, continuation: false),
                                   .prompt(3, continuation: true), .action(4, read), .prompt(5, continuation: false)]
        let spans = TaskSplit.split(events)
        #expect(spans.map(\.startMs) == [0, 2, 5])
        #expect(spans.map(\.events.count) == [2, 3, 1])
        #expect(TaskSplit.split([.action(7, read)]).map(\.startMs) == [7])
        #expect(TaskSplit.current([], now: nil) == nil)
    }

    // MARK: Python semantics

    @Test func roundingAddsHalfInDoubleThenTruncates() {
        #expect(taskRoundHalfAway(93.75) == 94)
        #expect(taskRoundHalfAway(0.5) == 1)
        #expect(taskRoundHalfAway(-0.5) == -1)
        #expect(taskRoundHalfAway(2.4999999999999996) == 2)
        // 0.49999999999999994 + 0.5 rounds to 1.0 in Double, so Python's int(value + 0.5) is 1.
        #expect(taskRoundHalfAway(0.49999999999999994) == 1)
        #expect(taskRoundHalfAway(-0.49999999999999994) == -1)
        #expect(taskRoundHalfAway(.nan) == 0)
        #expect(taskRoundHalfAway(.infinity) == Int.max)
    }

    @Test func integerDivisionIsCorrectlyRoundedLikePython() {
        #expect(taskDivide(1, 3) == 1.0 / 3.0)
        #expect(taskDivide(3_381_892_141_588_998_161, 611_100) == 5534105942708.229)
        #expect(taskDivide(354_843_653_387_482_572, 90125) == 3937238872537.948)
        #expect(taskDivide(3_865_964_579_695_847_258, 73251) == 52776952938469.74)
        #expect(taskDivide(9_007_199_254_740_993, 1) == 9007199254740992.0)
        #expect(taskDivide(9_223_372_036_854_775_807, 3) == 3.0744573456182584e+18)
        #expect(taskDivide(-(1 << 60) - 3, 5) == -2.305843009213694e+17)
        #expect(taskDivide(3, (1 << 60) + 1) == 2.6020852139652106e-18)
    }

    @Test func sourceOrderIsTheReferences() {
        #expect(SourceKind.allCases.map(\.rawValue) == [
            "codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity", "workbuddy",
            "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid", "trae",
        ])
    }

    @Test func normalisationUsesPythonWhitespaceAndCodePoints() {
        #expect(TaskPrompts.normalize("  Go\u{1F}\u{85}AHEAD\u{2026} ") == "go ahead")
        #expect(TaskPrompts.normalize("繼續。、!") == "繼續")
        #expect(TaskPrompts.normalize("\u{FF2F}\u{FF2B}\u{FF01}") == "ok")
        #expect(TaskPrompts.isContinuation("ok\u{1C}"))
        // U+200B is not whitespace in Python, so the text is not "ok".
        #expect(!TaskPrompts.isContinuation("o\u{200B}k"))
        // Only ASCII letters fold.
        #expect(TaskPrompts.normalize("\u{C9}COLE").unicodeScalars.elementsEqual("\u{C9}cole".unicodeScalars))
    }

    @Test func tagsAndPrefixesCompareCodePoints() {
        // A combining mark after `>` makes one Swift character, but the code points still start with the tag.
        let line = JSONValue.object(["type": .string("user"),
                                     "message": .object(["content": .string("<command-name>\u{301}/model")])])
        #expect(TaskPrompts.line(.claudeCode, line) == TaskPromptLine(kind: .command))
        let caveat = JSONValue.object(["type": .string("user"),
                                       "message": .object(["content": .string("  <bash-input\u{2028}ls")])])
        #expect(TaskPrompts.line(.claudeCode, caveat) == nil)
        let codex = JSONValue.object(["type": .string("response_item"), "payload": .object([
            "type": .string("message"), "role": .string("user"),
            "content": .array([.object(["type": .string("input_text"), "text": .string("<é")])]),
        ])])
        #expect(TaskPrompts.line(.codex, codex) == TaskPromptLine(kind: .prompt))
    }

    @Test func queuedPromptTimesFollowTheTimestampRule() {
        func queued(_ timestamp: JSONValue) -> JSONValue {
            .object(["type": .string("attachment"), "attachment": .object([
                "type": .string("queued_command"), "commandMode": .string("prompt"), "humanTurn": .bool(true),
                "prompt": .string("continue"), "timestamp": timestamp,
            ])])
        }
        #expect(TaskPrompts.line(.claudeCode, queued(.string("2026-10-28T09:15:02.250+08:00")))
            == TaskPromptLine(kind: .prompt, continuation: true, t: 1_793_150_102_250))
        #expect(TaskPrompts.line(.claudeCode, queued(.number(JSONLiteral("1793150102250"))))?.t == nil)
        #expect(TaskPrompts.line(.claudeCode, queued(.string("2026-10-28T09:15:02")))?.t == nil)
    }

    @Test func setValuesMergeAssociatively() {
        var random = Random(state: 41)
        for _ in 0..<500 {
            let members = (0..<random.int(7)).map { index -> (id: String, lastLineMs: Int64?, value: TaskAgentValue) in
                let forms: [TaskValueForm] = [.live, .resting, .none]
                let form = forms[random.int(3)]
                let score = form == .none ? nil : Double(random.int(4)) * 12.5
                return (["b", "a", "B", "é", "e\u{301}"][random.int(5)] + String(index % 2),
                        random.chance(20) ? nil : Int64(random.int(3)),
                        TaskAgentValue(form: form, score: score, shown: score.map(taskRoundHalfAway), state: nil))
            }
            let highest = TaskSetValue.highest(members)
            let singles = members.map { TaskSetValue(id: $0.id, lastLineMs: $0.lastLineMs, value: $0.value) }
            let cut = random.int(singles.count + 1)
            let left = singles[..<cut].reduce(TaskSetValue.empty) { $0.merged(with: $1) }
            let right = singles[cut...].reduce(TaskSetValue.empty) { $0.merged(with: $1) }
            #expect(left.merged(with: right) == highest)
        }
    }
}
