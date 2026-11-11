import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// The orb instrument in SwiftUI (DESIGN section 2): a static body under one GPU-drawn layer. Used for the dashboard's
/// embedded orb (which carries the hero vehicle instead of the readout, DESIGN 4c) and for still frames of any style
/// (previews). Floating orbs are AppKit windows (`OrbHostView`) on the shared frame clock.
struct OrbView: View {
    /// The canvas is this many times the disc diameter so the aura and sparks can leave the disc.
    static let canvasScale: CGFloat = 1.5

    let model: AppModel
    let feed: OrbFeed
    var diameter: CGFloat
    var surface: OrbSurface = .embedded
    var interaction: OrbInteraction?

    @State private var ownAnimator: OrbAnimator
    private let injectedAnimator: OrbAnimator?
    private let fixedTime: TimeInterval?
    private let forcesSolidBody: Bool
    private let contentOverride: ((inout OrbContent) -> Void)?

    @Environment(\.orbStillTime) private var environmentStillTime
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    /// Frames per second at most while active (Settings previews); nil follows the display.
    var frameCap: Int?

    init(model: AppModel, feed: OrbFeed? = nil, diameter: CGFloat, surface: OrbSurface = .embedded, interaction: OrbInteraction? = nil,
         frameCap: Int? = nil) {
        self.model = model
        self.feed = feed ?? model.mainFeed
        self.diameter = diameter
        self.surface = surface
        self.interaction = interaction
        self.frameCap = frameCap
        _ownAnimator = State(initialValue: OrbAnimator(surface: surface))
        injectedAnimator = nil
        fixedTime = nil
        forcesSolidBody = false
        contentOverride = nil
    }

    /// A still frame at `time` from a prepared animator (previews).
    init(model: AppModel, feed: OrbFeed? = nil, diameter: CGFloat, surface: OrbSurface, animator: OrbAnimator, time: TimeInterval,
         interaction: OrbInteraction? = nil, content: ((inout OrbContent) -> Void)? = nil) {
        self.model = model
        self.feed = feed ?? model.mainFeed
        self.diameter = diameter
        self.surface = surface
        self.interaction = interaction
        _ownAnimator = State(initialValue: animator)
        injectedAnimator = animator
        fixedTime = time
        forcesSolidBody = true
        contentOverride = content
    }

    private var canvasSide: CGFloat { diameter * Self.canvasScale }

    var body: some View {
        let animator = injectedAnimator ?? ownAnimator
        var content = OrbContentBuilder.content(model: model, feed: feed, diameter: diameter, dark: colorScheme == .dark,
                                                contrast: contrast == .increased, style: animator.style,
                                                showsReadout: surface != .embedded)
        let _ = contentOverride?(&content)
        let reduceMotion = model.accessibility.reduceMotion
        let hovered = surface == .floating && (interaction?.isHovered ?? false)
        ZStack {
            OrbBody(
                diameter: diameter,
                canvasSide: canvasSide,
                tokens: content.tokens,
                opacity: 1,
                solid: forcesSolidBody || surface == .embedded || model.accessibility.reduceTransparency,
                increaseContrast: content.increaseContrast,
                style: animator.style,
                rim: content.bezel == nil
            )
            .equatable()
            .drawingGroup()
            if let stillTime = fixedTime ?? environmentStillTime {
                let _ = animator.follow(feed, model: model)
                let _ = fixedTime == nil ? animator.primeForStill(model: model, gauge: feed.gauge) : ()
                if let image = OrbStill.image(content: content, animator: animator, model: model, feed: feed,
                                              canvasSide: canvasSide, time: stillTime) {
                    Image(decorative: image, scale: 2)
                }
            } else {
                OrbMetalView(mode: animator.frameMode, followsDisplay: surface == .embedded, frameCap: frameCap,
                             redrawKey: redrawKey(content: content)) { scene, size, scale, time in
                    OrbSceneBuilder.build(into: &scene, content: content, animator: animator, model: model, feed: feed,
                                          canvasSide: size.width, scale: scale, time: time)
                }
                .frame(width: canvasSide, height: canvasSide)
                if surface == .embedded { idleSweep(animator: animator, tokens: content.tokens) }
            }
        }
        .frame(width: canvasSide, height: canvasSide)
        .scaleEffect(hovered && !reduceMotion ? 1.03 : 1)
        .animation(reduceMotion ? nil : Motion.hover, value: hovered)
        .padding(surface == .embedded ? -(canvasSide - diameter) / 2 : 0)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
        .onAppear {
            guard fixedTime == nil, environmentStillTime == nil else { return }
            animator.attach(model: model, feed: feed, interaction: interaction)
        }
        .onDisappear {
            guard fixedTime == nil, environmentStillTime == nil else { return }
            animator.detach()
        }
    }

    private struct RedrawKey: Hashable {
        var content: String
        var dark: Bool
        var readout: Double
        var heat: Double
        var tier: SpeedTier
        var title: SpeedTitle
        var effects: EffectsLevel
    }

    private func redrawKey(content: OrbContent) -> AnyHashable {
        let gauge = feed.gauge
        let text = [content.unit, content.titleLabel, content.infoLine, content.language.rawValue]
            + content.generating.map(\.rawValue) + ["\(content.diameter)", "\(content.increaseContrast)"]
        return AnyHashable(RedrawKey(
            content: text.joined(separator: "|"),
            dark: content.tokens.isDark,
            readout: gauge.readoutValue,
            heat: gauge.targetHeat,
            tier: gauge.tier,
            title: gauge.title,
            effects: model.settings.effects
        ))
    }

