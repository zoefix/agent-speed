import AgentSpeedCore
import Foundation

/// One source of the 額度 card as drawn (DESIGN 2a.6): its name and color, then one line per window its account has.
struct CharacterQuotaSource: Equatable {
    var source: SourceKind
    var name: String
    var tint: RGBColor
    var lines: [CharacterQuotaLine]
}

/// One window line: the label (`5h`, `7d`, `Fable 7d`), the shown percent, the bar's fill (0…1) and the short reset
/// (`42m`, `3h`, `週五`), nil while unknown or passed.
struct CharacterQuotaLine: Equatable {
    var label: String
    var percent: Int
    var bar: Double
    var reset: String?
    var passed: Bool

    var percentText: String { "\(percent)%" }
}

/// The 額度 card's words (DESIGN 2a.6, QUOTAS 7), as drawn and as read by accessibility.
@MainActor
enum CharacterQuotaText {
    /// The card's sources from the orb's quota snapshots at `nowMs`.
    static func sources(_ snapshots: [QuotaSnapshot], nowMs: Int64, l10n: Localizer) -> [CharacterQuotaSource] {
        let format = l10n.quotaFormat
        return snapshots.map { snapshot in
            CharacterQuotaSource(
                source: snapshot.source, name: l10n(snapshot.source.titleKey), tint: snapshot.source.tint,
                lines: snapshot.shown(nowMs: nowMs, words: format.strings.words).map { window in
                    CharacterQuotaLine(label: window.label, percent: window.percent, bar: window.bar / 100,
                                       reset: window.resetsAtMs.map { format.resetShort($0, nowMs: nowMs) },
                                       passed: window.passed)
                })
        }
    }

    /// `a11y.orb.quota`: each source's name followed by its windows' `a11y.quota.window` (joined by `list.separator`),
    /// the sources joined by `list.separator`.
    static func sentence(_ snapshots: [QuotaSnapshot], nowMs: Int64, l10n: Localizer) -> String? {
        guard !snapshots.isEmpty else { return nil }
        let separator = l10n("list.separator")
        let words = l10n.quotaFormat.strings.words
        let parts = snapshots.map { snapshot in
            l10n(snapshot.source.titleKey) + " " + snapshot.shown(nowMs: nowMs, words: words).map {
                l10n.quotaWindowSpoken($0, nowMs: nowMs)
            }.joined(separator: separator)
        }
        return l10n("a11y.orb.quota", parts.joined(separator: separator))
    }
}
