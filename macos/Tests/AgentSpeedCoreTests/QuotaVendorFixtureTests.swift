import Foundation
import Testing
@testable import AgentSpeedCore

/// Every phase 2 case of `spec/fixtures/quota-cases.json` (`vendors`, QUOTAS 9, 10) through the Swift readers.
struct QuotaVendorFixtureTests {
    static let vendors: JSONValue = {
        let url = Fixtures.root.appendingPathComponent("quota-cases.json")
        let data = (try? Data(contentsOf: url)) ?? Data()
        return (try? JSONParser.parse(data))?["vendors"] ?? .null
    }()

    static func item(_ name: String) throws -> JSONValue {
        try #require(vendors[name], "missing vendors.\(name)")
    }

    /// The reference rounds percents to 6 digits.
    static func expectWindows(_ actual: [QuotaWindow]?, _ expected: JSONValue?, _ name: String,
                              sourceLocation: SourceLocation = #_sourceLocation) {
        guard let expected = expected?.array else {
            #expect(actual == nil, "\(name)", sourceLocation: sourceLocation)
            return
        }
        guard let actual else {
            Issue.record("\(name): no windows", sourceLocation: sourceLocation)
            return
        }
        #expect(actual.count == expected.count, "\(name) count", sourceLocation: sourceLocation)
        for (window, want) in zip(actual, expected) {
            #expect(window.kind.rawValue == want["kind"]?.string, "\(name) kind", sourceLocation: sourceLocation)
            #expect(window.name == want["name"]?.string, "\(name) name", sourceLocation: sourceLocation)
            #expect(window.minutes.map(Int64.init) == want["minutes"]?.int64, "\(name) minutes", sourceLocation: sourceLocation)
            #expect(window.resetsAtMs == want["resetsAtMs"]?.int64, "\(name) reset", sourceLocation: sourceLocation)
            let used = want["usedPercent"]?.literal?.double ?? .nan
            #expect(abs(window.usedPercent - used) < 1e-5, "\(name) used \(window.usedPercent) vs \(used)",
                    sourceLocation: sourceLocation)
        }
    }

    static func bytes(hex: String) -> [UInt8] {
        var out: [UInt8] = []
        var index = hex.startIndex
        while index < hex.endIndex, let next = hex.index(index, offsetBy: 2, limitedBy: hex.endIndex) {
            out.append(UInt8(hex[index..<next], radix: 16) ?? 0)
            index = next
        }
        return out
    }

    // MARK: Grok Build (9.2)

    @Test func grokFromTheLog() throws {
        let item = try Self.item("grokLog")
        let lines = (item["lines"]?.array ?? []).compactMap(\.string).map { Data($0.utf8) }
        let found = GrokBilling.fromLog(lines)
        #expect(found?.observedMs == item["expected"]?["observedMs"]?.int64)
        Self.expectWindows(found?.windows, item["expected"]?["windows"], "grokLog")
    }

    @Test func grokFromTheApiAndAnUnknownPeriod() throws {
        let api = try Self.item("grokApi")
        Self.expectWindows(GrokBilling.fromResponse(try #require(api["body"])), api["expected"], "grokApi")
        let unknown = try Self.item("grokUnknown")
        Self.expectWindows(GrokBilling.windows(unknown["config"]?.object ?? [:]), unknown["expected"], "grokUnknown")
    }

    // MARK: Devin (9.3)

    @Test func devinRows() throws {
        let item = try Self.item("devin")
        let rows = (item["rows"]?.array ?? []).map { row -> (key: String, value: Data) in
            (row.array?[0].string ?? "", Data((row.array?[1].string ?? "").utf8))
        }
        Self.expectWindows(DevinPlan.windows(rows), item["expected"], "devin")
    }

    // MARK: Antigravity (9.4)

    @Test func antigravitySummaryAndStatus() throws {
        let summary = try Self.item("antigravity")
        Self.expectWindows(AntigravitySummary.summaryWindows(try #require(summary["body"])), summary["expected"], "antigravity")
        let status = try Self.item("antigravityStatus")
        Self.expectWindows(AntigravitySummary.statusWindows(try #require(status["body"])), status["expected"], "antigravityStatus")
    }

    // MARK: Kimi, Gemini, Droid, z.ai (9.5–9.8)

    @Test func kimiShapes() throws {
        for name in ["kimiCurrent", "kimiLegacy"] {
            let item = try Self.item(name)
            Self.expectWindows(KimiUsage.windows(try #require(item["body"])), item["expected"], name)
        }
    }

    @Test func geminiProjectAndBuckets() throws {
        let load = try Self.item("geminiProject")
        #expect(GeminiQuota.project(try #require(load["body"])) == load["expected"]?.string)
        #expect(GeminiQuota.project(.object(["cloudaicompanionProject": .string("p-1")])) == "p-1")
        #expect(GeminiQuota.project(.object([:])) == nil)
        let item = try Self.item("gemini")
        Self.expectWindows(GeminiQuota.windows(try #require(item["body"])), item["expected"], "gemini")
    }

    @Test func droidEnvFileAndLimits() throws {
        for pair in Self.vendors["droidEnv"]?.array ?? [] {
            let text = pair.array?[0].string ?? ""
            #expect(DroidLimits.key(fromEnvFile: text) == pair.array?[1].string, "\(text)")
        }
        #expect((Self.vendors["droidEnv"]?.array ?? []).count == 3)
        let item = try Self.item("droid")
        let now = try #require(item["nowMs"]?.int64)
        Self.expectWindows(DroidLimits.windows(try #require(item["body"]), nowMs: now), item["expected"], "droid")
    }

    @Test func zaiLimitsAndFailure() throws {
        let item = try Self.item("zcode")
        let now = try #require(item["nowMs"]?.int64)
        Self.expectWindows(ZaiLimits.windows(try #require(item["body"]), nowMs: now), item["expected"], "zcode")
        let failure = try Self.item("zcodeFailure")
        #expect(failure["expected"]?.isNull == true)
        #expect(ZaiLimits.windows(try #require(failure["body"]), nowMs: now) == nil)
    }

    // MARK: Qoder and WorkBuddy (9.9)

    @Test func browserSessionSummaries() throws {
        for name in ["qoder", "qoderPercent"] {
            let item = try Self.item(name)
            Self.expectWindows(BrowserSessionCredits.qoderWindows(try #require(item["body"])), item["expected"], name)
        }
        let item = try Self.item("workbuddy")
        Self.expectWindows(BrowserSessionCredits.workBuddyWindows(try #require(item["body"])), item["expected"], "workbuddy")
        #expect(BrowserSessionCredits.workBuddyWindows(.object(["code": .number(JSONLiteral("7"))])) == nil)
    }

    @Test func chromeUserAgent() {
        #expect(BrowserUserAgent.chrome(major: 142) == Self.vendors["userAgent"]?.string)
    }

    // MARK: Browser cookies (9.10)

    @Test func chromeCbcVectors() throws {
        let cookies = try Self.item("cookies")
        for name in ["macV10", "macV10HostHash", "linuxV10", "linuxV11HostHash"] {
            let vector = try #require(cookies[name], "\(name)")
            let password = Array((vector["password"]?.string ?? "").utf8)
            let iterations = Int(vector["iterations"]?.int64 ?? 0)
            let key = try #require(ChromeCookieCrypto.cbcKey(password: password, iterations: iterations), "\(name)")
            let blob = Self.bytes(hex: vector["blob"]?.string ?? "")
            let meta = Int(vector["metaVersion"]?.int64 ?? 0)
            #expect(ChromeCookieCrypto.decryptCBC(blob, key: key, metaVersion: meta) == vector["expected"]?.string, "\(name)")
            // A wrong password never gives a value.
            let wrong = try #require(ChromeCookieCrypto.cbcKey(password: Array("nope".utf8), iterations: iterations))
            #expect(ChromeCookieCrypto.decryptCBC(blob, key: wrong, metaVersion: meta) != vector["expected"]?.string, "\(name) wrong")
        }
        #expect(ChromeCookieCrypto.macIterations == 1003)
    }

    @Test func windowsGcmVector() throws {
        let vector = try #require(try Self.item("cookies")["windowsGcm"])
        let nonce = Self.bytes(hex: vector["nonce"]?.string ?? "")
        let sealed = Self.bytes(hex: vector["ciphertextAndTag"]?.string ?? "")
        let blobText = try #require(vector["blob"]?.string)
        #expect(blobText.hasPrefix("v10"))
        #expect(Self.bytes(hex: String(blobText.dropFirst(3))) == nonce)
        let blob = Array("v10".utf8) + nonce + sealed
        let key = Self.bytes(hex: vector["key"]?.string ?? "")
        #expect(ChromeCookieCrypto.decryptGCM(blob, key: key, metaVersion: 23) == vector["plaintext"]?.string)
        var tampered = blob
        tampered[tampered.count - 1] ^= 1
        #expect(ChromeCookieCrypto.decryptGCM(tampered, key: key, metaVersion: 23) == nil)
        #expect(ChromeCookieCrypto.decryptGCM(Array("v20".utf8) + nonce + sealed, key: key, metaVersion: 23) == nil)
    }

    @Test func cookieHeader() throws {
        let item = try Self.item("cookieHeader")
        let rows = (item["rows"]?.array ?? []).map { row -> BrowserCookie in
            let cells = row.array ?? []
            return BrowserCookie(host: cells[0].string ?? "", name: cells[1].string ?? "", value: cells[2].string ?? "",
                                 expiresS: cells[3].int64 ?? 0)
        }
        let header = CookieHeader.join(rows, site: item["site"]?.string ?? "", nowS: item["nowS"]?.int64 ?? 0)
        #expect(header == item["expected"]?.string)
        #expect(CookieHeader.join([], site: "qoder.com", nowS: 0) == nil)
        let injected = BrowserCookie(host: "qoder.com", name: "a", value: "b\r\nX-Evil: 1", expiresS: 0)
        #expect(CookieHeader.join([injected], site: "qoder.com", nowS: 0) == nil)
    }
}