    private func idleSweep(animator: OrbAnimator, tokens: OrbTokens) -> some View {
        let gauge = feed.gauge
        let effects = animator.effects(for: model)
        let visible = interaction?.isVisibleOnScreen ?? true
        let color = tokens.isDark ? RGBColor.white.opacity(0.18) : HeatPalette.color(0.02).opacity(0.45)
        return OrbIdleSweep(
            diameter: diameter,
            canvasSide: canvasSide,
            color: color,
            active: gauge.tier == .idle && effects.idleSweep && visible,
            reduceMotion: effects.reduceMotion,
            epoch: animator.idleEpoch(for: gauge)
        )
        .frame(width: canvasSide, height: canvasSide)
    }
}

/// Resolves an orb's content from observable state (read in a view body or an observation loop).
@MainActor
enum OrbContentBuilder {
    /// `theme` nil = the General title theme.
    static func content(model: AppModel, feed: OrbFeed, diameter: CGFloat, dark: Bool, contrast: Bool,
                        style: OrbStyleKind, showsReadout: Bool, theme: TitleTheme? = nil) -> OrbContent {
        let l10n = model.l10n
        let gauge = feed.gauge
        let titles = model.titles
        let theme = theme ?? titles.general
        let money = feed.money
        return OrbContent(
            diameter: diameter,
            tokens: dark ? .dark : .light,
            increaseContrast: contrast || model.accessibility.increaseContrast,
            reduceTransparency: model.accessibility.reduceTransparency,
            unit: money?.unit ?? l10n("unit.tokS"),
            titleLabel: titles.label(gauge.title, in: theme, l10n: l10n),
            previousTitleLabel: titles.label(gauge.previousTitle, in: theme, l10n: l10n),
            infoLine: money?.hoverLine ?? l10n("today.summary", SpeedFormat.tokens(feed.todayTokens), feed.agentCount),
            peakLabel: l10n("peak.new"),
            latinTracking: l10n.language.usesLatinCase,
            language: l10n.language,
            generating: feed.generating,
            showsReadout: showsReadout,
            style: style,
            bezel: feed.bezel,
            unavailableTitle: feed.unavailableTitleKey.map { l10n($0) },
            money: money.map {
                // Without a unit line (`minimal`, small orbs) the value carries the symbol (DESIGN 9).
                let unitLine = OrbReadoutLayout.hasUnitLine(style: style, diameter: diameter)
                return OrbMoneyText(number: unitLine ? $0.number : $0.inline, value: $0.value, atLeast: $0.atLeast,
                                    unknown: $0.unknown)
            },
            lines: showsReadout ? feed.lines : nil,
            character: style == .character ? characterContent(model: model, feed: feed) : nil
        )
    }

    /// The character, model and cards of a `character` orb (CHARACTERS 7, 8; DESIGN 2a.6, Readouts).
    static func characterContent(model: AppModel, feed: OrbFeed) -> OrbCharacterContent {
        let l10n = model.l10n
        let pack = feed.characterID.flatMap(model.characters.installed)
        let vendor = feed.vendors.fastestVendor
        let readings = feed.characterReadings
        let readouts = readings?.readouts ?? .default
        let models = (readings?.models ?? []).map { row in
            CharacterModelLine(id: row.model, speed: row.speed.map(String.init) ?? CharacterReadoutText.noSpeed,
                               hasSpeed: row.speed != nil, accent: row.vendor.flatMap(model.characters.accent))
        }
        return OrbCharacterContent(
            pack: pack,
            modelName: feed.vendors.fastestModel,
            vendorName: vendor.flatMap { model.priceBook.current.providers[$0]?.name },
            accent: pack?.manifest.accentRGB.map { RGBColor(red: $0.r, green: $0.g, blue: $0.b) },
            readouts: readouts,
            readings: feed.characterReadings,
            todayLine: feed.todayTokens > 0 ? l10n("boards.today", SpeedFormat.tokens(feed.todayTokens)) : nil,
            agentsLine: feed.agentCount > 0 ? l10n("agents.count", feed.agentCount) : nil,
            costGlyph: l10n("cost.glyph"),
            latency: readings?.latencyMs.map { CharacterReadoutText.latency(ms: $0, language: l10n.language) },
            models: models,
            quota: readings.map { readings in
                readings.quota.isEmpty ? []
                    : CharacterQuotaText.sources(readings.quota, nowMs: model.quota.clockMs, l10n: l10n)
            } ?? [],
            emptyLabel: l10n("character.empty")
        )
    }
}

/// Still frames of an orb's dynamic layers (previews, the frame harness).
@MainActor
enum OrbStill {
    private static var scene = OrbScene(capacity: 2048)

    static func image(content: OrbContent, animator: OrbAnimator, model: AppModel, feed: OrbFeed, canvasSide: CGFloat,
                      time: TimeInterval, scale: CGFloat = 2, contentScale: CGFloat = 1) -> CGImage? {
        OrbSceneBuilder.build(into: &scene, content: content, animator: animator, model: model, feed: feed,
                              canvasSide: canvasSide, scale: scale, time: time, contentScale: contentScale)
        return OrbSnapshot.image(scene, side: canvasSide, scale: scale)
    }
}

extension EnvironmentValues {
    /// When set, every orb in the subtree renders a still frame at this time (previews).
    @Entry var orbStillTime: TimeInterval?
}
