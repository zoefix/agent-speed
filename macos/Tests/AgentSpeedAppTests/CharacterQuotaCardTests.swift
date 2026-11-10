@testable import AgentSpeed
import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Foundation
import Testing

/// The 額度 card of the `character` orb (DESIGN 2a.6, Readouts; user's choice, 2026-11-10): its lines, motion, forms,
/// small-orb rule, what accessibility reads and where its sources come from.
@Suite("Character readouts: the 額度 card")
@MainActor
struct CharacterQuotaCardTests {
    static let now: Int64 = 1_794_338_773_000
    static let hour: Int64 = 3_600_000

    static func snapshots() -> [QuotaSnapshot] {
        [
            QuotaSnapshot(source: .codex, observedMs: now, windows: [
                QuotaWindow(kind: .sevenDay, usedPercent: 25, resetsAtMs: now + 5 * 24 * hour),
            ]),
            QuotaSnapshot(source: .claudeCode, observedMs: now, windows: [
                QuotaWindow(kind: .fiveHour, usedPercent: 103.4, resetsAtMs: now - 1000),
                QuotaWindow(kind: .sevenDay, usedPercent: 86, resetsAtMs: nil),
            ]),
            QuotaSnapshot(source: .claudeDesktop, observedMs: now, windows: [
                QuotaWindow(kind: .modelWeek, name: "Fable", usedPercent: 27, resetsAtMs: now + 42 * 60_000 + 30_000),
                QuotaWindow(kind: .sevenDay, usedPercent: 94, resetsAtMs: now + 10 * hour + 60_000),
            ]),
        ]
    }

    static func sources(_ snapshots: [QuotaSnapshot] = snapshots(), _ language: LanguagePreference = .zhHant)
        -> [CharacterQuotaSource] {
        CharacterQuotaText.sources(snapshots.map { QuotaCardItem(snapshot: $0) }, nowMs: now, l10n: Localizer(preference: language))
    }

    @Test func linesFollowTheWindowsOfEachAccount() {
        let sources = Self.sources()
        #expect(sources.map(\.name) == ["Codex", "Claude Code", "Claude Desktop"])
        #expect(sources[0].lines.map(\.label) == ["7d"])
        let week = Self.now + 5 * 24 * Self.hour
        #expect(sources[0].lines[0].reset == Localizer(preference: .zhHant).quotaFormat.resetShort(week, nowMs: Self.now))
        #expect(["週一", "週日"].contains(sources[0].lines[0].reset ?? ""), "a weekday in any time zone")
        // A passed window shows 0 % with an empty bar and no reset; a window without a reset time has none.
        #expect(sources[1].lines.map(\.percentText) == ["0%", "86%"])
        #expect(sources[1].lines[0].passed && sources[1].lines[0].bar == 0 && sources[1].lines[0].reset == nil)
        #expect(sources[1].lines[1].reset == nil)
        // Section 2's order: the weekly window over every model first, then the model's.
        #expect(sources[2].lines.map(\.label) == ["7d", "Fable 7d"])
        #expect(sources[2].lines.map(\.reset) == ["10h", "42m"])
        #expect(sources[2].lines[0].bar == 0.94)
        #expect(["Mon", "Sun"].contains(Self.sources(Self.snapshots(), .en)[0].lines[0].reset ?? ""))
    }

