import Foundation
import Testing
@testable import AgentSpeedCore

/// The `pipeline` cases of `spec/fixtures/quota-estimate-cases.json`: raw events (responses, Codex token count lines,
/// sign-in changes, snapshots, a stored file) through the whole estimator to the outputs the reference gives.
struct QuotaEstimatePipelineTests {
    typealias F = EstimateFixtures

    static var pipeline: [String: Any] { F.object(F.cases["pipeline"]) }

    @Test(arguments: ["ledger", "chains", "claude", "keys", "classes", "loweredCap", "planChange", "live", "store",
                      "report"])
    func group(_ name: String) throws {
        let cases = F.list(Self.pipeline[name])
        #expect(!cases.isEmpty)
        for c in cases {
            let got = try Self.run(F.object(c["input"]))
            #expect(Set(got.keys) == Set(F.object(c["expected"]).keys), "\(name) / \(c["name"] ?? "")")
            let diff = F.differences(got, c["expected"])
            #expect(diff.isEmpty, "\(name) / \(c["name"] ?? ""): \(diff.prefix(12))")
        }
    }

    @Test func comparisonSeesADifference() throws {
        let c = F.list(Self.pipeline["claude"])[0]
        var got = try Self.run(F.object(c["input"]))
        #expect(F.differences(got, c["expected"]).isEmpty)
        var entries = got["entries"] as! [[String: Any]]
        entries[0]["livePercent"] = 3.1
        got["entries"] = entries
        #expect(!F.differences(got, c["expected"]).isEmpty)
    }

