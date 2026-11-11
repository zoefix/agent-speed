import AgentSpeedCore
import AppKit

/// One quota line of a Sources row as drawn (DESIGN 4, *Quota lines*).
struct SourceQuotaLine: Identifiable, Equatable {
    var id: String { label }
    var label: String
    var percent: Int
    var percentText: String
    /// The bar's fill, 0…1.
    var fraction: Double
    /// The share of the window that has passed, while known (QUOTAS 7, *Pace*).
    var pace: Double?
    var reset: String?
    /// The short reset (QUOTAS 7), which a section with amounts shows.
    var resetShort: String?
    /// The long reset, when the line shows the short one or none.
    var tooltipReset: String?
    /// `quota.age` while the snapshot is more than an hour old.
    var age: String?
    var level: QuotaLevel
    /// The window started over: its bar empties more slowly.
    var passed: Bool
    /// `quota.updated` with the snapshot's time, and `quota.pace` while known.
    var updated: String
    var paceText: String?
    /// The age, when the line has no room for it.
    var tooltipAge: String?
    var spoken: String
    /// The percent is extrapolated from this computer's spend (QUOTAS 10.7): a leading `≈`.
    var estimated = false
    /// The used amount (QUOTAS 10.8), and ` / ≈5,742` or ` / ≥598` after it while an estimate exists.
    var amount: String?
    var amountCap: String?
    /// The estimate's tooltip lines (DESIGN 4 *Tooltip*) and its closing note.
    var estimateDetails: [String] = []
    var estimateNote: String?

    var tooltip: String {
        (tooltipHead + estimateDetails + [estimateNote].compactMap { $0 }).joined(separator: "\n")
    }

    /// The lines above the tooltip's hairline.
    var tooltipHead: [String] { [tooltipReset, updated, tooltipAge, paceText].compactMap { $0 } }
}

/// The columns every quota line of the section shares, so labels, bars, percents and resets line up from row to row.
struct SourceQuotaColumns: Equatable {
    var label: CGFloat = 0
    var percent: CGFloat = 0
    /// The used amount and cap (QUOTAS 10.8), left-aligned; 0 while no line has one.
    var amount: CGFloat = 0
    var reset: CGFloat = 0

    static let gap: CGFloat = 8
    /// The bar's comfortable length: an age is only added after the reset while the bar keeps it.
    static let minimumBar: CGFloat = 88
    /// The shortest bar, for sections with long group labels (`Claude/GPT 7d`): the reset text keeps its room first.
    static let compactBar: CGFloat = 52

    static var labelFont: NSFont { NSFont.systemFont(ofSize: 11, weight: .medium) }
    static var percentFont: NSFont { NSFont.monospacedDigitSystemFont(ofSize: 11.5, weight: .semibold) }
    static var resetFont: NSFont { NSFont.systemFont(ofSize: 11) }
    static var amountFont: NSFont { NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium) }
    /// The `≈` of an extrapolated percent, kerned 1 pt to the digits.
    static let approximateKern: CGFloat = 1

    /// The lines' room in a Sources row: the dashboard's column less the dot and the gap before the name.
    static let nameInset: CGFloat = 8 + Spacing.s
    /// A host's source sub-line (DESIGN 13 *Host quotas*): its 6 pt dot and the gap before the source's name.
    static let hostSourceInset: CGFloat = nameInset + 6 + 6
    @MainActor static var lineWidth: CGFloat { width(inset: nameInset) }

    @MainActor static func width(inset: CGFloat) -> CGFloat {
        DashboardController.width - 2 * DashboardLayout.padding - inset
    }

    /// The section's columns, and its lines with an age only where the age still fits after the reset: a line that
    /// cannot hold both keeps its reset, and its tooltip keeps the time (DESIGN 4, *Quota lines*). With amounts the
    /// section shows the short resets, or none when even those do not fit beside the shortest bar; the long reset
    /// then opens each line's tooltip (user's choice 2026-11-11).
    @MainActor static func fit<Key: Hashable>(_ quota: [Key: [SourceQuotaLine]], separator: String,
                                              inset: CGFloat = nameInset)
        -> (lines: [Key: [SourceQuotaLine]], columns: SourceQuotaColumns) {
        var columns = SourceQuotaColumns()
        let all = quota.values.flatMap { $0 }
        columns.label = all.map { width($0.label, labelFont) }.max() ?? 0
        columns.percent = width("≈100%", percentFont) + approximateKern
        columns.amount = all.map { width(($0.amount ?? "") + ($0.amountCap ?? ""), amountFont) }.max() ?? 0
        let rest = width(inset: inset) - columns.label - columns.percent - (columns.amount > 0 ? columns.amount + gap : 0)
            - 3 * gap
        let room = rest - minimumBar
        let resetRoom = rest - compactBar
        var form = ResetForm.long
        if columns.amount > 0 {
            form = all.contains { width($0.resetShort ?? "", resetFont) > resetRoom } ? .none : .short
        }
        var fitted = quota
        for (source, lines) in quota {
            fitted[source] = lines.map { line in
                var line = line
                if form != .long, line.reset != nil {
                    line.tooltipReset = line.reset
                    line.reset = form == .short ? line.resetShort : nil
                }
                if let age = line.age, width(tail(line.reset, age, separator), resetFont) > room {
                    line.age = nil
                    line.tooltipAge = age
                }
                return line
            }
        }
        columns.reset = fitted.values.flatMap { $0 }.map { width(tail($0.reset, $0.age, separator), resetFont) }.max() ?? 0
        return (fitted, columns)
    }

    /// The reset a section shows (DESIGN 4 *Amount*).
    private enum ResetForm { case long, short, none }

    static func tail(_ reset: String?, _ age: String?, _ separator: String) -> String {
        [reset, age].compactMap { $0 }.joined(separator: separator)
    }

    init() {}

    static func width(_ text: String, _ font: NSFont) -> CGFloat {
        guard !text.isEmpty else { return 0 }
        return ceil(NSAttributedString(string: text, attributes: [.font: font]).size().width) + 1
    }
}

