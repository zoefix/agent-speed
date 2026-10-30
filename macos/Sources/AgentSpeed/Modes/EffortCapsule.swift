import AgentSpeedCore
import AppKit
import os

/// What one effort capsule draws (DESIGN 4, Agents): the effort's meter and word, and the speed's bolts.
struct EffortCapsule: Equatable, Hashable, Sendable {
    /// Native effort token, nil when unknown.
    var effort: String?
    /// Speed token, nil when unknown.
    var speed: String?
    /// Only a config file holds the effort; the agent may not be using it.
    var effortIsConfigured = false
    var speedIsConfigured = false
    /// `mode.configured.mark`, in the current language: its width is part of the capsule's.
    var mark = ""

    init(effort: String?, speed: String?, effortIsConfigured: Bool = false, speedIsConfigured: Bool = false, mark: String = "") {
        self.effort = effort
        self.speed = speed
        self.effortIsConfigured = effortIsConfigured
        self.speedIsConfigured = speedIsConfigured
        self.mark = mark
    }

    init(_ values: EffortAndSpeed, mark: String = "") {
        self.init(effort: values.effort?.mode, speed: values.speed?.mode, effortIsConfigured: values.effort?.isConfigured ?? false,
                  speedIsConfigured: values.speed?.isConfigured ?? false, mark: mark)
    }

    @MainActor
    init(_ values: EffortAndSpeed, _ l10n: Localizer) {
        self.init(values, mark: l10n("mode.configured.mark"))
    }

    init(_ modes: ExecutionModes) {
        self.init(modes.effortAndSpeed)
    }

    /// Lit bars 0–5; nil when there is no meter (unknown effort, or a token without a level).
    var level: Int? { effort.flatMap(ModeDisplay.effortLevel) }
    var isMax: Bool { effort == "max" }
    var isTopRung: Bool { effort.map(ModeDisplay.isTopRung) ?? false }
    var bolts: Int { speed.map(ModeDisplay.bolts) ?? 0 }

    /// A capsule shows for a known effort or a speed with bolts; never for unknown values or a standard speed.
    var isVisible: Bool { effort != nil || bolts > 0 }

    /// Everything the capsule draws is configured: the dashed form. With a real part beside a configured one it stays
    /// solid, and a value that draws nothing (a standard speed) does not count either way.
    var isConfiguredForm: Bool {
        isVisible && (effort == nil || effortIsConfigured) && (bolts == 0 || speedIsConfigured)
    }

    /// How the effort's meter and word are colored.
    var tone: EffortTone { effortIsConfigured ? .configured : EffortTone(effort) }

    /// What changes the capsule's look as a whole: a change of it crossfades, without bars refilling or bolts popping.
    var provenance: [Bool] { [isConfiguredForm, effortIsConfigured, speedIsConfigured] }

    /// Whether the word is drawn: it is dropped whole in compact mode, unless it is all the capsule would show.
    func showsWord(compact: Bool) -> Bool {
        guard effort != nil else { return false }
        return !compact || (level == nil && bolts == 0)
    }

    /// The configured mark, which drops with the word and says nothing in a capsule with a real part. A capsule with
    /// only a speed has no word, so its mark drops where a word would.
    func showsMark(compact: Bool) -> Bool {
        isConfiguredForm && !mark.isEmpty && (showsWord(compact: compact) || (effort == nil && !compact))
    }

    // MARK: Geometry

    static let height: CGFloat = 18
    static let padding: CGFloat = 6
    static let partSpacing: CGFloat = 5
    static let barWidth: CGFloat = 2.5
    static let barSpacing: CGFloat = 1.5
    static let barHeights: [CGFloat] = [4, 5.5, 7, 8.5, 10]
    static let meterWidth = barWidth * 5 + barSpacing * 4
    static let wordFontSize: CGFloat = 11
    static let markFontSize: CGFloat = 9.5
    static let boltPointSize: CGFloat = 9
    static let boltOverlap: CGFloat = 3