    @Test func everyGroupIsCovered() {
        let groups = Set(Self.pipeline.keys)
        #expect(groups == ["ledger", "chains", "claude", "keys", "classes", "loweredCap", "planChange", "live", "store",
                           "report", "cadence"])
        #expect(F.list(Self.pipeline["claude"]).count == 6)
        #expect(F.list(Self.pipeline["store"]).count == 6)
    }

    @Test func cadence() {
        let cadence = F.object(Self.pipeline["cadence"])
        let e = F.object(cadence["entry"])
        let window = F.object(e["window"])
        var current = [QuotaReportEntry(
            sources: e["sources"] as! [String], kind: QuotaWindowKind(rawValue: F.string(window["kind"])!)!,
            name: F.string(window["name"]), minutes: Int(F.int(window["minutes"])!), usedPercent: F.double(e["usedPercent"]),
            resetsInMs: F.int(e["resetsInMs"]), usedMicroUsd: F.int(e["usedMicroUsd"])!,
            confidence: QuotaEstimateConfidence(rawValue: F.string(F.object(e["estimate"])["confidence"])!)!)]
        var reporter = QuotaReportCadence()
        var out: [String] = []
        for step in F.list(cadence["steps"]) {
            if step["newRun"] as? Bool == true { reporter = QuotaReportCadence() }
            switch F.string(step["change"]) {
            case "set":
                var added = current[0]
                added.sources = ["claudeCode"]
                current.append(added)
            case "spend": current[0].usedMicroUsd += 1
            case "percent": current[0].usedPercent = (current[0].usedPercent ?? 0) + 1
            case "confidence": current[0].estimate.confidence = current[0].estimate.confidence == .high ? .medium : .high
            case "reset":
                current[0].resetsInMs = current[0].resetsInMs == nil ? 1_000_000 : nil
                current[0].usedPercent = nil
            default: break
            }
            let q = reporter.step(nowMs: F.int(step["at"])!, entries: current, on: step["switch"] as? Bool ?? true,
                                  leaving: step["leaving"] as? Bool ?? false, fits: step["fits"] as? Bool ?? true)
            out.append(q == nil ? "absent" : (q!.isEmpty ? "[]" : "entries"))
        }
        #expect(out == cadence["expected"] as? [String])
    }

    // MARK: The reference's `run`

    static func run(_ input: [String: Any]) throws -> [String: Any] {
        let salt = F.salt(F.string(input["salt"])!)
        var inputs = QuotaEstimateInputs()
        inputs.priceVersion = 8
        for event in F.list(input["events"]) {
            switch F.string(event["type"]) {
            case "response":
                inputs.spend.append(QuotaEstimateSpend(
                    source: F.string(event["source"])!, model: F.string(event["model"])!, startMs: F.int(event["startMs"]),
                    endMs: F.int(event["endMs"])!, costMicroUsd: F.int(event["costMicroUsd"]),
                    tokens: F.int(event["tokens"]) ?? 0, file: F.string(event["file"]),
                    provider: F.string(event["provider"]), cloudRoute: F.string(event["cloudRoute"]) != nil))
            case "tokenCount":
                let ts = F.int(event["ts"])!
                let limits = try F.codexLimits(F.object(event["rateLimits"]))
                inputs.codexLines.append(QuotaCodexLine(file: F.string(event["file"])!, ts: ts,
                                                        windows: limits.windows(timestampMs: ts), plan: limits.planType))
            case "codexAuth":
                inputs.codexKeys.append(QuotaKeyEntry(
                    at: F.int(event["at"])!, key: QuotaAccounts.key(salt: salt, vendor: "codex", id: F.string(event["accountId"])!)))
            case "claudeProfile":
                inputs.claudeProfiles.append(QuotaClaudeProfile(
                    at: F.int(event["at"])!, organization: F.string(event["org"])!, account: F.string(event["account"])!,
                    organizationType: F.string(event["orgType"]), tier: F.string(event["tier"]), salt: salt))
            case "snapshot":
                inputs.snapshots.append(QuotaEstimateSnapshot(
                    source: F.string(event["source"])!, observedMs: F.int(event["observedMs"])!,
                    windows: F.list(event["windows"]).map(F.window),
                    orgKey: F.string(event["org"]).map { QuotaAccounts.key(salt: salt, vendor: "claudeDesktop", id: $0) }))
            default:
                Issue.record("unknown event \(event)")
            }
        }
        if let store = input["store"] {
            inputs.stored = QuotaEstimateStore.decode(try JSONSerialization.data(withJSONObject: store)) ?? .empty
        }
        if let generated = input["generatedStore"] as? [String: Any] {
            inputs.stored = QuotaEstimateStore.decode(try JSONSerialization.data(withJSONObject: generatedStore(generated)))
                ?? .empty
        }
        let now = F.int(input["now"])!
        let build = QuotaEstimateBuild(inputs, salt: salt, nowMs: now)
        let want = input["outputs"] as? [String] ?? ["responses", "instances", "estimates", "entries"]
        var out: [String: Any] = [:]
        if want.contains("responses") {
            out["responses"] = build.spendAccounts.map { $0 as Any? ?? NSNull() }
        }
        if want.contains("sums") {
            out["sums"] = F.list(input["sums"]).map { q -> [String: Any] in
                let accounts = Set((q["accountsOf"] as? [Int] ?? []).compactMap { build.spendAccounts[$0] })
                let name = F.string(q["name"]), a = F.int(q["a"])!, b = F.int(q["b"])!
                let outer = build.ledger.outer(accounts, name, a, b)
                return ["inner": build.ledger.inner(accounts, name, a, b).cost, "outer": [outer.cost, outer.unpriced],
                        "outerHigh": F.fnum(build.ledger.outerHigh(accounts, name, a, b))]
            }
        }
        if want.contains("instances") {
            let rows = build.instances.map { x -> (key: (String, String, Int64, String), row: [String: Any]) in
                let bounds: Any = x.bounds.map { ["lo": F.fnum($0.lo), "hi": F.fnum($0.hi), "offset": $0.offset] } ?? NSNull()
                return ((x.account, x.window, x.instance.resetsMs, x.plan ?? "None"), [
                    "account": x.account, "window": x.window, "plan": x.plan ?? NSNull(), "tier": x.tier ?? NSNull(),
                    "class": x.capClass.list.map { $0 ?? NSNull() as Any }, "R": x.instance.resetsMs, "S": x.instance.start,
                    "Shi": x.instance.startHigh, "observations": x.instance.observations.count, "bounds": bounds,
                ])
            }
            out["instances"] = rows.sorted { $0.key < $1.key }.map(\.row)
        }
        if want.contains("estimates") {
            var estimates: [String: Any] = [:]
            for (capClass, e) in build.estimates {
                estimates[capClass.name] = ["cap": F.fnum(e.cap), "low": F.fnum(e.low), "high": F.fnum(e.high),
                                            "confidence": e.confidence.rawValue, "windows": e.windows]
            }
            out["estimates"] = estimates
        }
        if want.contains("entries") {
            out["entries"] = build.entries(nowMs: now).map { F.foundation($0.json) }
        }
        if want.contains("store") {
            out["store"] = F.foundation(try build.store().encoded())
        }
        if want.contains("storeCounts") {
            let store = build.store()
            out["storeCounts"] = ["observations": store.observations.count, "instances": store.instances.count,
                                  "keys": store.keys.count]
        }
        return out
    }

    /// The reference's `generated_store`.
    static func generatedStore(_ g: [String: Any]) -> [String: Any] {
        let t0 = F.int(g["t0"])!, account = F.string(g["account"])!
        let observations = (1...Int(F.int(g["observations"])!)).map { i -> [String: Any] in
            ["account": account, "source": "claudeCode", "kind": "sevenDay", "name": NSNull(), "minutes": 10080,
             "t": t0 + Int64(i) * F.minute, "tLo": t0 + Int64(i) * F.minute,
             "p": (Double(i) / 10 * 10).rounded() / 10, "R": t0 + 6 * F.day, "plan": "claude_max", "tier": NSNull()]
        }
        let instances = (1...Int(F.int(g["instances"])!)).map { i -> [String: Any] in
            ["class": ["plan", "anthropic/claude_max", NSNull(), "fiveHour::300"], "account": account,
             "R": t0 - Int64(i) * 5 * F.hour, "lo": 1e8, "hi": 2e8, "pMax": 40.0,
             "tLast": t0 - Int64(i) * 5 * F.hour - F.hour]
        }
        return ["version": g["version"]!, "observations": observations, "instances": instances,
                "keys": [["source": "claudeCode", "at": t0, "key": account, "plan": "claude_max", "tier": NSNull()]]]
    }
}
