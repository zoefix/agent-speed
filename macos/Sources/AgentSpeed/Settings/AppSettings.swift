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
    /// The menu bar intelligence block (DESIGN 10): 智力 and 思考 after the network block; off by default.
    var barIntelligence: Bool { didSet { store(barIntelligence, .barIntelligence) } }
    /// The menu bar money block (DESIGN 3): the live money rate and today's total after a tok/s value.
    var barSecondary: Bool { didSet { store(barSecondary, .barSecondary) } }

    /// Dashboard agents list: every agent instead of the first rows (DESIGN section 4.4).
    var agentsExpanded: Bool { didSet { store(agentsExpanded, .agentsExpanded) } }

    var effects: EffectsLevel { didSet { store(effects.rawValue, .effects) } }
    var celebratePeaks: Bool { didSet { store(celebratePeaks, .celebratePeaks) } }
    var responsePulses: Bool { didSet { store(responsePulses, .responsePulses) } }

    var enabledSources: Set<SourceKind> {
        didSet {
            store(enabledSources.map(\.rawValue).sorted(), .enabledSources)
            store(SourceKind.allCases.map(\.rawValue), .knownSources)
        }
    }
    /// Nil means the default location from `LiveMonitor`.
    var codexRoot: URL? { didSet { store(codexRoot?.path, .codexRoot) } }
    var claudeRoot: URL? { didSet { store(claudeRoot?.path, .claudeRoot) } }
    var dshRoot: URL? { didSet { store(dshRoot?.path, .dshRoot) } }
    /// The folder that contains `opencode.db`.
    var opencodeRoot: URL? { didSet { store(opencodeRoot?.path, .opencodeRoot) } }
    /// One Gemini CLI `tmp` folder, replacing both defaults.
    var geminiRoot: URL? { didSet { store(geminiRoot?.path, .geminiRoot) } }
    /// One omp `sessions` folder, replacing the default root and every named profile.
    var ompRoot: URL? { didSet { store(ompRoot?.path, .ompRoot) } }
    /// One Antigravity store folder (or its `conversations` folder), replacing the three default stores.
    var antigravityRoot: URL? { didSet { store(antigravityRoot?.path, .antigravityRoot) } }
    /// WorkBuddy's data folder (or its `projects` folder), replacing `~/.workbuddy`.
    var workbuddyRoot: URL? { didSet { store(workbuddyRoot?.path, .workbuddyRoot) } }
    /// WorkBuddy AI's data folder (or its `projects` folder), replacing `~/.workbuddy-ai`.
    var workbuddyAiRoot: URL? { didSet { store(workbuddyAiRoot?.path, .workbuddyAiRoot) } }
    var zcodeRoot: URL? { didSet { store(zcodeRoot?.path, .zcodeRoot) } }
    var kimiRoot: URL? { didSet { store(kimiRoot?.path, .kimiRoot) } }
    var grokBuildRoot: URL? { didSet { store(grokBuildRoot?.path, .grokBuildRoot) } }
    /// One Pi folder, replacing `<agent dir>/sessions` and Pi's own custom session folder.
    var piRoot: URL? { didSet { store(piRoot?.path, .piRoot) } }
    /// One OpenClaw state folder, agent folder or `openclaw-agent.sqlite`, replacing every automatic state folder.
    var openclawRoot: URL? { didSet { store(openclawRoot?.path, .openclawRoot) } }
    /// One Hermes Agent home or `logs` folder, replacing the automatic home.
    var hermesRoot: URL? { didSet { store(hermesRoot?.path, .hermesRoot) } }
    /// Factory Droid's `.factory` folder or its `sessions` folder, replacing `~/.factory/sessions`.
    var droidRoot: URL? { didSet { store(droidRoot?.path, .droidRoot) } }
    var traeRoot: URL? { didSet { store(traeRoot?.path, .traeRoot) } }

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
        // Earlier builds kept 智力 as a fourth traffic choice: it becomes the switch once, and the network block off.
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
        if let raw = defaults.array(forKey: Key.enabledSources.rawValue) as? [String] {
            // A source added after the choice was saved starts enabled, like it would for a new user.
            let known = (defaults.array(forKey: Key.knownSources.rawValue) as? [String]).map(Set.init) ?? Self.legacySources
            enabledSources = Set(raw.compactMap(SourceKind.init(rawValue:)))
                .union(SourceKind.allCases.filter { !known.contains($0.rawValue) })
        } else {
            enabledSources = Set(SourceKind.allCases)
        }
        codexRoot = Self.url(defaults.string(forKey: Key.codexRoot.rawValue))
        claudeRoot = Self.url(defaults.string(forKey: Key.claudeRoot.rawValue))
        dshRoot = Self.url(defaults.string(forKey: Key.dshRoot.rawValue))
        opencodeRoot = Self.url(defaults.string(forKey: Key.opencodeRoot.rawValue))
        geminiRoot = Self.url(defaults.string(forKey: Key.geminiRoot.rawValue))
        ompRoot = Self.url(defaults.string(forKey: Key.ompRoot.rawValue))
        antigravityRoot = Self.url(defaults.string(forKey: Key.antigravityRoot.rawValue))
        workbuddyRoot = Self.url(defaults.string(forKey: Key.workbuddyRoot.rawValue))
        workbuddyAiRoot = Self.url(defaults.string(forKey: Key.workbuddyAiRoot.rawValue))
        zcodeRoot = Self.url(defaults.string(forKey: Key.zcodeRoot.rawValue))
        kimiRoot = Self.url(defaults.string(forKey: Key.kimiRoot.rawValue))
        grokBuildRoot = Self.url(defaults.string(forKey: Key.grokBuildRoot.rawValue))
        piRoot = Self.url(defaults.string(forKey: Key.piRoot.rawValue))
        openclawRoot = Self.location(defaults.string(forKey: Key.openclawRoot.rawValue))
        hermesRoot = Self.url(defaults.string(forKey: Key.hermesRoot.rawValue))
        droidRoot = Self.url(defaults.string(forKey: Key.droidRoot.rawValue))
        traeRoot = Self.url(defaults.string(forKey: Key.traeRoot.rawValue))
        autoCheckUpdates = defaults.object(forKey: Key.autoCheckUpdates.rawValue) as? Bool ?? true
        networkEnabled = defaults.object(forKey: Key.networkEnabled.rawValue) as? Bool ?? true
        syncPrices = defaults.object(forKey: Key.syncPrices.rawValue) as? Bool ?? true
        currency = CurrencyTable.bundled.currencyOrUSD(defaults.string(forKey: Key.currency.rawValue) ?? "").code
        liveUnit = LiveUnit(rawValue: defaults.string(forKey: Key.liveUnit.rawValue) ?? "") ?? .tokens
        subscriptionSources = Set((defaults.array(forKey: Key.subscriptionSources.rawValue) as? [String] ?? [])
            .compactMap(SourceKind.init(rawValue:)))
        includeSubscriptions = defaults.object(forKey: Key.includeSubscriptions.rawValue) as? Bool ?? false
        heroLastTitle = defaults.string(forKey: Key.heroLastTitle.rawValue)
        if !redlinesMigrated {
            // A stored former default now reads as the new default; persist it once so a later choice of 400 stays.
            if defaults.object(forKey: Key.redline.rawValue) != nil { store(redlineOutput, .redline) }
            if defaults.object(forKey: Key.redlineTotal.rawValue) != nil { store(redlineTotal, .redlineTotal) }
            store(true, .redlineDefaultsMigrated)
        }
    }

    var monitorConfiguration: MonitorConfiguration {
        MonitorConfiguration(
            enabledSources: enabledSources,
            codexRoot: codexRoot,
            claudeRoot: claudeRoot,
            dshRoot: dshRoot,
            opencodeRoot: opencodeRoot,
            geminiRoot: geminiRoot,
            ompRoot: ompRoot,
            antigravityRoot: antigravityRoot,
            workbuddyRoot: workbuddyRoot,
            workbuddyAiRoot: workbuddyAiRoot,
            zcodeRoot: zcodeRoot,
            kimiRoot: kimiRoot,
            grokBuildRoot: grokBuildRoot,
            piRoot: piRoot,
            openclawRoot: openclawRoot,
            hermesRoot: hermesRoot,
            droidRoot: droidRoot,
            traeRoot: traeRoot
        )
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

    private static func url(_ path: String?) -> URL? {
        guard let path, !path.isEmpty else { return nil }
        return URL(fileURLWithPath: path, isDirectory: true)
    }

    /// A folder or a file, as the file system says.
    private static func location(_ path: String?) -> URL? {
        guard let path, !path.isEmpty else { return nil }
        return URL(fileURLWithPath: path)
    }

    /// The stored `barTraffic` value of builds before 智力 had its own switch (DESIGN 10).
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
        case effects, celebratePeaks, responsePulses
        case enabledSources, knownSources
        case codexRoot, claudeRoot, dshRoot, opencodeRoot, geminiRoot, ompRoot, antigravityRoot, workbuddyRoot, workbuddyAiRoot
        case zcodeRoot, kimiRoot, grokBuildRoot, piRoot, openclawRoot, hermesRoot, droidRoot, traeRoot
        case autoCheckUpdates
        case networkEnabled, syncPrices, currency, liveUnit, subscriptionSources, includeSubscriptions
        case heroLastTitle
    }
}

extension Comparable {
    func clamped(to range: ClosedRange<Self>) -> Self {
        min(max(self, range.lowerBound), range.upperBound)
    }
}