extension AppModel {
    /// The quota lines of a listed source (DESIGN 4): its windows in QUOTAS 2 order while its switch is on.
    func quotaLines(_ source: SourceKind) -> [SourceQuotaLine] {
        quota.lines(source).map { quotaLines($0) } ?? []
    }

    /// A remote host's sources with a quota and their lines (DESIGN 13 *Host quotas*), in source order.
    func hostQuotaLines(_ hostId: String) -> [(source: SourceKind, lines: [SourceQuotaLine])] {
        quota.hostSnapshots(hostId).map { ($0.source, quotaLines($0, host: hostId)) }
    }

    /// Each window with its estimate (QUOTAS 10.7, 10.8) while the account has one: the live percent and bar, the
    /// amount used with the cap, and the estimate's tooltip lines.
    func quotaLines(_ snapshot: QuotaSnapshot, host: String? = nil) -> [SourceQuotaLine] {
        let now = quota.clockMs
        let format = l10n.quotaFormat
        let age = format.age(observedMs: snapshot.observedMs, nowMs: now)
        let updated = l10n("quota.updated", Self.quotaTime(snapshot.observedMs, nowMs: now, locale: l10n.locale))
        return snapshot.shown(nowMs: now, words: format.strings.words).map { shown in
            var window = shown
            let estimate = quota.estimateLine(snapshot.source, host: host, window: shown.window)
            if let estimate { window = window.live(estimate) }
            let pace = window.passed ? nil : QuotaFormat.pace(window.window, nowMs: now)
            var line = SourceQuotaLine(
                label: window.label,
                percent: window.percent,
                percentText: window.percentText,
                fraction: window.bar / 100,
                pace: pace,
                reset: window.resetsAtMs.map { format.resetLong($0, nowMs: now) },
                resetShort: window.resetsAtMs.map { format.resetShort($0, nowMs: now) },
                age: age,
                level: QuotaLevel(percent: window.percent),
                passed: window.passed,
                updated: updated,
                paceText: pace.map(format.paceText),
                spoken: l10n.quotaWindowSpoken(window, nowMs: now)
            )
            if let estimate { applyEstimate(estimate, to: &line) }
            return line
        }
    }

    private func applyEstimate(_ estimate: QuotaEstimateDisplay, to line: inout SourceQuotaLine) {
        line.estimated = estimate.estimated
        let text = QuotaEstimateText(l10n: l10n, cost: cost)
        if estimate.showsAmount {
            (line.amount, line.amountCap) = text.amountCell(estimate)
            line.spoken += l10n("list.separator") + text.spokenAmount(estimate)
        }
        if estimate.estimated { line.spoken += l10n("list.separator") + l10n("a11y.quota.estimated") }
        line.estimateDetails = text.details(estimate)
        line.estimateNote = estimate.hasEstimate ? l10n("quota.estimate.note") : nil
    }

    /// The snapshot's time: the time alone today, else the date and time.
    static func quotaTime(_ ms: Int64, nowMs: Int64, locale: Locale) -> String {
        let date = Date(timeIntervalSince1970: Double(ms) / 1000)
        let today = Calendar.current.isDate(date, inSameDayAs: Date(timeIntervalSince1970: Double(nowMs) / 1000))
        var style = Date.FormatStyle(date: today ? .omitted : .abbreviated, time: .shortened)
        style.locale = locale
        return date.formatted(style)
    }
}