    static let boltSize: CGSize = {
        let configuration = NSImage.SymbolConfiguration(pointSize: boltPointSize, weight: .regular)
        let image = NSImage(systemSymbolName: "bolt.fill", accessibilityDescription: nil)?
            .withSymbolConfiguration(configuration)
        return image.map { CGSize(width: ceil($0.size.width), height: ceil($0.size.height)) } ?? CGSize(width: 7, height: 10)
    }()

    private static let wordWidths = OSAllocatedUnfairLock<[String: CGFloat]>(initialState: [:])

    /// The word's width, measured once per word (board rows ask on every tick).
    static func wordWidth(_ word: String) -> CGFloat {
        measured(word, key: word, size: wordFontSize)
    }

    static func markWidth(_ mark: String) -> CGFloat {
        measured(mark, key: "mark:" + mark, size: markFontSize)
    }

    private static func measured(_ text: String, key: String, size: CGFloat) -> CGFloat {
        if let width = wordWidths.withLock({ $0[key] }) { return width }
        let font = NSFont.systemFont(ofSize: size, weight: .medium)
        let width = ceil((text as NSString).size(withAttributes: [.font: font]).width)
        wordWidths.withLock { $0[key] = width }
        return width
    }

    static func boltsWidth(_ count: Int) -> CGFloat {
        count <= 0 ? 0 : boltSize.width * CGFloat(count) - boltOverlap * CGFloat(count - 1)
    }

    /// Intrinsic width; 0 when nothing is drawn.
    func width(compact: Bool) -> CGFloat {
        var parts: [CGFloat] = []
        if level != nil { parts.append(Self.meterWidth) }
        if showsWord(compact: compact), let effort { parts.append(Self.wordWidth(effort)) }
        if showsMark(compact: compact) { parts.append(Self.markWidth(mark)) }
        if bolts > 0 { parts.append(Self.boltsWidth(bolts)) }
        guard !parts.isEmpty else { return 0 }
        return Self.padding * 2 + parts.reduce(0, +) + Self.partSpacing * CGFloat(parts.count - 1)
    }

    /// Width of the shared column: the widest capsule among the rows, 0 when none shows.
    static func columnWidth(_ capsules: [EffortCapsule], compact: Bool) -> CGFloat {
        capsules.map { $0.width(compact: compact) }.max() ?? 0
    }

    // MARK: Text

    /// The tooltip lines: effort and speed, each spelled as its token, a real top rung's explanation, and the note that
    /// says what a configured value is.
    @MainActor
    func tooltipLines(_ l10n: Localizer) -> [String] {
        var lines = [effortLine(l10n) ?? l10n("effort.tooltip", l10n("modes.unrecorded")),
                     speedLine(l10n) ?? l10n("speed.tooltip", l10n("modes.unrecorded"))]
        if effort == "ultra", !effortIsConfigured { lines.append(l10n("effort.ultra.detail")) }
        if effort == "ultracode", !effortIsConfigured { lines.append(l10n("effort.ultracode.detail")) }
        if effortIsConfigured || speedIsConfigured { lines.append(l10n("modes.configured.note")) }
        return lines
    }

    /// The effort's line; nil when unknown.
    @MainActor
    func effortLine(_ l10n: Localizer) -> String? {
        effort.map { l10n(effortIsConfigured ? "effort.tooltip.configured" : "effort.tooltip", $0) }
    }

    /// The speed's line; nil when unknown.
    @MainActor
    func speedLine(_ l10n: Localizer) -> String? {
        speed.map { l10n(speedIsConfigured ? "speed.tooltip.configured" : "speed.tooltip", Self.speedValue($0, l10n)) }
    }

    /// The end of a row's accessibility label: the effort and speed lines.
    @MainActor
    func accessibilityText(_ l10n: Localizer) -> String {
        tooltipLines(l10n).prefix(2).joined(separator: l10n.language.usesLatinCase ? ", " : "，")
    }

    @MainActor
    static func speedValue(_ speed: String, _ l10n: Localizer) -> String {
        ["standard", "fast", "ultrafast", "highspeed"].contains(speed) ? l10n("speed.value." + speed) : speed
    }
}
