import AgentSpeedCore
import AppKit

extension Localizer {
    /// QUOTAS 7 in the app's language and the local time zone.
    var quotaFormat: QuotaFormat {
        QuotaFormat(strings: QuotaStrings { self($0) }, locale: locale)
    }

    /// `a11y.quota.window`: `7d 已用 94%，週五 09:00 重置`, or `a11y.quota.windowNoReset` without a reset time.
    func quotaWindowSpoken(_ window: QuotaShownWindow, nowMs: Int64) -> String {
        guard let resets = window.resetsAtMs else { return self("a11y.quota.windowNoReset", window.label, window.percent) }
        return self("a11y.quota.window", window.label, window.percent, quotaFormat.resetLong(resets, nowMs: nowMs))
    }
}

/// How full a window is, for its fill (DESIGN 4, 2a.6): the source color, amber from 80 %, the system red from 95 %.
enum QuotaLevel: Equatable {
    case normal
    case warning
    case critical

    init(percent: Int) {
        self = percent >= 95 ? .critical : percent >= 80 ? .warning : .normal
    }

    /// `#F5A524` on dark, `#B86E00` on light.
    static func amber(dark: Bool) -> RGBColor { RGBColor(hex: dark ? 0xF5A524 : 0xB86E00) }

    /// The system red as it resolves in that appearance.
    static func red(dark: Bool) -> RGBColor {
        var color = RGBColor(hex: dark ? 0xFF453A : 0xFF3B30)
        NSAppearance(named: dark ? .darkAqua : .aqua)?.performAsCurrentDrawingAppearance {
            if let red = NSColor.systemRed.usingColorSpace(.sRGB) {
                color = RGBColor(red: red.redComponent, green: red.greenComponent, blue: red.blueComponent)
            }
        }
        return color
    }

    /// The fill of a source's bar at this level; `nil` keeps the source color.
    func fill(dark: Bool) -> RGBColor? {
        switch self {
        case .normal: nil
        case .warning: Self.amber(dark: dark)
        case .critical: Self.red(dark: dark)
        }
    }
}
