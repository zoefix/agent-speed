import AgentSpeedCore
import Foundation

/// The card's words (DESIGN 12, *Score card*, *Accessibility*).
@MainActor
enum PerfCardText {
    /// `phase.idle` while resting, `perf.card.top` on the hero's card for N ≥ 2, joined with ` · ` when both apply.
    static func eyebrow(_ score: PerfCardScore, _ l10n: Localizer) -> String? {
        var parts: [String] = []
        if score.resting, score.shown != nil { parts.append(l10n("phase.idle")) }
        if score.topCount >= 2, score.shown != nil { parts.append(l10n("perf.card.top", score.topCount)) }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// The summary under the scale; `perf.status.none` while there is no score.
    static func summary(_ score: PerfCardScore, _ l10n: Localizer) -> String {
        guard let shown = score.shown else { return l10n("perf.status.none", score.n, PerfFormula.minimumSamples) }
        let tier = l10n(PerfScoreText.tierKey(shown))
        let ratio = PerfScoreText.ratio(shown)
        let present = PerfComponentId.cardOrder.compactMap { score.component($0) }.filter(\.isPresent)
        guard present.count >= 2 else { return l10n("perf.summary", tier, ratio) }
        var slowest = present[0]
        for component in present.dropFirst() where (component.subScore ?? 0) < (slowest.subScore ?? 0) {
            slowest = component
        }
        if let low = slowest.subScore, low < 1000 {
            return l10n("perf.summary.slowest", tier, ratio, l10n(slowest.id.titleKey))
        }
        return l10n("perf.summary.allAbove", tier, ratio)
    }

    /// `perf.provisional.hint` with n and 10 while provisional.
    static func provisionalHint(_ score: PerfCardScore, _ l10n: Localizer) -> String? {
        guard score.provisional, score.shown != nil else { return nil }
        return l10n("perf.provisional.hint", score.n, PerfEvaluation.provisionalBelow)
    }

    /// The usual chip: the signed percent against the 14-day baseline, or `perf.usual.none`.
    static func usual(_ score: PerfCardScore, _ l10n: Localizer) -> (text: String, kind: PerfPalette.ChipKind) {
        guard let percent = score.vsUsualPercent else { return (l10n("perf.usual.none"), .dotted) }
        return (l10n("perf.usual", PerfScoreText.signedPercent(percent)), percent > 0 ? .above : .neutral)
    }

    /// The usual words for accessibility.
    static func usualWords(_ score: PerfCardScore, _ l10n: Localizer) -> String {
        guard let percent = score.vsUsualPercent else { return l10n("perf.usual.none") }
        if percent > 0 { return l10n("a11y.perf.usual.up", "\(percent)%") }
        if percent < 0 { return l10n("a11y.perf.usual.down", "\(-percent)%") }
        return l10n("a11y.perf.usual.same")
    }

    /// One component row of the card.
    struct Row: Equatable, Identifiable {
        var id: PerfComponentId
        var label: String
        /// The measure as shown, or the reason it is absent.
        var value: String
        var present: Bool
        /// The sub-score on the log scale (0…1); nil while absent.
        var meter: Double?
        var subScore: String
        var meaning: String
    }

    static func rows(_ score: PerfCardScore, _ group: [PerfComponentId], _ l10n: Localizer) -> [Row] {
        group.compactMap { id in
            guard let component = score.component(id) else { return nil }
            return row(component, source: score.source, tools: score.tools, l10n)
        }
    }

    static func row(_ component: PerfComponent, source: SourceKind, tools: Int, _ l10n: Localizer) -> Row {
        let label = l10n(component.id.titleKey)
        guard let measure = component.measure, let sub = component.subScore else {
            let notRecorded = component.reason == .notRecorded
            return Row(id: component.id, label: label,
                       value: l10n(notRecorded ? "perf.reason.notRecorded" : "perf.reason.fewSamples"), present: false,
                       meter: nil, subScore: "\u{2013}",
                       meaning: notRecorded ? l10n("perf.reason.notRecorded.hint", l10n(source.titleKey))
                           : l10n("perf.reason.fewSamples.hint", PerfFormula.minimumSamples))
        }
        var meaning = l10n(component.id.meaningKey)
        if component.id == .N { meaning += " · " + l10n("perf.comp.pace.count", tools) }
        return Row(id: component.id, label: label, value: PerfScoreText.value(component.id, measure, l10n), present: true,
                   meter: PerfFormat.scale(sub), subScore: PerfFormat.full(PerfScoreText.rounded(sub)), meaning: meaning)
    }

    /// 行為: the work signals of METRICS 6.10, unchanged.
    struct SignalLine: Equatable, Identifiable {
        enum Kind: Equatable {
            case looping
            case repeating
            case summary
        }

        var kind: Kind
        var text: String
        var id: String { text }
    }

    static func signals(_ signals: WorkSignals, _ l10n: Localizer) -> [SignalLine] {
        var lines: [SignalLine] = []
        if let count = signals.looping { lines.append(SignalLine(kind: .looping, text: l10n("work.looping", count))) }
        if let count = signals.repeating { lines.append(SignalLine(kind: .repeating, text: l10n("work.repeating", count))) }
        if let novelty = signals.novelty {
            lines.append(SignalLine(kind: .summary, text: l10n("work.summary", HistoryText.percent(novelty), signals.failed,
                                                                signals.recovered)))
        }
        return lines
    }

    /// `perf.card.sample` with n, then the row's facts.
    static func facts(_ model: PerfCardModel, _ l10n: Localizer) -> [String] {
        [l10n("perf.card.sample", model.score.n)] + model.facts
    }

    /// The card's content as the row's extra accessibility content, in DESIGN 12's order.
    static func accessibilityLines(_ model: PerfCardModel, _ l10n: Localizer) -> [String] {
        let score = model.score
        var lines: [String] = []
        if let shown = score.shown {
            let words = PerfScoreText.words(shown: shown, provisional: score.provisional, form: score.form,
                                            count: score.topCount, l10n)
            lines.append(l10n("a11y.perf.score", words, l10n(PerfScoreText.tierKey(shown))))
            lines.append(usualWords(score, l10n))
        } else {
            lines.append(l10n("intel.none"))
        }
        lines.append(summary(score, l10n))
        if let hint = provisionalHint(score, l10n) { lines.append(hint) }
        if score.shown != nil {
            for id in PerfComponentId.cardOrder {
                guard let component = score.component(id) else { continue }
                let row = row(component, source: score.source, tools: score.tools, l10n)
                lines.append(row.present ? l10n("a11y.perf.comp", row.label, row.value, row.subScore)
                    : l10n("a11y.perf.comp.absent", row.label, row.value))
                lines.append(row.meaning)
            }
        }
        lines += signals(score.signals, l10n).map(\.text)
        lines += facts(model, l10n)
        lines.append(l10n("perf.card.footnote"))
        return lines
    }
}
