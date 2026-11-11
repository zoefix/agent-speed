import AgentSpeedCore
import Foundation

/// `--dump-quota`: the account limits this computer holds, read-only (QUOTAS 3–6): Codex from today's rollouts,
/// Claude Desktop from its cache, the Claude CLI from the bridge's quota file, and the running and signed-in checks
/// (booleans only; no credential is printed).
enum DumpQuota {
    static func run(configuration: MonitorConfiguration) {
        let nowMs = Int64(Date().timeIntervalSince1970 * 1000)
        var codexOnly = configuration
        codexOnly.enabledSources = [.codex]
        print(line("codex", TranscriptScan.today(configuration: codexOnly).codexQuota, nowMs: nowMs))

        let started = DispatchTime.now().uptimeNanoseconds
        let reader = DesktopQuotaReader()
        let desktop = reader.poll(nowMs: nowMs, desktopRunning: true)
        let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1e6
        print(line("claudeDesktop", desktop, nowMs: nowMs)
            + String(format: " (search: %d headers, %.0f ms, candidates %@)", reader.lastSearchReads, ms,
                     reader.candidateNames.joined(separator: ",")))

        let data = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/AgentSpeed")
        var cli = ClaudeCliQuotaReader(file: ClaudeStatusLineBridge(data: data).quotaFile)
        cli.refresh()
        print(line("claudeCode", cli.snapshot, nowMs: nowMs))

        var codexSignIn = QuotaSignIn(.codex, file: QuotaSignIn.codexFile)
        var claudeSignIn = QuotaSignIn(.claudeCode, file: QuotaSignIn.claudeFile)
        let apps = QuotaProcessScan().scan(nowMs: nowMs)
        print("signedIn codex=\(codexSignIn.refresh(nowMs: nowMs)) claudeCode=\(claudeSignIn.refresh(nowMs: nowMs))")
        print("runs codex=\(apps.codex) claudeCode=\(apps.claudeCode) claudeDesktop=\(apps.claudeDesktop)")
        // Desktop counts as signed in by its snapshot's age (QUOTAS 6).
        if let desktop { print("claudeDesktop snapshot \((nowMs - desktop.observedMs) / 60_000) min old") }
    }

    static func line(_ name: String, _ snapshot: QuotaSnapshot?, nowMs: Int64) -> String {
        guard let snapshot else { return "\(name): none" }
        let format = QuotaFormat(strings: QuotaStrings(resetIn: "resets in %@", resetAt: "resets %@", minutes: "%d min",
                                                       hours: "%d h", hoursMinutes: "%d h %d min", days: "%d d",
                                                       age: "updated %@ ago", pace: "%d%%"),
                                 locale: Locale(identifier: "en-US"))
        let windows = snapshot.shown(nowMs: nowMs).map { window in
            let reset = window.resetsAtMs.map { format.resetLong($0, nowMs: nowMs) } ?? "no reset"
            return "\(window.label) \(window.percentText) (\(reset))"
        }
        let age = format.age(observedMs: snapshot.observedMs, nowMs: nowMs).map { ", \($0)" } ?? ""
        return "\(name): " + (windows.isEmpty ? "no windows" : windows.joined(separator: " · ")) + age
    }
}
