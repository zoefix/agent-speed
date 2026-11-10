import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Observation

/// Every user setting (DESIGN section 5). Persisted to UserDefaults on change and observable for live updates.
@MainActor
@Observable
final class AppSettings {
    var appearance: AppearanceMode { didSet { store(appearance.rawValue, .appearance) } }
    var language: LanguagePreference { didSet { store(language.rawValue, .language) } }
    var metric: SpeedMetric { didSet { store(metric.rawValue, .metric) } }
    /// Redline of the output-speed metric.
    var redlineOutput: Double {
        didSet {
            let snapped = SettingsLimits.snapRedline(redlineOutput, metric: .output)
            if snapped != redlineOutput { redlineOutput = snapped; return }
            store(redlineOutput, .redline)
        }
    }
    /// Redline of the total-throughput metric (includes cached input, typically ~1000× larger).
    var redlineTotal: Double {
        didSet {
            let snapped = SettingsLimits.snapRedline(redlineTotal, metric: .total)
            if snapped != redlineTotal { redlineTotal = snapped; return }
            store(redlineTotal, .redlineTotal)
        }
    }

    var barMode: BarDisplayMode { didSet { store(barMode.rawValue, .barMode) } }
    var barColoredGlyph: Bool { didSet { store(barColoredGlyph, .barColoredGlyph) } }
    var barShowsUnit: Bool { didSet { store(barShowsUnit, .barShowsUnit) } }

    /// The floating orbs' master switch (DESIGN 2b). The single-orb keys of earlier versions (`orbVisible`, `orbSize`,
    /// …) are only read once to migrate and never written again, so a downgrade restores the single orb.
    var orbsVisible: Bool { didSet { store(orbsVisible, .orbsVisible) } }
    /// Name plates under the orbs (DESIGN 2b, Name plate): auto, always or never; each orb may override it.
    var orbNames: OrbNamesMode { didSet { store(orbNames.rawValue, .orbNames) } }

    /// Network traffic measuring (DESIGN 10, METRICS 10.4): off stops the sampler at once.
    var trafficEnabled: Bool { didSet { store(trafficEnabled, .trafficEnabled) } }
    /// Displayed source and agent traffic includes the tools the agents run (METRICS 10.5); stored data is unchanged.
    var trafficIncludeTools: Bool { didSet { store(trafficIncludeTools, .trafficIncludeTools) } }
    /// Rates in bytes or bits per second (DESIGN 10).
    var trafficUnit: TrafficUnit { didSet { store(trafficUnit.rawValue, .trafficUnit) } }
    /// The menu bar traffic block (DESIGN 10): off (default), this computer or the agents.
    var barTraffic: BarTraffic { didSet { store(barTraffic.rawValue, .barTraffic) } }
    /// The menu bar intelligence block (DESIGN 10): 性能 and 思考 after the network block; off by default.
    var barIntelligence: Bool { didSet { store(barIntelligence, .barIntelligence) } }
    /// The menu bar money block (DESIGN 3): the live money rate and today's total after a tok/s value.
    var barSecondary: Bool { didSet { store(barSecondary, .barSecondary) } }

    /// Dashboard agents list: every agent instead of the first rows (DESIGN section 4.4).
    var agentsExpanded: Bool { didSet { store(agentsExpanded, .agentsExpanded) } }

    var effects: EffectsLevel { didSet { store(effects.rawValue, .effects) } }
    var celebratePeaks: Bool { didSet { store(celebratePeaks, .celebratePeaks) } }
    var responsePulses: Bool { didSet { store(responsePulses, .responsePulses) } }
    /// The orbs and boards present in step with the display's refresh (DESIGN 5, Effects); off kept the built-in
    /// display's brightness from changing on an M1 (issue #48).
    var displaySync: Bool { didSet { store(displaySync, .displaySync) } }

    var enabledSources: Set<SourceKind> {
        didSet {
            store(enabledSources.map(\.rawValue).sorted(), .enabledSources)
            store(SourceKind.allCases.map(\.rawValue), .knownSources)
        }
    }
    /// Every source's location list (DESIGN 5, METRICS 2.1), stored as one JSON value under `sourceLocations`. The
    /// single-folder keys of earlier versions (`codexRoot`, …) are only read once to migrate and never written again,
    /// so a downgrade restores the single folder.
    var sourceLocations: SourceLocations {
        didSet { if sourceLocations != oldValue { store(sourceLocations.data(), .sourceLocations) } }
    }

