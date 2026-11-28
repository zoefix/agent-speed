import AgentSpeedCore
import AppKit

/// The menu bar quota block (DESIGN 10, Menu bar and taskbar; QUOTAS): after the intelligence block, one source's first
/// two windows in QUOTAS 2 order, `5h 68%` over `7d 42%`, in the other blocks' 9 pt tabular text. Labels sit
/// left-aligned in a cell as wide as the wider label, percents right-aligned in a cell as wide as `100%`; the renderer's
/// width governors hold both cells, so the item never jitters. A number is in the label color at 85 %, red from 95 %
/// used, pale (secondary) while its window is more than an hour old or the source's sign-in no longer works; a source
/// whose quota is off or unknown reads `額度 –`. Everything else about the source (every window, resets, the update
/// time, the route's status) is in the tooltip and the spoken words.
struct StatusItemQuota: Equatable {
    static let size: CGFloat = 9
    static let gap: CGFloat = 8
    static let pitch: CGFloat = 10
    static let labelGap: CGFloat = 3
    /// The usual widest percent: an extrapolated `≈100%` widens the cell through the governor.
    static let widestNumber = "100%"
    static let approximate = "≈"
    static let dash = "–"
    /// A window this full reads red (DESIGN 4, the dashboard's red percent).
    static let criticalPercent = 95

    enum Ink: Equatable {
        /// The label color at 85 % (Increase Contrast: full).
        case number
        /// Old, signed out or unknown: the secondary color.
        case pale
        /// 95 % used or more.
        case critical
    }

    struct Line: Equatable {
        var label: String
        /// `68%`, or `–` without a value.
        var number: String
        /// Extrapolated (QUOTAS 10.7): a leading `≈` in the secondary color.
        var approximate = false
        var ink: Ink
    }

    /// The source's state as the block reads it.
    enum Form: Equatable {
        /// The source is disabled or its quota switch is off.
        case off
        /// No snapshot with a window.
        case none
        case shown
    }

    var form: Form
    /// One or two lines; one is drawn alone, centred vertically.
    var lines: [Line]
    /// Room for the labels and the percents: at least the drawn text, wider while a governor holds a longer one.
    var labelCell: CGFloat
    var numberCell: CGFloat
    /// `a11y.bar.quota` with every window, or the off / none sentence; then the route's status sentence.
    var spoken: String
    /// The tooltip's quota lines: the header, every window, the route's status.
    var tooltip: [String]

    static var font: NSFont { .monospacedDigitSystemFont(ofSize: size, weight: .medium) }

    @MainActor
    init?(model: AppModel) {
        guard let source = model.quota.barSource else { return nil }
        let quota = model.quota
        let status = Self.routeStatus(source, quota: quota)
        self.init(source: source, read: model.settings.showsQuota(source), snapshot: quota.lines(source),
                  estimate: { quota.estimateLine(source, window: $0) }, status: status?.status,
                  statusText: status.map { QuotaStatusText.text($0.status, route: $0.route, model: model) },
                  percent: model.settings.barQuotaPercent, nowMs: quota.clockMs, l10n: model.l10n)
    }

    /// The block for `source`: `read` while its quota is read (enabled, switch on), its `snapshot` with windows, each
    /// window's estimate, and its route's status other than `ok` with that status's sentence.
    @MainActor
    init(source: SourceKind, read: Bool, snapshot: QuotaSnapshot?,
         estimate: (QuotaWindow) -> QuotaEstimateDisplay? = { _ in nil }, status: QuotaRouteStatus? = nil,
         statusText: String? = nil, percent: BarQuotaPercent, nowMs: Int64, l10n: Localizer) {
        let name = l10n(source.titleKey)
        guard read, let snapshot, snapshot.hasWindows else {
            form = read ? .none : .off
            lines = [Line(label: l10n("quota.title"), number: Self.dash, ink: .pale)]
            let sentence = l10n(read ? "bar.quota.none" : "bar.quota.off", name)
            let statusLine = read ? statusText : nil
            spoken = Self.spoken(sentence, status: statusLine, l10n: l10n)
            tooltip = [sentence] + [statusLine].compactMap { $0 }
            labelCell = 0
            numberCell = 0
            labelCell = labelWidth
            numberCell = numberWidth
            return
        }
        form = .shown
        let format = l10n.quotaFormat
        let signedOut = status.map(Self.isSignedOut) ?? false
        var shown: [Line] = []
        var words: [String] = []
        for vendor in snapshot.shown(nowMs: nowMs, words: format.strings.words) {
            let estimated = estimate(vendor.window)
            let window = estimated.map(vendor.live) ?? vendor
            let approximate = estimated?.estimated ?? false
            let age = format.age(observedMs: snapshot.time(of: vendor.window), nowMs: nowMs)
            let value = percent == .used ? window.percent : 100 - window.percent
            let ink: Ink = age != nil || signedOut ? .pale : window.percent >= Self.criticalPercent ? .critical : .number
            shown.append(Line(label: window.label, number: "\(value)%", approximate: approximate, ink: ink))
            var spokenWindow = Self.windowWords(window, value: value, percent: percent, nowMs: nowMs, l10n: l10n)
            if approximate { spokenWindow += l10n("list.separator") + l10n("a11y.quota.estimated") }
            if let age { spokenWindow += " · " + age }
            words.append(spokenWindow)
        }
        lines = Array(shown.prefix(2))
        spoken = Self.spoken(l10n("a11y.bar.quota", name, words.joined(separator: l10n("list.separator"))), status: statusText,
                             l10n: l10n)
        let updated = l10n("quota.updated", AppModel.quotaTime(snapshot.observedMs, nowMs: nowMs, locale: l10n.locale))
        tooltip = [l10n("bar.quota.title", name) + " · " + updated] + words + [statusText].compactMap { $0 }
        labelCell = 0
        numberCell = 0
        labelCell = labelWidth
        numberCell = numberWidth
    }

