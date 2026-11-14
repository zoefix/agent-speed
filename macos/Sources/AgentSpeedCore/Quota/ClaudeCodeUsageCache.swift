import Foundation

/// Claude Code's own cache of its last claude.ai usage response (QUOTAS 5.6): `.claude.json`'s top-level
/// `cachedUsageUtilization`, read with the signed-in marker's cadence (QUOTAS 6). Only that key and
/// `oauthAccount.accountUuid` are looked at; the two uuids are compared during the scan and never kept.
public struct ClaudeCodeUsageCache: Sendable {
    /// One read of the file.
    enum Outcome: Equatable {
        /// The file has a cache: its windows by QUOTAS 4.3, at `fetchedAtMs`.
        case cache(QuotaSnapshot)
        case none
        /// The file does not parse: the previous value stays.
        case unparsed
    }

    public let file: URL
    private var stamp: (size: Int64, modifiedNs: Int64)?
    private var readMs: Int64?
    public private(set) var snapshot: QuotaSnapshot?

    public init(file: URL = QuotaSignIn.claudeFile) {
        self.file = file
    }

    /// Reads the file when it changed and a minute has passed since the last read; returns true when the cache
    /// changed. After a file that did not parse (or could not be read), the next check after the minute reads it again.
    @discardableResult
    public mutating func refresh(nowMs: Int64) -> Bool {
        let before = snapshot
        guard let stat = FileStat.stat(file.path), stat.isRegularFile else {
            stamp = nil
            snapshot = nil
            return before != nil
        }
        let changed = stamp.map { $0.size != stat.size || $0.modifiedNs != stat.modifiedNs } ?? true
        guard changed, readMs.map({ nowMs - $0 >= QuotaSignIn.rereadAfterMs }) ?? true else { return false }
        readMs = nowMs
        stamp = (stat.size, stat.modifiedNs)
        guard stat.size <= Int64(QuotaSignIn.maxFileBytes) else {
            snapshot = nil
            return before != nil
        }
        guard let data = try? Data(contentsOf: file, options: .uncached) else {
            stamp = nil
            return false
        }
        switch data.withUnsafeBytes(Self.read) {
        case .cache(let next): snapshot = next
        case .none: snapshot = nil
        case .unparsed: stamp = nil
        }
        return snapshot != before
    }

    /// Forgets the value (the route is off).
    public mutating func reset() {
        stamp = nil
        readMs = nil
        snapshot = nil
    }

    /// Scans the whole document; every member but the two named is skipped (and validated).
    static func read(_ bytes: UnsafeRawBufferPointer) -> Outcome {
        var scanner = JSONScanner(bytes)
        var signedIn: String?, cached: String?, fetchedMs: Int64?, body: JSONValue?
        var hasCache = false
        do {
            guard scanner.nextIsObject else {
                try scanner.skipValue()
                try scanner.finish()
                return .none
            }
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "oauthAccount") {
                    signedIn = nil
                    guard try scanner.beginObjectIfPresent() else { continue }
                    while let member = try scanner.nextMember() {
                        if scanner.key(member, is: "accountUuid") {
                            signedIn = try scanner.readNonEmptyString()
                        } else {
                            try scanner.skipValue()
                        }
                    }
                } else if scanner.key(key, is: "cachedUsageUtilization") {
                    (hasCache, cached, fetchedMs, body) = (false, nil, nil, nil)
                    guard try scanner.beginObjectIfPresent() else { continue }
                    hasCache = true
                    while let member = try scanner.nextMember() {
                        if scanner.key(member, is: "fetchedAtMs") {
                            fetchedMs = try scanner.readNumber().flatMap(Self.time)
                        } else if scanner.key(member, is: "accountUuid") {
                            cached = try scanner.readNonEmptyString()
                        } else if scanner.key(member, is: "utilization") {
                            if scanner.nextIsObject {
                                body = try scanner.withStoredText { try? JSONParser.parse(Data($0)) }
                            } else {
                                body = nil
                                try scanner.skipValue()
                            }
                        } else {
                            try scanner.skipValue()
                        }
                    }
                } else {
                    try scanner.skipValue()
                }
            }
            try scanner.finish()
        } catch {
            return .unparsed
        }
        guard hasCache, let fetchedMs, let body, body.object != nil else { return .none }
        if let signedIn, let cached, signedIn != cached { return .none }
        return .cache(QuotaSnapshot(source: .claudeCode, observedMs: fetchedMs, windows: DesktopUsage.windows(body)))
    }

    /// The integer part of a number when it is a time within 2000–2100 (REMOTE 5.6).
    private static func time(_ number: JSONNumber) -> Int64? {
        guard number.isFinite, (RemoteFields.earliestMs...RemoteFields.latestMs).contains(number.truncated) else {
            return nil
        }
        return number.truncated
    }
}
