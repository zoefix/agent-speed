import Foundation

/// Which apps of the quota sources run (QUOTAS 6, 9): a `codex` agent process or the Codex app's main process, a
/// `claudeCode` agent process, Claude Desktop's main process, Devin Desktop's, and every other source's agent processes.
public struct QuotaApps: Equatable, Hashable, Sendable {
    public var codex: Bool
    public var claudeCode: Bool
    public var claudeDesktop: Bool
    public var devinDesktop: Bool
    /// The sources with a live agent process (METRICS 10.5).
    public var agents: Set<SourceKind>

    public init(codex: Bool = false, claudeCode: Bool = false, claudeDesktop: Bool = false, devinDesktop: Bool = false,
                agents: Set<SourceKind> = []) {
        self.codex = codex
        self.claudeCode = claudeCode
        self.claudeDesktop = claudeDesktop
        self.devinDesktop = devinDesktop
        self.agents = agents
    }

    public static let none = QuotaApps()

    public func runs(_ source: SourceKind) -> Bool {
        switch source {
        case .codex: codex
        case .claudeCode: claudeCode
        case .claudeDesktop: claudeDesktop
        case .devin: devinDesktop || agents.contains(.devin)
        default: agents.contains(source)
        }
    }

    /// Every source that runs, for the vendor readers' cadence (QUOTAS 9.1).
    public var running: Set<SourceKind> {
        var all = agents
        if codex { all.insert(.codex) }
        if claudeCode { all.insert(.claudeCode) }
        if claudeDesktop { all.insert(.claudeDesktop) }
        if devinDesktop { all.insert(.devin) }
        return all
    }

    /// The Codex app's own executable, never an agent process (METRICS 10.5).
    static let codexApp = ["/Applications/Codex.app/Contents/MacOS/Codex"]
    static let devinApp = ["/Applications/Devin.app/Contents/MacOS/Devin"]

    /// From the traffic process table (METRICS 10.5): its live roots and the apps' main processes.
    static func of(_ table: ProcessTable) -> QuotaApps {
        var apps = QuotaApps()
        let platform = table.rules.platform
        for record in table.records.values where record.alive {
            if let processClass = record.processClass, processClass.role == .agent {
                if processClass.source == .codex { apps.codex = true }
                if processClass.source == .claudeCode { apps.claudeCode = true }
                apps.agents.insert(processClass.source)
            } else if record.processClass == nil, !record.executable.isEmpty {
                if table.rules.isClaudeDesktopApp(record.executable) { apps.claudeDesktop = true }
                if platform.isUnder(record.executable, codexApp) { apps.codex = true }
                if platform.isUnder(record.executable, devinApp) { apps.devinDesktop = true }
            }
        }
        return apps
    }
}

/// A light listing of the user's processes for the quota card while the traffic sampler keeps no process table
/// (QUOTAS 6): the same table and rules, refreshed when asked (every 15 s while an orb shows the card).
public final class QuotaProcessScan: @unchecked Sendable {
    private let table = ProcessTable(platform: .current)
    private let reader = ProcessReader()
    private let lock = NSLock()

    public init() {}

    public func scan(nowMs: Int64) -> QuotaApps {
        lock.lock()
        defer { lock.unlock() }
        table.refresh(reader.list(nowMs: nowMs), inspector: reader, nowMs: nowMs)
        return QuotaApps.of(table)
    }
}

/// The account a sign-in file names, for the estimate's keys (QUOTAS 10.1): kept in memory only and turned into a key
/// at once, never stored or logged.
public enum QuotaSignInIdentity: Equatable, Sendable {
    /// `auth.json` `tokens.account_id`.
    case codex(accountId: String)
    /// `.claude.json` `oauthAccount`.
    case claude(organization: String, account: String, organizationType: String?, tier: String?)
}

/// The signed-in markers of QUOTAS 6. Only the named keys are looked at; the files are scanned in memory, nothing of
/// them is copied, kept or logged, and no token ever becomes a string. The account ids of QUOTAS 10.1 stay in memory
/// for the estimate's keys. A file is read again only when it changed, and at most once a minute.
public struct QuotaSignIn: Sendable {
    public static let rereadAfterMs: Int64 = 60_000
    static let maxFileBytes = 64 << 20

    public enum Marker: Sendable {
        /// `auth.json`: an object with `auth_mode == "chatgpt"` or a `tokens` object.
        case codex
        /// `.claude.json`: an `oauthAccount` object.
        case claudeCode
    }