    /// UPDATES section 3: check GitHub at launch and every 6 h.
    var autoCheckUpdates: Bool { didSet { store(autoCheckUpdates, .autoCheckUpdates) } }

    /// NETWORK 1: off, nothing is ever requested (updates, prices, rates).
    var networkEnabled: Bool { didSet { store(networkEnabled, .networkEnabled) } }
    /// NETWORK 3: sync the price table daily.
    var syncPrices: Bool { didSet { store(syncPrices, .syncPrices) } }
    /// The display currency (DESIGN 9): an ISO 4217 code of `currencies.json`.
    var currency: String { didSet { store(currency, .currency) } }
    /// The unit of the menu bar widget and the dashboard hero (DESIGN 9); orbs choose their own.
    var liveUnit: LiveUnit { didSet { store(liveUnit.rawValue, .liveUnit) } }
    /// Sources used through a subscription (METRICS 9.4): left out of displayed costs unless included.
    var subscriptionSources: Set<SourceKind> {
        didSet { store(subscriptionSources.map(\.rawValue).sorted(), .subscriptionSources) }
    }
    var includeSubscriptions: Bool { didSet { store(includeSubscriptions, .includeSubscriptions) } }

    /// The remote hosts (REMOTE 2.1), stored as one JSON value under `remoteHosts`.
    var remoteHosts: RemoteHostsDocument {
        didSet { if remoteHosts != oldValue { store(remoteHosts.data(), .remoteHosts) } }
    }

    /// The local network feed (LAN 2.1), stored as one JSON value under `lan`.
    var lan: LanDocument {
        didSet { if lan != oldValue { store(lan.data(), .lan) } }
    }

    /// The subscription quotas (QUOTAS; DESIGN 5 *額度*): Codex and Claude Desktop on, the Claude CLI's status line
    /// bridge off until the user confirms it.
    var quotaCodex: Bool { didSet { store(quotaCodex, .quotaCodex) } }
    var quotaClaudeDesktop: Bool { didSet { store(quotaClaudeDesktop, .quotaClaudeDesktop) } }
    var quotaClaudeCli: Bool { didSet { store(quotaClaudeCli, .quotaClaudeCli) } }
    /// The dashboard's Claude Code invitation was dismissed for good (`quotaCliInvite` = `"dismissed"`).
    var quotaCliInviteDismissed: Bool {
        didSet { store(quotaCliInviteDismissed ? Self.inviteDismissed : nil, .quotaCliInvite) }
    }

    /// Phase 2 (QUOTAS 9; DESIGN 5 *額度, phase 2*): local routes and the agents' own sign-ins on, Grok's live request
    /// off, ZCode off until a key is saved, the browser sessions off until the user agrees. The ZCode key itself is
    /// only in the keychain.
    var quotaGrokBuild: Bool { didSet { store(quotaGrokBuild, .quotaGrokBuild) } }
    var quotaGrokBuildLive: Bool { didSet { store(quotaGrokBuildLive, .quotaGrokBuildLive) } }
    var quotaDevin: Bool { didSet { store(quotaDevin, .quotaDevin) } }
    var quotaAntigravity: Bool { didSet { store(quotaAntigravity, .quotaAntigravity) } }
    var quotaKimi: Bool { didSet { store(quotaKimi, .quotaKimi) } }
    var quotaGemini: Bool { didSet { store(quotaGemini, .quotaGemini) } }
    var quotaDroid: Bool { didSet { store(quotaDroid, .quotaDroid) } }
    var quotaZcode: Bool { didSet { store(quotaZcode, .quotaZcode) } }
    var quotaZcodeRegion: QuotaRequests.ZaiRegion { didSet { store(quotaZcodeRegion.rawValue, .quotaZcodeRegion) } }
    var quotaQoder: Bool { didSet { store(quotaQoder, .quotaQoder) } }
    var quotaWorkBuddy: Bool { didSet { store(quotaWorkBuddy, .quotaWorkBuddy) } }

    /// The vehicle of the last speed title other than idle (DESIGN 4c, Idle), parked in the dashboard hero while idle,
    /// also across relaunches. Nil before any title was reached.
    var heroLastTitle: String? {
        didSet { if heroLastTitle != oldValue { store(heroLastTitle, .heroLastTitle) } }
    }

    @ObservationIgnored private let defaults: UserDefaults

