import Foundation
import Testing
@testable import AgentSpeedCore

/// The unit cases of `spec/fixtures/quota-estimate-cases.json` (QUOTAS 10) through the Swift estimator.
struct QuotaEstimateFixtureTests {
    typealias F = EstimateFixtures

    @Test func constantsMatchTheReference() {
        let c = F.object(F.cases["constants"])
        #expect(F.int(c["lagMs"]) == QuotaEstimateConstants.lagMs)
        #expect(F.double(c["hiPenalty"]) == QuotaEstimateConstants.hiPenalty)
        #expect(F.double(c["fullStep"]) == QuotaEstimateConstants.fullStep)
        #expect(F.double(c["minWeight"]) == QuotaEstimateConstants.minWeight)
        #expect(F.double(c["incrementStep"]) == QuotaEstimateConstants.incrementStep)
        #expect(F.int(c["segmentsPerInstance"]) == Int64(QuotaEstimateConstants.segmentsPerInstance))
        #expect(F.int(c["instancesPerClass"]) == Int64(QuotaEstimateConstants.instancesPerClass))
        #expect(F.double(c["highWidth"]) == QuotaEstimateConstants.highWidth)
        #expect(F.double(c["precisionFloor"]) == QuotaEstimateConstants.precisionFloor)
        #expect(F.int(c["sendEveryMs"]) == QuotaEstimateConstants.sendEveryMs)
    }

    @Test func resolution() {
        let cases = F.list(F.cases["resolution"])
        #expect(cases.count == 6)
        for c in cases {
            #expect(QuotaEstimateWindow.resolution(F.double(c["p"])!) == F.double(c["expected"]), "\(c)")
        }
    }

