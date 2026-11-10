import Foundation

/// What a usage limit window covers (QUOTAS 2, 9.1).
public enum QuotaWindowKind: String, Sendable, CaseIterable {
    /// The rolling 5-hour window.
    case fiveHour
    /// The weekly window over every model.
    case sevenDay
    /// A weekly window of one model or model family, named by the vendor: `sevenDay` with a name.
    case modelWeek
    /// Any other length (`1d` is the daily window).
    case other
    /// A monthly or billing-cycle window.
    case month
    /// A credit pool with no stated length.
    case credits

    /// Equal lengths sort in this order.
    var rank: Int {
        switch self {
        case .fiveHour: 0
        case .other: 1
        case .sevenDay: 2
        case .modelWeek: 3
        case .month: 4
        case .credits: 5
        }
    }

    /// The window's length when the kind fixes it (a month without known ends counts 30 days).
    var fixedMinutes: Int? {
        switch self {
        case .fiveHour: 300
        case .sevenDay, .modelWeek: 10080
        case .month: 43200
        case .other, .credits: nil
        }
    }
}

/// One limit window of an account (QUOTAS 2): how much is used and when it starts over.
public struct QuotaWindow: Equatable, Hashable, Sendable {
    /// The language-dependent parts of labels: `quota.window.month` and `quota.window.credits`.
    public struct Words: Equatable, Sendable {
        public var month: String
        public var credits: String

        public init(month: String, credits: String) {
            self.month = month
            self.credits = credits
        }

        public static let english = Words(month: "mo", credits: "credits")
    }

    public var kind: QuotaWindowKind
    /// A model group (`Fable`, `Opus`, `Gemini`); any kind may carry one.
    public var name: String?
    /// Nil for a credit pool.
    public var minutes: Int?
    public var usedPercent: Double
    public var resetsAtMs: Int64?

    public init(kind: QuotaWindowKind, name: String? = nil, minutes: Int? = nil, usedPercent: Double, resetsAtMs: Int64?) {
        self.kind = kind
        self.name = name
        self.minutes = minutes ?? kind.fixedMinutes
        self.usedPercent = usedPercent
        self.resetsAtMs = resetsAtMs
    }

    /// A non-empty name.
    var groupName: String? { name.flatMap { $0.isEmpty ? nil : $0 } }

    /// `5h`, `7d`, `月`, `點數`, or the length of any other window (`2h`, `1d`, `45m`); a named window puts its name
    /// first (`Fable 7d`, `Gemini 5h`).
    public func label(_ words: Words = .english) -> String {
        let base: String
        switch kind {
        case .fiveHour: base = "5h"
        case .sevenDay, .modelWeek: base = "7d"
        case .month: base = words.month
        case .credits: base = words.credits
        case .other:
            let minutes = minutes ?? 0
            if minutes > 0, minutes % 1440 == 0 {
                base = "\(minutes / 1440)d"
            } else if minutes > 0, minutes % 60 == 0 {
                base = "\(minutes / 60)h"
            } else {
                base = "\(minutes)m"
            }
        }
        return groupName.map { "\($0) \(base)" } ?? base
    }

    /// The window has started over (QUOTAS 2, *Reset passed*).
    public func hasReset(nowMs: Int64) -> Bool {
        resetsAtMs.map { nowMs >= $0 } ?? false
    }

    /// Shortest first; credits and unknown lengths last; equal lengths in kind order.
    var lengthKey: (Int, Int) {
        (kind == .credits ? Int.max : minutes ?? Int.max, kind.rank)
    }
}

/// The limits of one source's account at one moment (QUOTAS 2). `observedMs` is when the vendor computed them.
public struct QuotaSnapshot: Equatable, Hashable, Sendable {
    public var source: SourceKind
    public var observedMs: Int64
    /// In section 2's order.
    public private(set) var windows: [QuotaWindow]

    public init(source: SourceKind, observedMs: Int64, windows: [QuotaWindow]) {
        self.source = source
        self.observedMs = observedMs
        self.windows = QuotaSnapshot.ordered(windows)
    }

    public var hasWindows: Bool { !windows.isEmpty }

    /// Section 2's order: the windows without a name by length, then the named ones grouped by name in the vendor's
    /// order, each group by length. Equal keys keep the vendor's order.
    public static func ordered(_ windows: [QuotaWindow]) -> [QuotaWindow] {
        func byLength(_ group: [QuotaWindow]) -> [QuotaWindow] {
            group.enumerated().sorted { lhs, rhs in
                (lhs.element.lengthKey.0, lhs.element.lengthKey.1, lhs.offset) < (rhs.element.lengthKey.0, rhs.element.lengthKey.1, rhs.offset)
            }.map(\.element)
        }
        var names: [String] = []
        for window in windows {
            if let name = window.groupName, !names.contains(name) { names.append(name) }
        }
        return byLength(windows.filter { $0.groupName == nil })
            + names.flatMap { name in byLength(windows.filter { $0.groupName == name }) }
    }

    /// The windows as displayed at `nowMs` (QUOTAS 2).
    public func shown(nowMs: Int64, words: QuotaWindow.Words = .english) -> [QuotaShownWindow] {
        windows.map { QuotaShownWindow($0, nowMs: nowMs, words: words) }
    }

    /// How old the values are.
    public func ageMs(nowMs: Int64) -> Int64 { nowMs - observedMs }
}

/// A window as drawn: a window whose reset time has passed shows `0%`, an empty bar and no reset time until a newer
/// snapshot arrives (QUOTAS 2).
public struct QuotaShownWindow: Equatable, Hashable, Sendable {
    public var window: QuotaWindow
    public var label: String
    /// The integer of section 2, clamped to 0…100.
    public var percent: Int
    /// The bar's fill, `clamp(usedPercent, 0, 100)`.
    public var bar: Double
    public var resetsAtMs: Int64?
    public var passed: Bool

    public init(_ window: QuotaWindow, nowMs: Int64, words: QuotaWindow.Words = .english) {
        self.window = window
        label = window.label(words)
        passed = window.hasReset(nowMs: nowMs)
        percent = passed ? 0 : QuotaPercent.rounded(window.usedPercent)
        bar = passed ? 0 : QuotaPercent.clamped(window.usedPercent)
        resetsAtMs = passed ? nil : window.resetsAtMs
    }

    /// `23%`, with no space in any language (QUOTAS 7).
    public var percentText: String { "\(percent)%" }
}

public enum QuotaPercent {
    /// Rounded half up to an integer and clamped to 0…100: `100%` once a window is exhausted, even at 103.
    public static func rounded(_ used: Double) -> Int {
        guard used.isFinite else { return 0 }
        let value = (used + 0.5).rounded(.down)
        return Int(min(100, max(0, value)))
    }

    public static func clamped(_ used: Double) -> Double {
        guard used.isFinite else { return 0 }
        return min(100, max(0, used))
    }
}

enum QuotaText {
    /// Python's `casefold` for the names vendors use; Foundation's case folding covers them.
    static func fold(_ text: String) -> String {
        text.folding(options: [.caseInsensitive], locale: nil)
    }
}
