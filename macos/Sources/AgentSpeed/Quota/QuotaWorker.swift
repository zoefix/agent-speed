import AgentSpeedCore
import Foundation

/// The quota readers' file work, off the main thread (QUOTAS 4–6): the Claude Desktop cache, the Claude CLI's quota
/// file, the bridge's status, the signed-in markers and the light process listing. One serial queue runs it.
final class QuotaWorker: @unchecked Sendable {
    /// What one round should look at.
    struct Request: Sendable {
        var nowMs: Int64
        var desktop: Bool
        var desktopRunning: Bool
        var desktopStarted: Bool
        var cli: Bool
        var bridgeStatus: Bool
        /// Each marker only for an enabled source with its quota on (QUOTAS 9.1).
        var codexSignIn: Bool
        var claudeSignIn: Bool
        var scanProcesses: Bool
    }

    /// What it found; nil fields were not looked at.
    struct Result: Sendable {
        var desktop: QuotaSnapshot??
        var cli: QuotaSnapshot??
        var cliReceivedMs: Int64??
        var bridgeStatus: StatusLineSettings.Status?
        var signedIn: Set<SourceKind>?
        var apps: QuotaApps?
        /// The estimate's account keys (QUOTAS 10.1): the ids were turned into keys here.
        var desktopOrgKey: String?
        var codexKey: (key: String, at: Int64)?
        var claudeProfile: QuotaClaudeProfile?
    }

    let queue = DispatchQueue(label: "AgentSpeed.quota", qos: .utility)
    let bridge: ClaudeStatusLineBridge
    private let desktopReader: DesktopQuotaReader
    private var cliReader: ClaudeCliQuotaReader
    private var codexSignIn: QuotaSignIn
    private var claudeSignIn: QuotaSignIn
    private lazy var processes = QuotaProcessScan()
    /// The estimate's keys (QUOTAS 10.1); nil leaves the ids unused.
    private var keys: QuotaAccountKeys?

    init(bridge: ClaudeStatusLineBridge, desktopFolder: URL = DesktopQuotaReader.defaultFolder,
         signInFiles: (codex: URL, claude: URL) = (QuotaSignIn.codexFile, QuotaSignIn.claudeFile)) {
        self.bridge = bridge
        codexSignIn = QuotaSignIn(.codex, file: signInFiles.codex)
        claudeSignIn = QuotaSignIn(.claudeCode, file: signInFiles.claude)
        desktopReader = DesktopQuotaReader(folder: desktopFolder)
        cliReader = ClaudeCliQuotaReader(file: bridge.quotaFile)
    }

    func setKeys(_ next: QuotaAccountKeys?) {
        queue.async { [self] in keys = next }
    }

    func run(_ request: Request, completion: @escaping @Sendable (Result) -> Void) {
        queue.async { [self] in completion(round(request)) }
    }

    /// Queue work that changes the bridge (turning it on or off), in order with the reads.
    func perform<T: Sendable>(_ body: @escaping @Sendable (ClaudeStatusLineBridge) -> T,
                              completion: @escaping @Sendable (T) -> Void) {
        queue.async { [self] in
            let value = body(bridge)
            cliReader = ClaudeCliQuotaReader(file: bridge.quotaFile)
            completion(value)
        }
    }

    private func round(_ request: Request) -> Result {
        var result = Result()
        if request.desktop {
            if request.desktopStarted { desktopReader.requestSearch() }
            result.desktop = .some(desktopReader.poll(nowMs: request.nowMs, desktopRunning: request.desktopRunning))
            if let keys, let organization = desktopReader.organization {
                result.desktopOrgKey = keys.desktop(organization: organization)
            }
        }
        if request.cli {
            cliReader.refresh()
            result.cli = .some(cliReader.snapshot)
            result.cliReceivedMs = .some(cliReader.receivedMs)
        }
        if request.bridgeStatus { result.bridgeStatus = bridge.status() }
        if request.codexSignIn || request.claudeSignIn {
            var signedIn: Set<SourceKind> = []
            if request.codexSignIn, codexSignIn.refresh(nowMs: request.nowMs) { signedIn.insert(.codex) }
            if request.claudeSignIn, claudeSignIn.refresh(nowMs: request.nowMs) { signedIn.insert(.claudeCode) }
            result.signedIn = signedIn
            if let keys {
                if request.codexSignIn, let identity = codexSignIn.identity, let key = keys.codex(identity) {
                    result.codexKey = (key, codexSignIn.identityMs ?? request.nowMs)
                }
                if request.claudeSignIn, let identity = claudeSignIn.identity {
                    result.claudeProfile = keys.claude(identity, at: claudeSignIn.identityMs ?? request.nowMs)
                }
            }
        }
        if request.scanProcesses { result.apps = processes.scan(nowMs: request.nowMs) }
        return result
    }
}
