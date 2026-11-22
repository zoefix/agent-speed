import Foundation
import os

struct OpenBitFunRoundID: Hashable, Sendable {
    let turn: String
    let round: String
}

private struct OpenBitFunAttempt: Equatable, Sendable {
    let round: OpenBitFunRoundID
    let id: String
    let model: String?
    let provider: String?
    let usage: TokenUsage?
    let recordedMs: Int64
}

struct OpenBitFunRoundUsage: Sendable {
    let model: String?
    let provider: String?
    let count: Int
    var tokens = TokenUsage.zero
    var measured = true

    fileprivate init(_ rows: [OpenBitFunAttempt]) {
        var attempts: [String: OpenBitFunAttempt] = [:]
        var last = rows[0].id
        for row in rows {
            if attempts.updateValue(row, forKey: row.id) == nil { last = row.id }
        }
        model = attempts[last]?.model
        provider = attempts[last]?.provider
        count = attempts.count
        for attempt in attempts.values {
            guard let usage = attempt.usage else { measured = false; continue }
            tokens.input = TokenCount.sum(tokens.input, usage.input)
            tokens.cachedInput = TokenCount.sum(tokens.cachedInput, usage.cachedInput)
            tokens.cacheWriteInput = TokenCount.sum(tokens.cacheWriteInput, usage.cacheWriteInput)
            tokens.output = TokenCount.sum(tokens.output, usage.output)
            tokens.reasoning = TokenCount.sum(tokens.reasoning, usage.reasoning)
        }
    }
}

/// A daily usage file is shared by sessions, live monitoring and history. Cache only its measurement fields.
final class OpenBitFunUsageLedger: Sendable {
    static let shared = OpenBitFunUsageLedger(root: FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("openbitfun/data/token_usage/records"))

    final class Snapshot: Sendable {
        static let empty = Snapshot(rows: [])
        let rounds: [OpenBitFunRoundID: OpenBitFunRoundUsage]
        let signature: Int64
        let modifiedNs: Int64

        fileprivate init(rows: [OpenBitFunAttempt]) {
            rounds = Dictionary(grouping: rows, by: \.round).mapValues(OpenBitFunRoundUsage.init)
            var hash: UInt64 = 0
            for row in rows {
                for value in [row.round.turn, row.round.round, row.id, row.model ?? "", row.provider ?? ""] {
                    hash = (hash ^ KeyHash.fnv1a(value)) &* KeyHash.prime
                }
                if let usage = row.usage {
                    for count in [usage.input, usage.cachedInput, usage.cacheWriteInput, usage.output, usage.reasoning] {
                        hash = (hash ^ UInt64(count)) &* KeyHash.prime
                    }
                }
            }
            signature = Int64(bitPattern: hash)
            let newest = rows.map(\.recordedMs).max() ?? 0
            modifiedNs = min(newest, Int64.max / 1_000_000) * 1_000_000
        }
    }

    private struct Day: Sendable {
        let stat: FileStat
        var sessions: [String: [OpenBitFunAttempt]] = [:]

        init?(path: String, stat: FileStat) {
            guard let rows = HarnessMetadata.read(path, maxBytes: DocumentReader.defaultMaxBytes)?["records"]?.array else { return nil }
            self.stat = stat
            for row in rows {
                let details = row["token_details"]
                let request = details?["openbitfunModelRequest"]
                guard HarnessJSON.milliseconds(request?["version"]) == 1,
                      let session = HarnessJSON.text(row["session_id"]), let turn = HarnessJSON.text(row["turn_id"]),
                      let round = HarnessJSON.text(request?["roundId"]), let id = HarnessJSON.text(request?["attemptId"])
                else { continue }
                let output = HarnessJSON.count(row["output_tokens"])
                let usage = request?["usageKnown"]?.bool == true ? TokenUsage(
                    input: HarnessJSON.count(row["input_tokens"]), cachedInput: HarnessJSON.count(row["cached_tokens"]),
                    cacheWriteInput: HarnessJSON.count(row["cache_write_tokens"]), output: output,
                    reasoning: min(output, HarnessJSON.count(details?["reasoningTokenCount"]))) : nil
                let model = HarnessJSON.text(row["effective_model_name"]) ?? HarnessJSON.text(request?["responseModel"])
                    ?? HarnessJSON.text(request?["requestedModel"])
                sessions[session, default: []].append(OpenBitFunAttempt(round: .init(turn: turn, round: round), id: id,
                    model: model, provider: HarnessJSON.text(request?["channel"]?["protocol"]), usage: usage,
                    recordedMs: max(0, OpenBitFunRound.timestamp(row["timestamp"]) ?? 0)))
            }
        }
    }

    private struct State: Sendable {
        var days: [String: Day] = [:]
        var sessions: [String: Snapshot] = [:]
        var nextScan: UInt64 = 0
    }
    private let cache = OSAllocatedUnfairLock(initialState: State())
    private let root: URL
    init(root: URL) { self.root = root }

    func snapshot(for session: String) -> Snapshot {
        cache.withLock { state in
            let now = DispatchTime.now().uptimeNanoseconds
            guard now >= state.nextScan else { return state.sessions[session] ?? .empty }
            state.nextScan = now + 1_000_000_000
            let names = FileStat.lstat(root.path)?.isDirectory == true
                ? (try? FileManager.default.contentsOfDirectory(atPath: root.path)) ?? [] : []
            var seen: Set<String> = []
            var affected: Set<String> = []
            for name in names where name.utf8.count == 15 && name.hasSuffix(".json") {
                guard Timestamp.parse(String(name.dropLast(5)) + "T00:00:00Z") != nil else { continue }
                let path = root.appendingPathComponent(name).path
                guard let stat = FileStat.lstat(path), stat.isRegularFile else { continue }
                seen.insert(name)
                guard state.days[name]?.stat != stat, let day = Day(path: path, stat: stat) else { continue }
                let previous = state.days.updateValue(day, forKey: name)?.sessions ?? [:]
                for owner in Set(previous.keys).union(day.sessions.keys) where previous[owner] != day.sessions[owner] {
                    affected.insert(owner)
                }
            }
            for name in state.days.keys where !seen.contains(name) {
                if let removed = state.days.removeValue(forKey: name) { affected.formUnion(removed.sessions.keys) }
            }
            if !affected.isEmpty {
                let days = state.days.sorted { $0.key < $1.key }
                for owner in affected {
                    let rows = days.flatMap { $0.value.sessions[owner] ?? [] }
                    state.sessions[owner] = rows.isEmpty ? nil : Snapshot(rows: rows)
                }
            }
            return state.sessions[session] ?? .empty
        }
    }
}