    /// The store these settings live in (the floating orbs keep their document next to them).
    var storage: UserDefaults { defaults }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        appearance = AppearanceMode(rawValue: defaults.string(forKey: Key.appearance.rawValue) ?? "") ?? .auto
        language = LanguagePreference(rawValue: defaults.string(forKey: Key.language.rawValue) ?? "") ?? .system
        metric = SpeedMetric(rawValue: defaults.string(forKey: Key.metric.rawValue) ?? "") ?? .output
        let redlinesMigrated = defaults.bool(forKey: Key.redlineDefaultsMigrated.rawValue)
        redlineOutput = SettingsLimits.snapRedline(
            SettingsLimits.startingRedline(stored: defaults.object(forKey: Key.redline.rawValue) as? Double,
                                           metric: .output, migrated: redlinesMigrated),
            metric: .output
        )
        redlineTotal = SettingsLimits.snapRedline(
            SettingsLimits.startingRedline(stored: defaults.object(forKey: Key.redlineTotal.rawValue) as? Double,
                                           metric: .total, migrated: redlinesMigrated),
            metric: .total
        )
        barMode = BarDisplayMode(rawValue: defaults.string(forKey: Key.barMode.rawValue) ?? "") ?? .glyphValue
        barColoredGlyph = defaults.object(forKey: Key.barColoredGlyph.rawValue) as? Bool ?? true
        barShowsUnit = defaults.object(forKey: Key.barShowsUnit.rawValue) as? Bool ?? true
        orbsVisible = defaults.object(forKey: Key.orbsVisible.rawValue) as? Bool
            ?? defaults.object(forKey: Key.orbVisible.rawValue) as? Bool ?? true
        orbNames = OrbNamesMode(rawValue: defaults.string(forKey: Key.orbNames.rawValue) ?? "") ?? .auto
        trafficEnabled = defaults.object(forKey: Key.trafficEnabled.rawValue) as? Bool ?? true
        trafficIncludeTools = defaults.object(forKey: Key.trafficIncludeTools.rawValue) as? Bool ?? true
        trafficUnit = TrafficUnit(rawValue: defaults.string(forKey: Key.trafficUnit.rawValue) ?? "") ?? .bytes
        // Earlier builds kept 性能 as a fourth traffic choice: it becomes the switch once, and the network block off.
        let storedTraffic = defaults.string(forKey: Key.barTraffic.rawValue)
        let migrates = storedTraffic == Self.retiredIntelligenceTraffic
        barTraffic = BarTraffic(rawValue: storedTraffic ?? "") ?? .off
        barIntelligence = migrates || defaults.object(forKey: Key.barIntelligence.rawValue) as? Bool ?? false
        if migrates {
            defaults.set(BarTraffic.off.rawValue, forKey: Key.barTraffic.rawValue)
            defaults.set(true, forKey: Key.barIntelligence.rawValue)
        }
        barSecondary = defaults.object(forKey: Key.barSecondary.rawValue) as? Bool ?? false
        agentsExpanded = defaults.object(forKey: Key.agentsExpanded.rawValue) as? Bool ?? false
        effects = EffectsLevel(rawValue: defaults.string(forKey: Key.effects.rawValue) ?? "") ?? .standard
        celebratePeaks = defaults.object(forKey: Key.celebratePeaks.rawValue) as? Bool ?? true
        responsePulses = defaults.object(forKey: Key.responsePulses.rawValue) as? Bool ?? true
        displaySync = defaults.object(forKey: Key.displaySync.rawValue) as? Bool ?? true
        if let raw = defaults.array(forKey: Key.enabledSources.rawValue) as? [String] {
            // A source added after the choice was saved starts enabled, like it would for a new user.
            let known = (defaults.array(forKey: Key.knownSources.rawValue) as? [String]).map(Set.init) ?? Self.legacySources
            enabledSources = Set(raw.compactMap(SourceKind.init(rawValue:)))
                .union(SourceKind.allCases.filter { !known.contains($0.rawValue) })
        } else {
            enabledSources = Set(SourceKind.allCases)
        }
        sourceLocations = defaults.data(forKey: Key.sourceLocations.rawValue).flatMap(SourceLocations.init(data:))
            ?? Self.legacyLocations(defaults)
        autoCheckUpdates = defaults.object(forKey: Key.autoCheckUpdates.rawValue) as? Bool ?? true
        networkEnabled = defaults.object(forKey: Key.networkEnabled.rawValue) as? Bool ?? true
        syncPrices = defaults.object(forKey: Key.syncPrices.rawValue) as? Bool ?? true
        currency = CurrencyTable.bundled.currencyOrUSD(defaults.string(forKey: Key.currency.rawValue) ?? "").code
        liveUnit = LiveUnit(rawValue: defaults.string(forKey: Key.liveUnit.rawValue) ?? "") ?? .tokens
        subscriptionSources = Set((defaults.array(forKey: Key.subscriptionSources.rawValue) as? [String] ?? [])
            .compactMap(SourceKind.init(rawValue:)))
        includeSubscriptions = defaults.object(forKey: Key.includeSubscriptions.rawValue) as? Bool ?? false
        heroLastTitle = defaults.string(forKey: Key.heroLastTitle.rawValue)
        quotaCodex = defaults.object(forKey: Key.quotaCodex.rawValue) as? Bool ?? true
        quotaClaudeDesktop = defaults.object(forKey: Key.quotaClaudeDesktop.rawValue) as? Bool ?? true
        quotaClaudeCli = defaults.object(forKey: Key.quotaClaudeCli.rawValue) as? Bool ?? false
        quotaCliInviteDismissed = defaults.string(forKey: Key.quotaCliInvite.rawValue) == Self.inviteDismissed
        func flag(_ key: Key, _ fallback: Bool) -> Bool { defaults.object(forKey: key.rawValue) as? Bool ?? fallback }
        quotaGrokBuild = flag(.quotaGrokBuild, true)
        quotaGrokBuildLive = flag(.quotaGrokBuildLive, false)
        quotaDevin = flag(.quotaDevin, true)
        quotaAntigravity = flag(.quotaAntigravity, true)
        quotaKimi = flag(.quotaKimi, true)
        quotaGemini = flag(.quotaGemini, true)
        quotaDroid = flag(.quotaDroid, true)
        quotaZcode = flag(.quotaZcode, false)
        quotaZcodeRegion = QuotaRequests.ZaiRegion(rawValue: defaults.string(forKey: Key.quotaZcodeRegion.rawValue) ?? "") ?? .global
        quotaQoder = flag(.quotaQoder, false)
        quotaWorkBuddy = flag(.quotaWorkBuddy, false)
        remoteHosts = defaults.data(forKey: Key.remoteHosts.rawValue).flatMap(RemoteHostsDocument.init(data:)) ?? .empty
        lan = defaults.data(forKey: Key.lan.rawValue).map(LanDocument.init(data:)) ?? .empty
        if !redlinesMigrated {
            // A stored former default now reads as the new default; persist it once so a later choice of 400 stays.
            if defaults.object(forKey: Key.redline.rawValue) != nil { store(redlineOutput, .redline) }
            if defaults.object(forKey: Key.redlineTotal.rawValue) != nil { store(redlineTotal, .redlineTotal) }
            store(true, .redlineDefaultsMigrated)
        }
    }

    var monitorConfiguration: MonitorConfiguration {
        MonitorConfiguration(enabledSources: enabledSources, locations: sourceLocations)
    }

    func isEnabled(_ source: SourceKind) -> Bool { enabledSources.contains(source) }

    /// The sources whose costs are shown (METRICS 9.4): every source unless marked as a subscription and not included.
    var billableSources: SourceSet {
        includeSubscriptions ? .everything : SourceSet.everything.subtracting(SourceSet(subscriptionSources))
    }

    func isSubscription(_ source: SourceKind) -> Bool { subscriptionSources.contains(source) }

    func setSubscription(_ source: SourceKind, _ marked: Bool) {
        if marked { subscriptionSources.insert(source) } else { subscriptionSources.remove(source) }
    }

    func setEnabled(_ source: SourceKind, _ enabled: Bool) {
        if enabled { enabledSources.insert(source) } else { enabledSources.remove(source) }
    }

    /// Redline of the selected metric; setting it edits that metric's value (DESIGN section 1).
    var redline: Double {
        get { redline(for: metric) }
        set {
            switch metric {
            case .output: redlineOutput = newValue
            case .total: redlineTotal = newValue
            }
        }
    }

    func redline(for metric: SpeedMetric) -> Double {
        metric == .output ? redlineOutput : redlineTotal
    }

    /// Slider range of the selected metric's redline.
    var redlineRange: ClosedRange<Double> { SettingsLimits.redlineRange(for: metric) }

    /// Thresholds (tok/s) where boost, overdrive and redline begin for the current redline.
    var tierThresholds: (boost: Double, overdrive: Double, redline: Double) {
        (redline * SpeedTier.boost.threshold, redline * SpeedTier.overdrive.threshold, redline * SpeedTier.redline.threshold)
    }

    /// The sources that existed before `knownSources` was saved alongside `enabledSources`.
    private static let legacySources: Set<String> = ["codex", "claudeCode", "claudeDesktop"]

    /// The single folder each source could be given before location lists (issue #45): that folder alone.
    private static func legacyLocations(_ defaults: UserDefaults) -> SourceLocations {
        var paths: [TranscriptFolder: String] = [:]
        for folder in TranscriptFolder.allCases {
            if let path = defaults.string(forKey: legacyKey(folder)), !path.isEmpty { paths[folder] = path }
        }
        return SourceLocations(legacy: paths)
    }

    /// `codexRoot`, `claudeRoot`, …: read to migrate, never written.
    static func legacyKey(_ folder: TranscriptFolder) -> String { folder.rawValue + "Root" }

    static let inviteDismissed = "dismissed"

    /// Whether a source's quota is read and shown (QUOTAS 9.1): the source is enabled and its quota switch is on; only
    /// the quota sources have one (DESIGN 5 *額度*). Qoder's one switch covers both Qoder sources, each row its own.
    func showsQuota(_ source: SourceKind) -> Bool {
        isEnabled(source) && quotaSwitch(source)
    }

    /// The quota switch alone, whatever the source's toggle.
    func quotaSwitch(_ source: SourceKind) -> Bool {
        switch source {
        case .codex: quotaCodex
        case .claudeDesktop: quotaClaudeDesktop
        case .claudeCode: quotaClaudeCli
        case .grokBuild: quotaGrokBuild
        case .devin: quotaDevin
        case .antigravity: quotaAntigravity
        case .kimi: quotaKimi
        case .gemini: quotaGemini
        case .droid: quotaDroid
        case .zcode: quotaZcode
        case .qoder, .qoderCli: quotaQoder
        case .workbuddy: quotaWorkBuddy
        default: false
        }
    }

    /// The phase 2 readers that may run (QUOTAS 9.1): the source enabled and its switch on; Grok's live request only
    /// with its log's switch; Qoder's shared account while either Qoder source is enabled.
    var quotaRoutes: Set<QuotaRoute> {
        Set(QuotaRoute.allCases.filter(routeIsOn))
    }

    func routeIsOn(_ route: QuotaRoute) -> Bool {
        switch route {
        case .grokBuildLive: showsQuota(.grokBuild) && quotaGrokBuildLive
        case .qoder: quotaQoder && (isEnabled(.qoder) || isEnabled(.qoderCli))
        default: showsQuota(route.source)
        }
    }

    /// The switches every remote host reads with (REMOTE 11.1): enabled sources only; Claude Code is each host's own
    /// switch, and only while Claude Code is enabled.
    var remoteQuotaSwitches: RemoteQuotaSwitches {
        let sources = RemoteQuotaConfig.hostSources.filter { $0 != .claudeCode && showsQuota($0) }
        return RemoteQuotaSwitches(sources: Set(sources), grokLive: routeIsOn(.grokBuildLive),
                                   claudeCode: isEnabled(.claudeCode))
    }

    /// The stored `barTraffic` value of builds before 性能 had its own switch (DESIGN 10).
    static let retiredIntelligenceTraffic = "intelligence"

    private func store(_ value: Any?, _ key: Key) {
        if let value { defaults.set(value, forKey: key.rawValue) } else { defaults.removeObject(forKey: key.rawValue) }
    }

    private enum Key: String {
        case appearance, language, metric, redline, redlineTotal, redlineDefaultsMigrated
        case barMode, barColoredGlyph, barShowsUnit
        case orbVisible, orbsVisible, orbNames
        case trafficEnabled, trafficIncludeTools, trafficUnit, barTraffic, barIntelligence, barSecondary
        case agentsExpanded
        case effects, celebratePeaks, responsePulses, displaySync
        case enabledSources, knownSources
        case sourceLocations
        case autoCheckUpdates
        case networkEnabled, syncPrices, currency, liveUnit, subscriptionSources, includeSubscriptions
        case heroLastTitle
        case remoteHosts
        case lan
        case quotaCodex, quotaClaudeDesktop, quotaClaudeCli, quotaCliInvite
        case quotaGrokBuild, quotaGrokBuildLive, quotaDevin, quotaAntigravity, quotaKimi, quotaGemini, quotaDroid
        case quotaZcode, quotaZcodeRegion, quotaQoder, quotaWorkBuddy
    }
}

extension Comparable {
    func clamped(to range: ClosedRange<Self>) -> Self {
        min(max(self, range.lowerBound), range.upperBound)
    }
}
