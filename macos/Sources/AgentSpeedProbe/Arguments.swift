import AgentSpeedCore
import Foundation

struct ProbeArguments {
    enum Mode {
        case dumpToday
        case dumpHistory
        /// The copied unpriced diagnostics text (METRICS 9.10), as `reference.py --dump-unpriced`.
        case dumpUnpriced(UnpricedOptions)
        case watch(seconds: Int)
        /// Live network traffic for real (METRICS 10).
        case traffic(seconds: Int)
        /// One real price-table sync round into a scratch folder (NETWORK 3).
        case syncPrices(directory: String)
        /// One real exchange-rate round into a scratch folder (NETWORK 4).
        case syncRates(directory: String, currency: String)
        /// The quotas this computer holds (QUOTAS 3–6), read-only.
        case dumpQuota
    }

    var mode: Mode
    var configuration: MonitorConfiguration
    /// `--utc`: group history days in UTC instead of the local time zone.
    var utc = false
    /// `--history-store PATH`: reuse (and update) a persistent history store instead of analysing in memory.
    var historyStore: String?
    /// `--prices PATH`: price with this table instead of the bundled one (METRICS 9), as `reference.py --prices`.
    var prices: String?
    /// `--traffic-interval N`: a fixed nettop interval (0 forces standby) instead of the adaptive cadence.
    var trafficInterval: Int?
    /// `--no-live`: traffic without the live transcript monitor (no per-agent attribution), to measure its own cost.
    var noLive = false
    /// `--now MS`: `--dump-today` evaluates the performance scores at this Unix ms instead of the capture time.
    var now: Int64?
    /// `--remote DESTINATION` (repeatable): `--watch` also watches these SSH hosts (REMOTE).
    var remotes: [String] = []
    /// `--history` (with `--remote`): also syncs the hosts' daily history (REMOTE 8) into `--history-store` or a
    /// scratch store.
    var remoteHistory = false

    /// The range and source of `--dump-unpriced`.
    struct UnpricedOptions: Equatable {
        /// `--days N`: the last N local days with today (default 30).
        var days: Int?
        var first: String?
        var last: String?
        /// `--live`: today's engine responses instead of the history.
        var live = false
        var source: SourceKind?
    }

    static let usage = """
        usage: AgentSpeedProbe (--dump-today [--now MS] | --dump-history [--utc] [--history-store PATH]
                                | --dump-unpriced [--days N | --first YYYY-MM-DD --last YYYY-MM-DD | --live]
                                  [--source ID] [--history-store PATH]
                                | --watch SECONDS [--remote DESTINATION... [--history] [--history-store PATH]]
                                | --traffic SECONDS [--traffic-interval 0|1|5] [--no-live] [--history-store PATH])
                               [--prices PATH] [--codex LOCATION]... [--claude LOCATION]... [--dsh LOCATION]...
                               [--opencode LOCATION]... [--gemini LOCATION]... [--omp LOCATION]...
                               [--antigravity LOCATION]... [--workbuddy LOCATION]... [--workbuddy-ai LOCATION]...
                               [--zcode LOCATION]... [--kimi LOCATION]... [--grok-build LOCATION]... [--pi LOCATION]...
                               [--openclaw LOCATION]... [--hermes LOCATION]... [--droid LOCATION]...
                               [--devin LOCATION]... [--qoder LOCATION]... [--qoder-cli LOCATION]...
               LOCATION: a path, or @auto for the source's automatic roots; repeat a flag to read several
               AgentSpeedProbe --sync-prices DIR | --sync-rates DIR CURRENCY | --dump-quota [--codex LOCATION]...
        """

