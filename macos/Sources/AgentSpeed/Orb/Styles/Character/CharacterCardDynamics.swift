import AgentSpeedCharacter
import Foundation

/// What the cards carry from frame to frame (DESIGN 2a.6, Readouts), kept per orb by the renderer: the 首字 row's fade
/// with the last value it showed, and the models card's rows as they glide and fade, with the lines of rows still
/// fading out. The `+N` line is a row of its own, always last.
struct CharacterCardDynamics {
    static let fade = 0.25
    static let moreId = "\u{1}more"

    private(set) var latency: CharacterReadoutText.Latency?
    private(set) var latencyPresence = 0.0
    private(set) var modelRows = HudRows()
    private(set) var modelLines: [String: CharacterModelLine] = [:]
    /// The models the card had no room for at the last change (its `+N`).
    private(set) var moreCount = 0
    private var lastModels: [CharacterModelLine]?
    private var lastLimit = 0
    private var ids: [String] = []
    private var primed = false

    var isMoving: Bool { (latencyPresence > 0 && latencyPresence < 1) || modelRows.isMoving }

    /// Advances one frame. `small`: below S = 96 the card keeps its first two rows. `instant` reorders at once (Reduce
    /// Motion).
    mutating func update(_ dt: Double, character: OrbCharacterContent?, small: Bool, instant: Bool) {
        let readouts = character?.readouts ?? .default
        let value = readouts.latency ? character?.latency : nil
        if let value { latency = value }
        let target = value == nil ? 0.0 : 1.0
        latencyPresence = primed ? (target > latencyPresence ? min(target, latencyPresence + dt / Self.fade)
            : max(target, latencyPresence - dt / Self.fade)) : target
        if latencyPresence == 0 { latency = nil }
        primed = true

        let models = character?.models ?? []
        let limit = CharacterReadoutText.rowLimit(small: small)
        if models != lastModels || limit != lastLimit {
            lastModels = models
            lastLimit = limit
            for line in models.prefix(limit) { modelLines[line.id] = line }
            if models.count > limit { moreCount = models.count - limit }
            ids = models.prefix(limit).map(\.id)
            if models.count > limit { ids.append(Self.moreId) }
        }
        modelRows.update(dt, ids: ids, instant: instant)
        if modelLines.count > modelRows.rows.count + 1 {
            let kept = Set(modelRows.rows.map(\.id))
            modelLines = modelLines.filter { kept.contains($0.key) }
        }
    }

    /// The card has faded out: its next rows appear with it instead of fading in one by one.
    mutating func resetModels() {
        guard !modelRows.rows.isEmpty else { return }
        modelRows = HudRows()
        modelLines = [:]
        lastModels = nil
        ids = []
    }
}