    @Test func accountKeys() {
        let accounts = F.object(F.cases["accounts"])
        let salt = F.salt(F.string(accounts["salt"])!)
        for k in F.list(accounts["keys"]) {
            #expect(QuotaAccounts.key(salt: salt, vendor: F.string(k["vendor"])!, id: F.string(k["id"])!)
                == F.string(k["expected"]))
        }
    }

    @Test func fingerprints() {
        let cases = F.list(F.object(F.cases["accounts"])["fingerprints"])
        #expect(cases.count == 8)
        func points(_ value: Any?) -> [QuotaFingerprintPoint] {
            (value as? [[Any]] ?? []).map {
                QuotaFingerprintPoint(t: F.int($0[0])!, window: $0[1] as! String, resetsMs: F.int($0[2])!,
                                      minutes: Int(F.int($0[3])!))
            }
        }
        for c in cases {
            let decision = QuotaAccountFingerprint(points(c["a"]), points(c["b"])).decision(at: F.int(c["at"])!)
            let expected: Bool? = c["expected"] is NSNull ? nil : (c["expected"] as? Bool)
            #expect(decision == expected, "\(c["why"] ?? "")")
        }
    }

    @Test func desktopMergeAndEqualKeys() {
        let accounts = F.object(F.cases["accounts"])
        let merges = F.list(accounts["desktopMerge"])
        #expect(merges.count == 8)
        for c in merges {
            let print: Bool? = c["fingerprint"] is NSNull ? nil : c["fingerprint"] as? Bool
            #expect(QuotaAccounts.desktopSharesCli(desktopOrg: F.string(c["desktopOrg"]), cliOrg: F.string(c["cliOrg"]),
                                                   cliOrgType: F.string(c["cliOrgType"]), fingerprint: print)
                == c["expected"] as? Bool, "\(c)")
        }
        for c in F.list(accounts["keysShare"]) {
            let print: Bool? = c["fingerprint"] is NSNull ? nil : c["fingerprint"] as? Bool
            #expect(QuotaAccounts.keysShare(fingerprint: print) == c["expected"] as? Bool)
        }
    }

    @Test func plansAndChains() {
        let accounts = F.object(F.cases["accounts"])
        for c in F.list(accounts["plans"]) {
            #expect(QuotaAccounts.cleanPlan(c["value"] as? String) == F.string(c["expected"]), "\(c)")
        }
        let chains = F.object(accounts["chains"])
        var union = QuotaChains()
        let roots = (chains["lines"] as? [[String]] ?? []).map { union.link($0) }
        var names: [String: String] = [:]
        let got = roots.map { root -> String in
            let found = union.find(root)
            if let name = names[found] { return name }
            names[found] = "chain\(names.count)"
            return names[found]!
        }
        #expect(got == chains["expected"] as? [String])
    }

    @Test func segments() {
        let cases = F.list(F.cases["segments"])
        #expect(cases.count == 7)
        for c in cases {
            let name = F.string(c["name"]) ?? "?"
            let ledger = F.ledger(F.list(c["rows"]))
            let observations = F.list(c["observations"]).map(F.observation)
            let instance = QuotaInstance.group(observations)[0]
            let segments = QuotaSegments.segments(instance, ledger: ledger, accounts: ["a"], name: observations[0].name)
            let bounds = QuotaSegments.bounds(segments, resetsMs: instance.resetsMs)
            let got: [String: Any] = [
                "S": instance.start, "Shi": instance.startHigh,
                "segments": segments.map { ["type": $0.kind.rawValue, "lo": F.fnum($0.lo), "hi": F.fnum($0.hi), "dp": $0.dp] },
                "instance": bounds.map { ["type": "instance", "lo": F.fnum($0.lo), "hi": F.fnum($0.hi), "dp": $0.pMax] }
                    ?? NSNull(),
            ]
            let diff = F.differences(got, c["expected"])
            #expect(diff.isEmpty, "\(name): \(diff)")
        }
    }

    @Test func vote() {
        let cases = F.list(F.cases["vote"])
        #expect(cases.count == 10)
        for c in cases {
            let items = (c["items"] as? [[Any]] ?? []).map {
                QuotaVoteItem(lo: F.double($0[0])!, hi: F.double($0[1]) ?? .infinity, weight: F.double($0[2])!,
                              id: F.int($0[3])!)
            }
            let result = QuotaVote.vote(items)!
            let above = items.filter { $0.lo > result.low && $0.weight >= QuotaEstimateConstants.minWeight }
                .reduce(0) { $0 + $1.weight }
            let got: [String: Any] = [
                "low": result.low, "high": result.high.isFinite ? result.high : NSNull(),
                "inside": F.round6(result.inside), "insideWindows": result.narrow, "loss": F.round6(result.loss),
                "confidence": QuotaVote.confidence(low: result.low, high: result.high, inside: result.inside,
                                                   narrow: result.narrow, above: above).rawValue,
            ]
            let diff = F.differences(got, c["expected"])
            #expect(diff.isEmpty, "\(c["name"] ?? ""): \(diff)")
        }
    }

    @Test func weights() {
        let cases = F.list(F.cases["weights"])
        #expect(cases.count == 5)
        for c in cases {
            let ageMs = Int64(F.double(c["ageDays"])! * Double(F.day))
            let w = QuotaVote.weight(pMax: F.double(c["dp"])!, tLast: F.t0, nowMs: F.t0 + ageMs,
                                     halfLifeMs: QuotaEstimateWindow.halfLifeMs(minutes: Int(F.int(c["minutes"])!)))
            #expect(F.round6(w) == F.double(c["expected"]), "\(c)")
            #expect((w >= QuotaEstimateConstants.minWeight) == c["used"] as? Bool)
        }
    }

    @Test func estimates() {
        let cases = F.list(F.cases["estimate"])
        #expect(cases.count == 6)
        for c in cases {
            let items = F.list(c["items"]).map {
                QuotaInstanceBounds(resetsMs: F.int($0["window"])!, lo: F.double($0["lo"])!,
                                    hi: F.double($0["hi"]) ?? .infinity, pMax: F.double($0["dp"])!,
                                    tLast: F.t0 - Int64(F.double($0["ageDays"])! * Double(F.day)), offset: false)
            }
            let e = QuotaVote.estimate(items, nowMs: F.t0, halfLifeMs: 7 * F.day)
            let got: [String: Any] = [
                "confidence": e.confidence.rawValue, "cap": e.cap.map(F.round6) ?? NSNull(),
                "low": e.low.map(F.round6) ?? NSNull(), "high": e.high.map(F.round6) ?? NSNull(), "windows": e.windows,
            ]
            let diff = F.differences(got, c["expected"])
            #expect(diff.isEmpty, "\(c["name"] ?? ""): \(diff)")
        }
    }

    @Test func planChangeMovesTheClass() {
        let plan = F.object(F.cases["plan"])
        let ledger = F.ledger(F.list(plan["rows"]))
        var got: [String: Any] = [:]
        for key in ["old", "new"] {
            let group = F.list(plan[key]).map(F.observation)
            let items = QuotaInstance.group(group).compactMap { instance in
                QuotaSegments.bounds(QuotaSegments.segments(instance, ledger: ledger, accounts: ["a"], name: nil),
                                     resetsMs: instance.resetsMs)
            }
            let e = QuotaVote.estimate(items, nowMs: F.t0 + 100 * F.hour, halfLifeMs: 7 * F.day)
            got[group[0].plan!] = ["cap": QuotaEstimateRounding.micro(e.cap) as Any,
                                   "low": QuotaEstimateRounding.micro(e.low) as Any,
                                   "high": QuotaEstimateRounding.micro(e.high) as Any,
                                   "confidence": e.confidence.rawValue]
        }
        let diff = F.differences(got, plan["expected"])
        #expect(diff.isEmpty, "\(diff)")
    }

    @Test func livePercent() {
        let live = F.object(F.cases["live"])
        let ledger = F.ledger(F.list(live["rows"]))
        let cases = F.list(live["cases"])
        #expect(cases.count == 6)
        for c in cases {
            let resets = F.int(c["R"])!, minutes = Int(F.int(c["minutes"])!)
            let result = QuotaLivePercent.compute(
                p: F.double(c["p"])!, t: F.int(c["t"])!, resetsMs: resets,
                start: resets - Int64(minutes) * F.minute, minutes: minutes,
                kind: QuotaWindowKind(rawValue: F.string(c["kind"])!)!, cap: F.double(c["cap"]), ledger: ledger,
                accounts: ["a"], name: nil, nowMs: F.int(c["now"])!, firstAfterReset: F.int(c["firstAfterReset"]))
            let got: [String: Any] = ["live": F.round6(result.percent), "estimated": result.estimated,
                                      "resets": result.resetsMs ?? NSNull()]
            let diff = F.differences(got, c["expected"])
            #expect(diff.isEmpty, "\(c["name"] ?? ""): \(diff)")
        }
    }

    @Test func reportEntries() {
        let report = F.object(F.cases["report"])
        let ledger = F.ledger(F.list(report["rows"]))
        let input = F.object(report["input"])
        let estimate = F.object(input["estimate"])
        let base = QuotaEntryBase(
            account: "a", source: F.string(input["source"])!, sources: input["sources"] as? [String] ?? [],
            plan: F.string(input["plan"]), tier: F.string(input["tier"]),
            kind: QuotaWindowKind(rawValue: F.string(input["kind"])!)!, name: F.string(input["name"]),
            minutes: Int(F.int(input["minutes"])!), p: F.double(input["p"])!, t: F.int(input["t"])!,
            resetsMs: F.int(input["R"])!, start: F.int(input["S"])!,
            estimate: QuotaCapEstimate(confidence: QuotaEstimateConfidence(rawValue: F.string(estimate["confidence"])!)!,
                                       cap: F.double(estimate["cap"]), low: F.double(estimate["low"]),
                                       high: F.double(estimate["high"]), windows: Int(F.int(estimate["windows"])!)),
            firstAfterReset: nil)
        let entries = F.list(report["entries"])
        #expect(entries.count == 3)
        for c in entries {
            var b = base
            if F.string(c["name"]) == "no estimate" { b.estimate = .none }
            let entry = QuotaReportEntry(b, nowMs: F.int(c["now"])!, ledger: ledger, priceVersion: 8)
            let diff = F.differences(F.foundation(entry.json), c["expected"])
            #expect(diff.isEmpty, "\(c["name"] ?? ""): \(diff)")
        }
    }

    @Test func reportCadence() throws {
        let report = F.object(F.cases["report"])
        let first = F.object(F.list(report["entries"])[0]["expected"])
        let window = F.object(first["window"])
        func entry(sources: [String]? = nil, percent: Double? = nil, spend: Int64 = 0) -> QuotaReportEntry {
            QuotaReportEntry(sources: sources ?? (first["sources"] as! [String]),
                             kind: QuotaWindowKind(rawValue: F.string(window["kind"])!)!, name: F.string(window["name"]),
                             minutes: Int(F.int(window["minutes"])!), usedPercent: percent ?? F.double(first["usedPercent"]),
                             resetsInMs: F.int(first["resetsInMs"]), usedMicroUsd: F.int(first["usedMicroUsd"])! + spend,
                             confidence: .medium)
        }
        let a = entry(), b = entry(spend: 5), c = entry(percent: 24)
        let cases = F.list(report["cadence"])
        #expect(cases.count == 6)
        for item in cases {
            let ago = F.int(item["lastSentMsAgo"])
            let now: [QuotaReportEntry] = switch F.string(item["change"]) {
            case "spend": [b]
            case "usedPercent": [c]
            case "set": [a, entry(sources: ["claudeCode"], spend: 5)]
            default: [a]
            }
            let due = QuotaReportCadence.due(now, last: ago == nil ? nil : [a], lastMs: 0, nowMs: ago ?? 0,
                                             leaving: F.string(item["name"]) == "leaving")
            #expect(due == item["expected"] as? Bool, "\(item["name"] ?? "")")
        }
    }
}