    public let marker: Marker
    public let file: URL
    private var stamp: (size: Int64, modifiedNs: Int64)?
    private var readMs: Int64?
    public private(set) var signedIn = false
    /// The account of the last read and the file's modification time then (capped at the read).
    public private(set) var identity: QuotaSignInIdentity?
    public private(set) var identityMs: Int64?

    public init(_ marker: Marker, file: URL) {
        self.marker = marker
        self.file = file
    }

    /// `$CODEX_HOME/auth.json`, else `~/.codex/auth.json`.
    public static var codexFile: URL {
        let environment = ProcessInfo.processInfo.environment
        if let home = environment["CODEX_HOME"], !home.isEmpty {
            return URL(fileURLWithPath: (home as NSString).expandingTildeInPath).appendingPathComponent("auth.json")
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex/auth.json")
    }

    /// `$CLAUDE_CONFIG_DIR/.claude.json`, else `~/.claude.json`.
    public static var claudeFile: URL {
        let environment = ProcessInfo.processInfo.environment
        if let folder = environment["CLAUDE_CONFIG_DIR"], !folder.isEmpty {
            return URL(fileURLWithPath: (folder as NSString).expandingTildeInPath).appendingPathComponent(".claude.json")
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude.json")
    }

    /// Returns whether the marker says signed in.
    @discardableResult
    public mutating func refresh(nowMs: Int64) -> Bool {
        guard let stat = FileStat.stat(file.path), stat.isRegularFile else {
            stamp = nil
            signedIn = false
            identity = nil
            return false
        }
        let changed = stamp.map { $0.size != stat.size || $0.modifiedNs != stat.modifiedNs } ?? true
        guard changed, readMs.map({ nowMs - $0 >= Self.rereadAfterMs }) ?? true else { return signedIn }
        readMs = nowMs
        stamp = (stat.size, stat.modifiedNs)
        guard stat.size <= Int64(Self.maxFileBytes), let data = try? Data(contentsOf: file, options: .uncached) else {
            signedIn = false
            identity = nil
            return false
        }
        let read = data.withUnsafeBytes { Self.read(marker, $0) }
        signedIn = read.found
        identity = read.identity
        identityMs = min(nowMs, stat.modifiedNs / 1_000_000)
        return signedIn
    }

    /// Scans the top-level object for the marker's keys, skipping (and validating) everything else.
    static func check(_ marker: Marker, _ bytes: UnsafeRawBufferPointer) -> Bool {
        read(marker, bytes).found
    }

    /// The marker and the account ids beside it: only `tokens.account_id`, or `oauthAccount`'s organization, account,
    /// organization type and rate limit tier; every other value is skipped unread.
    static func read(_ marker: Marker, _ bytes: UnsafeRawBufferPointer) -> (found: Bool, identity: QuotaSignInIdentity?) {
        var scanner = JSONScanner(bytes)
        var found = false
        var identity: QuotaSignInIdentity?
        do {
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                switch marker {
                case .codex where scanner.key(key, is: "auth_mode"):
                    if try scanner.readStringMatch(["chatgpt"]) == 0 { found = true }
                case .codex where scanner.key(key, is: "tokens"):
                    guard try scanner.beginObjectIfPresent() else { continue }
                    found = true
                    var accountId: String?
                    while let member = try scanner.nextMember() {
                        if scanner.key(member, is: "account_id") {
                            accountId = try scanner.readNonEmptyString()
                        } else {
                            try scanner.skipValue()
                        }
                    }
                    identity = accountId.map { .codex(accountId: $0) }
                case .claudeCode where scanner.key(key, is: "oauthAccount"):
                    guard try scanner.beginObjectIfPresent() else { continue }
                    found = true
                    var organization: String?, account: String?, type: String?, tier: String?
                    while let member = try scanner.nextMember() {
                        if scanner.key(member, is: "organizationUuid") {
                            organization = try scanner.readNonEmptyString()
                        } else if scanner.key(member, is: "accountUuid") {
                            account = try scanner.readNonEmptyString()
                        } else if scanner.key(member, is: "organizationType") {
                            type = try scanner.readNonEmptyString()
                        } else if scanner.key(member, is: "organizationRateLimitTier") {
                            tier = try scanner.readNonEmptyString()
                        } else {
                            try scanner.skipValue()
                        }
                    }
                    if let organization, let account {
                        identity = .claude(organization: organization, account: account, organizationType: type, tier: tier)
                    }
                default:
                    try scanner.skipValue()
                }
            }
            try scanner.finish()
        } catch {
            return (false, nil)
        }
        return (found, identity)
    }
}
