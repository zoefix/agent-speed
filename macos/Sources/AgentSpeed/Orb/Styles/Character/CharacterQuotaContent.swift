import AgentSpeedCore
import Foundation

/// One source of the 額度 card as drawn (DESIGN 2a.6): its name and color, then one line per window its account has.
struct CharacterQuotaSource: Equatable {
    var source: SourceKind
    /// The source, and its host for a remote one: what motion follows from frame to frame.
    var key: String
    var name: String
    /// A remote host's name: the header reads `<source> · <host>` (DESIGN 13 *Host quotas*).
    var host: String? = nil
    var tint: RGBColor
    var lines: [CharacterQuotaLine]
}

/// One window line: the label (`5h`, `7d`, `Fable 7d`), the shown percent (`≈` while extrapolated, QUOTAS 10.7), the
/// bar's fill (0…1), the used amount (QUOTAS 10.8; nil without spend) and the short reset (`42m`, `3h`, `週五`), nil
/// while unknown or passed.
struct CharacterQuotaLine: Equatable {
    var label: String
    var percent: Int
    var bar: Double
    var reset: String?
    var passed: Bool
    var estimated = false
    var amount: String?

    var percentText: String { "\(percent)%" }
}

/// The 額度 card's words (DESIGN 2a.6, QUOTAS 7), as drawn and as read by accessibility.
@MainActor
enum CharacterQuotaText {
    /// The header of a source: its name, then ` · <host>` for a remote host's.
    static func name(_ item: QuotaCardItem, l10n: Localizer) -> String {
        let name = l10n(item.source.titleKey)
        return item.hostName.map { "\(name) · \($0)" } ?? name
    }

    /// The card's sources from the orb's quota snapshots at `nowMs`, each window with its estimate's live percent and
    /// used amount (`text` formats the amounts; nil leaves them out).
    static func sources(_ items: [QuotaCardItem], nowMs: Int64, l10n: Localizer,
                        text: QuotaEstimateText? = nil) -> [CharacterQuotaSource] {
        let format = l10n.quotaFormat
        return items.map { item in
            let snapshot = item.snapshot
            return CharacterQuotaSource(
                source: snapshot.source, key: item.key, name: l10n(snapshot.source.titleKey), host: item.hostName,
                tint: snapshot.source.tint,
                lines: snapshot.shown(nowMs: nowMs, words: format.strings.words).map { shown in
                    let estimate = item.estimate(shown.window)
                    let window = estimate.map(shown.live) ?? shown
                    return CharacterQuotaLine(label: window.label, percent: window.percent, bar: window.bar / 100,
                                              reset: window.resetsAtMs.map { format.resetShort($0, nowMs: nowMs) },
                                              passed: window.passed, estimated: estimate?.estimated ?? false,
                                              amount: estimate.flatMap { text?.usedAmount($0) })
                })
        }
    }

    /// `a11y.orb.quota`: each source's name followed by its windows' `a11y.quota.window`, each with
    /// `a11y.quota.amountNoCap` and, while extrapolated, `a11y.quota.estimated` (joined by `list.separator`), the
    /// sources joined by `list.separator`.
    static func sentence(_ items: [QuotaCardItem], nowMs: Int64, l10n: Localizer,
                         text: QuotaEstimateText? = nil) -> String? {
        guard !items.isEmpty else { return nil }
        let separator = l10n("list.separator")
        let words = l10n.quotaFormat.strings.words
        let parts = items.map { item in
            name(item, l10n: l10n) + " " + item.snapshot.shown(nowMs: nowMs, words: words).map { shown in
                guard let estimate = item.estimate(shown.window) else { return l10n.quotaWindowSpoken(shown, nowMs: nowMs) }
                var spoken = [l10n.quotaWindowSpoken(shown.live(estimate), nowMs: nowMs)]
                if estimate.showsAmount, let text { spoken.append(text.spokenAmount(estimate, withCap: false)) }
                if estimate.estimated { spoken.append(l10n("a11y.quota.estimated")) }
                return spoken.joined(separator: separator)
            }.joined(separator: separator)
        }
        return l10n("a11y.orb.quota", parts.joined(separator: separator))
    }
}