    static func parse(_ arguments: [String]) -> ProbeArguments? {
        var mode: Mode?
        var configuration = MonitorConfiguration()
        var utc = false
        var historyStore: String?
        var prices: String?
        var trafficInterval: Int?
        var noLive = false
        var unpriced = UnpricedOptions()
        var dumpsUnpriced = false
        var now: Int64?
        var remotes: [String] = []
        var remoteHistory = false
        var givenLocations: [TranscriptFolder: [DataLocation]] = [:]
        var iterator = arguments.makeIterator()
        while let argument = iterator.next() {
            switch argument {
            case "--dump-today":
                mode = .dumpToday
            case "--dump-history":
                mode = .dumpHistory
            case "--dump-quota":
                mode = .dumpQuota
            case "--dump-unpriced":
                dumpsUnpriced = true
            case "--now":
                guard let value = iterator.next(), let ms = Int64(value) else { return nil }
                now = ms
            case "--days":
                guard let value = iterator.next(), let days = Int(value), days >= 1, days <= 1_000_000 else { return nil }
                unpriced.days = days
            case "--first":
                guard let value = iterator.next(), isDate(value) else { return nil }
                unpriced.first = value
            case "--last":
                guard let value = iterator.next(), isDate(value) else { return nil }
                unpriced.last = value
            case "--live":
                unpriced.live = true
            case "--source":
                guard let value = iterator.next(), let source = SourceKind(rawValue: value) else { return nil }
                unpriced.source = source
            case "--utc":
                utc = true
            case "--history-store":
                guard let path = iterator.next() else { return nil }
                historyStore = path
            case "--prices":
                guard let path = iterator.next() else { return nil }
                prices = path
            case "--sync-prices":
                guard let directory = iterator.next() else { return nil }
                mode = .syncPrices(directory: directory)
            case "--sync-rates":
                guard let directory = iterator.next(), let currency = iterator.next() else { return nil }
                mode = .syncRates(directory: directory, currency: currency.uppercased())
            case "--watch":
                guard let value = iterator.next(), let seconds = Int(value), seconds > 0, seconds <= 86_400 else { return nil }
                mode = .watch(seconds: seconds)
            case "--traffic":
                guard let value = iterator.next(), let seconds = Int(value), seconds > 0, seconds <= 86_400 else { return nil }
                mode = .traffic(seconds: seconds)
            case "--traffic-interval":
                guard let value = iterator.next(), let seconds = Int(value), [0, 1, 2, 5].contains(seconds) else { return nil }
                trafficInterval = seconds
            case "--no-live":
                noLive = true
            case "--remote":
                guard let destination = iterator.next(), RemoteHostValidation.destinationIssue(destination) == nil,
                      remotes.count < RemoteHostsDocument.maxHosts else { return nil }
                remotes.append(destination)
            case "--history":
                remoteHistory = true
            case let flag where ProbeArguments.locationFlags[flag] != nil:
                // Repeatable: every occurrence adds one location to that source's list (METRICS 2.1), as many folders
                // as the settings would. `@auto` is the automatic entry; a list given only paths reads just those, as
                // the matching `reference.py` option.
                guard let folder = ProbeArguments.locationFlags[flag], let value = iterator.next(),
                      let location = ProbeArguments.location(value) else { return nil }
                var list = givenLocations[folder] ?? []
                guard !list.contains(location), list.count < SourceLocations.maxEntries else { return nil }
                list.append(location)
                givenLocations[folder] = list
            default:
                return nil
            }
        }
        for (folder, list) in givenLocations { configuration.locations[folder] = list }
        let usesUnpricedOptions = unpriced != UnpricedOptions()
        if dumpsUnpriced {
            guard mode.map({ _ in false }) ?? true, (unpriced.first == nil) == (unpriced.last == nil),
                  unpriced.first.map({ $0 <= unpriced.last! }) ?? true,
                  unpriced.days == nil || unpriced.first == nil,
                  !unpriced.live || (unpriced.days == nil && unpriced.first == nil) else { return nil }
            mode = .dumpUnpriced(unpriced)
        } else if usesUnpricedOptions {
            return nil
        }
        guard let mode else { return nil }
        if now != nil {
            guard case .dumpToday = mode else { return nil }
        }
        if !remotes.isEmpty {
            guard case .watch = mode else { return nil }
        }
        if remoteHistory, remotes.isEmpty { return nil }
        return ProbeArguments(mode: mode, configuration: configuration, utc: utc, historyStore: historyStore, prices: prices,
                              trafficInterval: trafficInterval, noLive: noLive, now: now, remotes: remotes,
                              remoteHistory: remoteHistory)
    }
}

extension ProbeArguments {
    /// The source flags and the location list each one feeds.
    static let locationFlags: [String: TranscriptFolder] = [
        "--codex": .codex, "--claude": .claude, "--dsh": .dsh, "--opencode": .opencode, "--gemini": .gemini,
        "--omp": .omp, "--antigravity": .antigravity, "--workbuddy": .workbuddy, "--workbuddy-ai": .workbuddyAi,
        "--zcode": .zcode, "--kimi": .kimi, "--grok-build": .grokBuild, "--pi": .pi, "--openclaw": .openclaw,
        "--hermes": .hermes, "--droid": .droid, "--devin": .devin, "--qoder": .qoder, "--qoder-cli": .qoderCli,
    ]

    static let automaticLocation = "@auto"

    /// `@auto`, or a path made absolute against the working directory.
    static func location(_ value: String) -> DataLocation? {
        if value == automaticLocation { return .automatic }
        guard !value.isEmpty else { return nil }
        return SourceLocations.standardized(URL(fileURLWithPath: value).path).map(DataLocation.path)
    }

    /// `YYYY-MM-DD` of a real calendar date.
    static func isDate(_ text: String) -> Bool {
        UnpricedRange.isDate(text)
    }

    /// The table of `--prices`, else the bundled one; exits when the file does not validate.
    func priceTable() -> PriceTable {
        guard let prices else { return PriceBook.bundled }
        guard let data = FileManager.default.contents(atPath: prices) else {
            FileHandle.standardError.write(Data("cannot read \(prices)\n".utf8))
            exit(2)
        }
        switch PriceTable.load(data) {
        case .success(let table):
            return table
        case .failure(let error):
            FileHandle.standardError.write(Data("\(prices) is not a valid price table: \(error)\n".utf8))
            exit(2)
        }
    }
}