    /// `a11y.quota.window` (used) or `a11y.quota.windowLeft` (remaining) with the shown percent and the long reset, each
    /// `…NoReset` without a reset time.
    @MainActor
    private static func windowWords(_ window: QuotaShownWindow, value: Int, percent: BarQuotaPercent, nowMs: Int64,
                                    l10n: Localizer) -> String {
        let key = percent == .used ? "a11y.quota.window" : "a11y.quota.windowLeft"
        guard let resets = window.resetsAtMs else { return l10n(key + "NoReset", window.label, value) }
        return l10n(key, window.label, value, l10n.quotaFormat.resetLong(resets, nowMs: nowMs))
    }

    /// The words, then the route's status sentence, so a number that is pale for a sign-in is said why.
    @MainActor
    private static func spoken(_ words: String, status: String?, l10n: Localizer) -> String {
        [words, status].compactMap { $0 }.joined(separator: l10n("list.separator"))
    }

    /// A sign-in that no longer works (QUOTAS 9.1): the numbers belong to it, so they read pale.
    static func isSignedOut(_ status: QuotaRouteStatus) -> Bool {
        switch status {
        case .noCredential, .noBrowser, .expired, .denied: true
        default: false
        }
    }

    /// The first route of the source (Qoder's one route for both Qoder sources) whose status line is not `ok`.
    @MainActor
    static func routeStatus(_ source: SourceKind, quota: QuotaController) -> (route: QuotaRoute, status: QuotaRouteStatus)? {
        let key: SourceKind = source == .qoderCli ? .qoder : source
        for route in QuotaRoute.allCases where route.source == key {
            guard let status = quota.status(route) else { continue }
            if case .ok = status { continue }
            return (route, status)
        }
        return nil
    }

    private static func width(_ text: String) -> CGFloat {
        ceil((text as NSString).size(withAttributes: [.font: font]).width)
    }

    private static func numberText(_ line: Line) -> String {
        (line.approximate ? approximate : "") + line.number
    }

    /// The drawn width of the wider label.
    var labelWidth: CGFloat { lines.map { Self.width($0.label) }.max() ?? 0 }

    /// The drawn width of the wider percent, at least `100%`'s. Digits are tabular, so it only changes with `≈`.
    var numberWidth: CGFloat { max(Self.width(Self.widestNumber), lines.map { Self.width(Self.numberText($0)) }.max() ?? 0) }

    /// One line: drawn alone, centred vertically.
    var alone: Bool { lines.count == 1 }

    /// The block's width, gap included.
    var width: CGFloat { Self.gap + labelCell + Self.labelGap + numberCell }

    /// Baselines of line 1 and line 2; a line alone sits halfway between them, at the item's text centre.
    func baselines(height: CGFloat) -> (first: CGFloat, second: CGFloat) {
        let first = (height / 2 - Self.pitch / 2 + Self.font.capHeight / 2).rounded()
        return (alone ? first + Self.pitch / 2 : first, first + Self.pitch)
    }

    func color(_ ink: Ink, palette: StatusItemPalette) -> RGBColor {
        switch ink {
        case .number: palette.increasedContrast ? palette.primary : palette.primary.opacity(0.85)
        case .pale: palette.secondary
        case .critical: palette.critical
        }
    }

    func draw(at x: CGFloat, height: CGFloat, palette: StatusItemPalette) {
        let font = Self.font
        let baselines = baselines(height: height)
        let left = x + Self.gap
        let numberRight = left + labelCell + Self.labelGap + numberCell
        for (line, baseline) in zip(lines, [baselines.first, baselines.second]) {
            func draw(_ text: String, at x: CGFloat, color: RGBColor) {
                NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: color.nsColor])
                    .draw(at: CGPoint(x: x, y: baseline - font.ascender))
            }
            draw(line.label, at: left, color: palette.secondary)
            let numberWidth = (line.number as NSString).size(withAttributes: [.font: font]).width
            let numberX = numberRight - numberWidth
            draw(line.number, at: numberX, color: color(line.ink, palette: palette))
            if line.approximate {
                let mark = (Self.approximate as NSString).size(withAttributes: [.font: font]).width
                draw(Self.approximate, at: numberX - mark, color: palette.secondary)
            }
        }
    }
}
