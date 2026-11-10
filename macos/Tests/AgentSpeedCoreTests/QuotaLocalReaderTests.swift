import Foundation
import Testing
@testable import AgentSpeedCore

/// Sign-in files, Grok's log tail and the Antigravity server rules (QUOTAS 9.2, 9.4–9.7).
struct QuotaLocalReaderTests {
    @Test func signInFiles() throws {
        let home = try TempDirectory()
        #expect(QuotaCredentials.kimi(home: home.url, environment: [:]) == nil)
        try home.write(".kimi/credentials/kimi-code.json", #"{"access_token":"legacy","expires_at":1900000000123}"#)
        #expect(QuotaCredentials.kimi(home: home.url, environment: [:])?.value == "legacy")
        #expect(QuotaCredentials.kimi(home: home.url, environment: [:])?.expiresAtMs == 1_900_000_000_123)
        try home.write(".kimi-code/credentials/kimi-code.json", #"{"refresh_token":"r","access_token":"current","expires_at":1900000000}"#)
        #expect(QuotaCredentials.kimi(home: home.url, environment: [:])?.value == "current")
        try home.write("custom/credentials/kimi-code.json", #"{"access_token":"custom"}"#)
        #expect(QuotaCredentials.kimi(home: home.url, environment: ["KIMI_CODE_HOME": home.file("custom").path])?.value == "custom")

        try home.write("g/.gemini/oauth_creds.json", #"{"access_token":"ya29.x","expiry_date":1794338773000}"#)
        let gemini = QuotaCredentials.gemini(home: home.url, environment: ["GEMINI_CLI_HOME": home.file("g").path])
        #expect(gemini?.value == "ya29.x")
        #expect(gemini?.isExpired(nowMs: 1_794_338_773_000) == true)
        #expect(gemini?.isExpired(nowMs: 1_794_338_772_999) == false)

        try home.write(".grok/auth.json", #"{"https://a":{"access_token":"acc","expires_at":"2026-11-10T19:27:00Z"},"https://b":{"key":"k2"}}"#)
        let grok = try #require(QuotaCredentials.grok(home: home.url, environment: [:]))
        #expect(grok.value == "acc")
        #expect(grok.isExpired(nowMs: 1_794_338_773_000, marginMs: 60_000))
        #expect(!grok.isExpired(nowMs: 1_794_338_700_000, marginMs: 60_000))
        try home.write(".grok/auth.json", #"[1]"#)
        #expect(QuotaCredentials.grok(home: home.url, environment: [:]) == nil)
        #expect(QuotaCredentials.droid(home: home.url, environment: [:]) == nil)
    }

    @Test func grokLogTailSkipsACutFirstLine() throws {
        let home = try TempDirectory()
        let config = #"{"ts":"2026-11-10T12:00:00Z","msg":"billing: fetched credits config","ctx":{"config":{"creditUsagePercent":33}}}"#
        let filler = String(repeating: "x", count: 1024)
        var text = ""
        for _ in 0..<(GrokLogReader.tailBytes / 1025 + 8) { text += #"{"msg":"\#(filler)"}"# + "\n" }
        text += config + "\n"
        let file = try home.write(".grok/logs/unified.jsonl", text)
        let size = try #require(FileStat.stat(file.path)?.size)
        let lines = try #require(GrokLogReader.tailLines(file, size: size))
        #expect(lines.last == Data(config.utf8))
        #expect(lines.allSatisfy { (try? JSONParser.parse($0)) != nil })
        var reader = GrokLogReader(file: file)
        let first = reader.refresh()
        #expect(first)
        #expect(reader.snapshot?.windows.first?.usedPercent == 33)
        let again = reader.refresh()
        #expect(!again)
        #expect(GrokLogReader.defaultFile(home: home.url, environment: ["GROK_HOME": "/x"]).path == "/x/logs/unified.jsonl")
    }

    @Test func antigravityServers() throws {
        let args = ["/Applications/Antigravity.app/Contents/Resources/bin/language_server_macos_arm", "--csrf_token", "c1",
                    "--extension_server_port=4100", "--extension_server_csrf_token", "e1", "--app_data_dir", "antigravity"]
        #expect(AntigravityRules.isLanguageServer(executable: args[0], arguments: args))
        #expect(AntigravityRules.isLanguageServer(executable: "/opt/language_server_linux_x64",
                                                  arguments: ["x", "--app_data_dir", "/home/u/.antigravity"]))
        #expect(!AntigravityRules.isLanguageServer(executable: "/opt/windsurf/language_server_macos",
                                                   arguments: ["x", "--app_data_dir", "/u/.windsurf"]))
        #expect(!AntigravityRules.isLanguageServer(executable: "/bin/zsh", arguments: ["--app_data_dir", "antigravity"]))
        let server = AntigravityRules.languageServer(pid: 9, arguments: args, listening: [4100, 4200])
        #expect(server.endpoints == [.init(scheme: "http", port: 4100, token: "e1"), .init(scheme: "https", port: 4100, token: "c1"),
                                     .init(scheme: "http", port: 4200, token: "c1"), .init(scheme: "https", port: 4200, token: "c1")])
        let agy = try #require(AntigravityRules.agy(pid: 10, environment: ["ANTIGRAVITY_CSRF_TOKEN": "a1",
                                                                          "ANTIGRAVITY_LS_ADDRESS": "127.0.0.1:4300"], listening: []))
        #expect(agy.endpoints.map(\.port) == [4300, 4300])
        #expect(agy.endpoints.allSatisfy { $0.token == "a1" })
        #expect(AntigravityRules.agy(pid: 10, environment: ["ANTIGRAVITY_LS_ADDRESS": "10.0.0.2:4300"], listening: []) == nil)
        let daemon = try #require(AntigravityRules.daemon(Data(#"{"pid":42,"csrfToken":"d1","httpsPort":4400,"httpPort":4401}"#.utf8)))
        #expect(daemon.pid == 42)
        #expect(daemon.endpoints == [.init(scheme: "http", port: 4401, token: "d1"), .init(scheme: "https", port: 4400, token: "d1")])
        #expect(AntigravityRules.daemon(Data(#"{"pid":0,"httpPort":1}"#.utf8)) == nil)
    }

    @Test func onlyTheNamedVariablesOfAnAgyEnvironment() {
        var bytes: [UInt8] = withUnsafeBytes(of: Int32(2).littleEndian, Array.init)
        bytes += Array("/usr/local/bin/agy".utf8) + [0, 0, 0]
        bytes += Array("agy".utf8) + [0] + Array("--serve".utf8) + [0]
        for entry in ["HOME=/Users/me", "SECRET_TOKEN=keep-out", "ANTIGRAVITY_CSRF_TOKEN=tok", "ANTIGRAVITY_LS_ADDRESS=127.0.0.1:1"] {
            bytes += Array(entry.utf8) + [0]
        }
        bytes += [0, 0]
        let found = bytes.withUnsafeBytes {
            AntigravityProcessLocator.parseEnvironment($0, names: AntigravityRules.environmentNames)
        }
        #expect(found == ["ANTIGRAVITY_CSRF_TOKEN": "tok", "ANTIGRAVITY_LS_ADDRESS": "127.0.0.1:1"])
    }

    @Test func devinLaunchFolders() {
        let taipei = TimeZone(identifier: "Asia/Taipei")!
        #expect(DevinPlan.launchMs("20261111T032000", timeZone: taipei) == 1_794_338_400_000)
        #expect(DevinPlan.launchMs("2026111T032000", timeZone: taipei) == nil)
        #expect(DevinPlan.launchMs("20261311T032000", timeZone: taipei) == nil)
        #expect(DevinPlan.launchMs("main.log", timeZone: taipei) == nil)
    }

    @Test func vendorTimes() {
        #expect(VendorValue.timeMs(.string("2026-11-10T22:00:00")) == 1_794_348_000_000)
        #expect(VendorValue.timeMs(.string("2026-11-10")) == 1_794_268_800_000)
        #expect(VendorValue.timeMs(.string("1794800000")) == 1_794_800_000_000)
        #expect(VendorValue.timeMs(.number(JSONLiteral("1794800000000"))) == 1_794_800_000_000)
        #expect(VendorValue.timeMs(.string("soon")) == nil)
        #expect(VendorValue.timeMs(.bool(true)) == nil)
        #expect(VendorValue.number(.string(" 2.5 ")) == 2.5)
        #expect(VendorValue.number(.bool(true)) == nil)
    }
}
