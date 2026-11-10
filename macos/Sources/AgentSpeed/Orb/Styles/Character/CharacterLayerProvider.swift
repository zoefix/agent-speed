import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// What a `character` orb shows besides the speed (CHARACTERS 7, 8; DESIGN 2a.6, Readouts): the chosen pack, the
/// fastest model and the cards. `pack` is nil while no character is installed.
struct OrbCharacterContent: Equatable {
    var pack: InstalledCharacter?
    /// The fastest generating model's display name and its vendor's name.
    var modelName: String?
    var vendorName: String?
    /// The pack's accent color (vendor dot, the cards' edge).
    var accent: RGBColor?
    /// The cards switched on, and their live values (nil before the orb's first reading).
    var readouts: CharacterReadouts = .default
    var readings: OrbCharacterReadings?
    /// The agents card: `今日 1.2M` (nil before today's first token) and `3 個 agent` (nil without agents).
    var todayLine: String?
    var agentsLine: String?
    /// `費用`, the cost card's glyph.
    var costGlyph: String = ""
    /// The 首字 row's value, nil while the orb has none.
    var latency: CharacterReadoutText.Latency?
    /// The models card's rows, fastest first (every running model; the card shows the first four).
    var models: [CharacterModelLine] = []
    /// The 額度 card's sources: the orb's running, signed-in agents with a quota, in source order.
    var quota: [CharacterQuotaSource] = []
    /// Shown in place of the character while none is installed.
    var emptyLabel: String
}

/// One row of the models card: the model id, its speed as shown (`–` before a timed response) and its vendor's accent.
struct CharacterModelLine: Equatable {
    var id: String
    var speed: String
    var hasSpeed: Bool
    var accent: RGBColor?
}

/// The skinned character and the readout cards beside her (CHARACTERS 3–8, DESIGN 2a.6). `CharacterStyle` owns one per orb
/// and calls it every frame; until a provider is registered the style draws its fallback (the gauge ring and the
/// standard readout).
@MainActor
protocol CharacterLayerProvider: AnyObject {
    /// Advances the director, physics, pose swaps and readouts to the frame (once per rendered frame, before `build`).
    func step(_ frame: OrbFrame, character: OrbCharacterContent?)
    /// Adds every layer of the style, back to front. Returns false to let the style draw its fallback instead (for
    /// example while the pack is still loading).
    func build(into scene: inout OrbScene, _ frame: OrbFrame, character: OrbCharacterContent?) -> Bool
    /// The frame class at rest in this state (DESIGN 2a.6: 30 fps at idle, 60 when active, none when hidden).
    func ambientFrameClass(_ frame: OrbFrame) -> OrbFrameClass
    func isBusy(_ frame: OrbFrame) -> Bool
    /// A completed response (strength 0.3...1, heat of the response).
    func pulse(at time: TimeInterval, strength: Double, heat: Double)
    /// A celebrated new peak.
    func celebrate(at time: TimeInterval)
}

/// Where the character renderer registers itself, once, at launch.
@MainActor
enum CharacterLayers {
    /// Creates the provider of one orb; nil draws the fallback.
    static var factory: (() -> CharacterLayerProvider)?
}
