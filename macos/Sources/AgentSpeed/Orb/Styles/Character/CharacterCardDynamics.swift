import AgentSpeedCharacter
import Foundation

/// What the cards carry from frame to frame (DESIGN 2a.6, Readouts), kept per orb by the renderer: the 首字 row's fade
/// with the last value it showed, the models card's rows as they glide and fade, with the lines of rows still
/// fading out (the `+N` line is a row of its own, always last), and the 額度 card's sources, bars and form.
struct CharacterCardDynamics {
    static let fade = 0.25
    static let moreId = "\u{1}more"

    private(set) var latency: CharacterReadoutText.Latency?
    private(set) var latencyPresence = 0.0
    private(set) var modelRows = HudRows()
    /// The names' width on the models card; nil keeps only the dots and the speeds.
    private(set) var names = HudNameWidth()
    private(set) var modelLines: [String: CharacterModelLine] = [:]
    /// The models the card had no room for at the last change (its `+N`).
    private(set) var moreCount = 0
    private var lastModels: [CharacterModelLine]?
    private var lastLimit = 0
    private var ids: [String] = []
    private var primed = false
    /// The 額度 card's sources and lines as they ease, and the form its room allows.
    private(set) var quota = CharacterQuotaDynamics()
    private(set) var quotaFit = HudFormFit()

    var isMoving: Bool { (latencyPresence > 0 && latencyPresence < 1) || modelRows.isMoving || quota.isMoving }

    /// Picks the 額度 card's form for `room` from its forms' `widths` (widest first). `snap` while it is off screen.
    mutating func fitQuota(_ dt: Double, widths: [Double], room: Double, spare: Double, snap: Bool) {
        quotaFit.update(dt, widths: widths, room: room, spare: spare, snap: snap)
    }

    /// The 額度 card has faded out: its next sources appear with it.
    mutating func resetQuota() {
        guard !quota.sources.isEmpty else { return }
        quota.reset()
    }

    var nameWidth: CGFloat? { names.width.map { CGFloat($0) } }

    /// Fits the names to `room` (the wings' room to the orb's edge at the card's place) from the card's measures.
    /// `snap` while the card is off screen.
    mutating func fitNames(_ dt: Double, room: Double, measure: CharacterCards.ModelsMeasure, t: CGFloat, spare: Double,
                           snap: Bool) {
        names.update(dt, room: room, rest: Double(measure.rest), natural: Double(measure.natural), t: Double(t),
                     spare: spare, snap: snap)
    }

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
        quota.update(dt, quota: readouts.quota ? character?.quota ?? [] : [], small: small, instant: instant)
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