    @Test func accessibilityReadsEverySourceAfterTheOtherCards() {
        let l10n = Localizer(preference: .en)
        let sentence = CharacterQuotaText.sentence(Self.snapshots().prefix(2).map { QuotaCardItem(snapshot: $0) }, nowMs: Self.now,
                                                   l10n: l10n)
        let reset = l10n.quotaFormat.resetLong(Self.now + 5 * 24 * Self.hour, nowMs: Self.now)
        #expect(sentence == "Quota: Codex 7d 25% used, \(reset), Claude Code 5h 0% used, 7d 86% used")
        #expect(CharacterQuotaText.sentence([], nowMs: Self.now, l10n: l10n) == nil)
        var readings = CharacterReadoutsTests.readings(models: [], latency: nil)
        readings.quota = Self.snapshots().map { QuotaCardItem(snapshot: $0) }
        let parts = OrbAccessibilityReporter.characterParts(readings, currency: "US dollars", l10n, now: 100,
                                                            quotaNowMs: Self.now)
        #expect(parts.last?.hasPrefix("Quota: Codex 7d 25% used") == true)
        readings.readouts.quota = false
        #expect(!OrbAccessibilityReporter.characterParts(readings, currency: "US dollars", l10n, now: 100,
                                                         quotaNowMs: Self.now).contains { $0.hasPrefix("Quota") })
    }

    // MARK: Motion

    @Test func barsGrowFromEmptyAndEaseToANewLength() throws {
        var dynamics = CharacterQuotaDynamics()
        dynamics.update(1 / 60, quota: Self.sources(), small: false, instant: false)
        let first = try #require(dynamics.sources.first?.lines.first)
        #expect(first.bar < 0.05 && dynamics.isMoving)
        #expect(dynamics.sources.allSatisfy { $0.presence == 1 }, "a card that appears brings its sources with it")
        for _ in 0..<30 { dynamics.update(1 / 60, quota: Self.sources(), small: false, instant: false) }
        #expect(abs((dynamics.sources[0].lines[0].bar) - 0.25) < 1e-9 && !dynamics.isMoving)
        var next = Self.snapshots()
        next[0] = QuotaSnapshot(source: .codex, observedMs: Self.now, windows: [
            QuotaWindow(kind: .sevenDay, usedPercent: 31, resetsAtMs: Self.now + 5 * 24 * Self.hour),
        ])
        dynamics.update(0.1, quota: Self.sources(next), small: false, instant: false)
        let easing = dynamics.sources[0].lines[0]
        #expect(easing.bar > 0.25 && easing.bar < 0.31)
        #expect(easing.percent == 31 && easing.oldPercent == 25 && easing.percentMix > 0.4 && easing.percentMix < 0.6)
        for _ in 0..<30 { dynamics.update(1 / 60, quota: Self.sources(next), small: false, instant: false) }
        #expect(abs(dynamics.sources[0].lines[0].bar - 0.31) < 1e-9 && dynamics.sources[0].lines[0].oldPercent == nil)
    }

    @Test func aResetEmptiesTheBarOverSixTenthsWhileItsCellFades() {
        var snapshots = Self.snapshots()
        var dynamics = CharacterQuotaDynamics()
        dynamics.update(1 / 60, quota: Self.sources(snapshots), small: false, instant: true)
        #expect(dynamics.sources[2].lines[0].reset == "10h")
        snapshots[2] = QuotaSnapshot(source: .claudeDesktop, observedMs: Self.now, windows: [
            QuotaWindow(kind: .modelWeek, name: "Fable", usedPercent: 27, resetsAtMs: Self.now + 42 * 60_000 + 30_000),
            QuotaWindow(kind: .sevenDay, usedPercent: 94, resetsAtMs: Self.now - 1),
        ])
        dynamics.update(0.3, quota: Self.sources(snapshots), small: false, instant: false)
        let line = dynamics.sources[2].lines[0]
        #expect(line.duration == CharacterQuotaDynamics.resetEmpty)
        #expect(line.bar > 0.05 && line.bar < 0.94)
        #expect(line.reset == "10h" && line.resetPresence > 0.4 && line.resetPresence < 0.6, "the cell fades with its text")
        for _ in 0..<30 { dynamics.update(1 / 60, quota: Self.sources(snapshots), small: false, instant: false) }
        #expect(dynamics.sources[2].lines[0].bar == 0 && dynamics.sources[2].lines[0].reset == nil)
    }

    @Test func reduceMotionChangesLengthsAtOnceButStillFades() {
        var dynamics = CharacterQuotaDynamics()
        dynamics.update(1 / 60, quota: Array(Self.sources().prefix(1)), small: false, instant: true)
        #expect(dynamics.sources[0].lines[0].bar == 0.25)
        dynamics.update(1 / 60, quota: Self.sources(), small: false, instant: true)
        #expect(dynamics.sources[1].presence < 0.1 && dynamics.isMoving, "a joining source fades in")
        for _ in 0..<30 { dynamics.update(1 / 60, quota: Self.sources(), small: false, instant: true) }
        dynamics.update(0.1, quota: Array(Self.sources().prefix(1)), small: false, instant: true)
        let leaving = dynamics.sources.first { $0.source == .claudeCode }
        #expect(leaving?.wanted == false && leaving?.lines.count == 2, "a leaving source keeps its lines while it fades")
        for _ in 0..<30 { dynamics.update(1 / 60, quota: Array(Self.sources().prefix(1)), small: false, instant: true) }
        #expect(dynamics.sources.map(\.source) == [.codex])
    }

    @Test func smallOrbsListTheFirstSourceAlone() {
        var dynamics = CharacterQuotaDynamics()
        dynamics.update(1 / 60, quota: Self.sources(), small: true, instant: true)
        #expect(dynamics.sources.map(\.source) == [.codex])
    }

    // MARK: Forms

    static func cards(_ sources: [CharacterQuotaSource], disc: CGFloat = 296, form: Int? = nil) -> CharacterCards {
        var dynamics = CharacterCardDynamics()
        var content = CharacterReadoutsTests.content([], latency: nil)
        content.quota = sources
        dynamics.update(1 / 60, character: content, small: disc < 192, instant: true)
        let frame = Self.frame(content, disc: disc)
        let probe = CharacterCards(frame: frame, character: content, backing: 2, dynamics: dynamics)
        // A form asked for is the only one that fits.
        let widths = form.map { wanted in (0..<4).map { $0 == wanted ? 1.0 : .infinity } } ?? probe.quotaWidths()
        dynamics.fitQuota(1 / 60, widths: widths, room: form == nil ? 1000 : 1, spare: 0, snap: true)
        return CharacterCards(frame: frame, character: content, backing: 2, dynamics: dynamics)
    }

    static func frame(_ content: OrbCharacterContent, disc: CGFloat) -> OrbFrame {
        var frame = CharacterCardsTests.frame(CharacterCardsTests.readings())
        var c = frame.content
        c.character = content
        frame = OrbFrame(time: 100, dt: 0, geometry: OrbGeometry(diameter: disc, canvasSide: 1.5 * disc), content: c,
                         effects: frame.effects, heat: 0, overdrive: 0, rawHeat: 0, rawOverdrive: 0, displayHeat: 0,
                         tier: .idle, redline: 400, tierChangedAt: -.infinity, titleChangedAt: -.infinity, flash: 0,
                         breath: 1, breathPhase: 0, redlineAmount: 0, activity: 0, signOfLife: nil, peakAge: .infinity,
                         alpha: 1)
        return frame
    }

    @Test func noSourceNoCard() {
        #expect(Self.cards([]).size(.quota, model: nil) == (0, 0))
        #expect(Self.cards([]).quotaWidths().isEmpty)
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func theResetCellsGoFirstThenTheBarsShortenThenTheHeaders() throws {
        let cards = Self.cards(Self.sources())
        let widths = cards.quotaWidths()
        #expect(widths.count == 4)
        #expect(widths[0] > widths[1] && widths[1] > widths[2], "full, without resets, short bars")
        #expect(abs((widths[1] - widths[2]) - 2 * Double(cards.m.t)) < 0.01, "bars from 5 t to 3 t")
        let full = cards.size(.quota, model: nil)
        #expect(abs(full.w - widths[0]) < 0.01)
        // The compact form drops the headers, the dot before each source's first label.
        let compact = Self.cards(Self.sources(), form: 3)
        #expect(compact.quotaForm == .compact)
        #expect(abs(compact.size(.quota, model: nil).h - (full.h - 3 * Double(cards.m.rowHeight(0.9 * cards.m.t)
            + 0.25 * cards.m.t))) < 0.01)
        #expect(abs(widths[3] - (widths[2] + Double(0.9 * cards.m.t))) < 0.01, "the lines are the widest here")
        let named = Self.sources([QuotaSnapshot(source: .claudeDesktop, observedMs: Self.now, windows: [
            QuotaWindow(kind: .sevenDay, usedPercent: 5, resetsAtMs: nil),
        ])])
        #expect(Self.cards(named).quotaWidths()[0] == .infinity, "no reset time: no reset cells to drop")
        // Below S = 96: the first source, no reset cells.
        let small = Self.cards(Self.sources(), disc: 184)
        #expect(small.quotaWidths()[0] == .infinity)
        #expect(small.size(.quota, model: nil).h < full.h)
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func everyLineFitsItsPlate() {
        let cards = Self.cards(Self.sources())
        let size = cards.size(.quota, model: nil)
        let m = cards.m
        // Three headers and five lines, with the gaps between them.
        let content = 3 * m.rowHeight(0.9 * m.t) + 5 * m.rowHeight(m.t) + 5 * 0.25 * m.t + 2 * 0.45 * m.t
        #expect(abs(size.h - Double(content + 2 * m.padY)) < 0.01)
    }

    // MARK: Sources

    private static func model() -> AppModel {
        _ = NSApplication.shared
        let suite = "CharacterQuotaCardTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.removePersistentDomain(forName: suite)
        return AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
    }

    @Test func theOrbTakesItsSourcesThatRunAndAreSignedIn() throws {
        let model = Self.model()
        model.settings.quotaClaudeCli = true
        let snapshots = Self.snapshots()
        model.quota.preview(desktop: snapshots[2], cli: snapshots[1],
                            apps: QuotaApps(codex: true, claudeCode: true, claudeDesktop: true),
                            signedIn: [.claudeCode], nowMs: Self.now)
        var config = OrbConfig(id: "a", style: .character)
        let runtime = OrbRuntime(config: config)
        runtime.ingestQuota(model.quota, connected: [])
        // Codex has no snapshot in the engine's; Claude Code and Desktop run and are signed in.
        #expect(runtime.feed.characterReadings?.quota.map(\.source) == [.claudeCode, .claudeDesktop])
        config.sources = .explicit([.claudeDesktop])
        runtime.update(config: config)
        runtime.ingestQuota(model.quota, connected: [])
        #expect(runtime.feed.characterReadings?.quota.map(\.source) == [.claudeDesktop])
        // An orb of remote hosts alone shows only their sources, and only while connected.
        config.hosts = .explicit([UUID().uuidString])
        runtime.update(config: config)
        runtime.ingestQuota(model.quota, connected: [])
        #expect(runtime.feed.characterReadings?.quota.isEmpty == true)
    }
}
