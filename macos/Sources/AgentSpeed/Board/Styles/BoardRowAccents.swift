import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// What a row plays on a fresh tick (DESIGN 2c Rows, Overtake): its value's flash or dim, its rank's roll and the
/// pass that moved it.
struct BoardRowAccents {
    var change = BoardValueChange.steady
    var previousRank: Int?
    var event: BoardRankEvent?
    /// The 性能 cell's crossfade (a fall, `–` ↔ a number, live ↔ resting, the provisional mark; DESIGN 2c *Updates*).
    var intelFade: Double?

    static let none = BoardRowAccents()

    init(change: BoardValueChange = .steady, previousRank: Int? = nil, event: BoardRankEvent? = nil) {
        self.change = change
        self.previousRank = previousRank
        self.event = event
    }

    /// The accents of `row` given what the board showed before.
    init(_ row: BoardPlannedRow, live: Double, tick: BoardTick, memory: BoardTickMemory, model: BoardRowModel? = nil,
         reduceMotion: Bool = false) {
        change = memory.change(row.key, live: live)
        if let score = model?.intel, let old = memory.previousScore(row.key) {
            intelFade = BoardScoreMotion.row(from: old, to: score, reduceMotion: reduceMotion)
        }
        if case .group(let index) = row.kind {
            let id = tick.groups[index].id
            previousRank = memory.rankChange(id, rank: index + 1)
            event = memory.event(tick, for: id)
        }
    }
}

extension BoardRowModel {
    /// The row's body without its value, rank, share and interval (they are items of their own), so their changes leave
    /// it cached.
    var body: BoardRowModel {
        var body = self
        body.value = ""
        body.idle = false
        body.share = 0
        body.heat = 0
        body.interval = ""
        body.intel = intel == nil ? nil : BoardRowScore.none
        if let rank { body.kind = .group(rank: rank == 1 ? 1 : 0) }
        return body
    }
}

/// The overtake decals of a row (DESIGN 2c Overtake).
@MainActor
enum BoardRowDecals {
    /// A light sweep across the row in the climber's source color (gold and white for a new leader), 0.75 s.
    static func sweep(width: CGFloat, height: CGFloat, color: RGBColor, leader: Bool, radius: CGFloat) -> BoardDecal {
        let band = max(80, width * 0.7)
        return BoardDecal(frame: CGRect(x: 0, y: 0, width: width, height: height), motion: .sweep(seconds: 0.75),
                          mask: CGPath(roundedRect: CGRect(x: 0, y: 0, width: width, height: height), cornerWidth: radius,
                                       cornerHeight: radius, transform: nil),
                          bandWidth: band) { context, size in
            let tint = leader ? RGBColor(red: 1, green: 226 / 255, blue: 120 / 255, alpha: 0.55) : color.opacity(0.4)
            BoardDrawing.gradient(CGRect(origin: .zero, size: size),
                                  stops: [(tint.opacity(0), 0), (tint, 0.42), (RGBColor.white.opacity(leader ? 0.7 : 0.5), 0.52),
                                          (tint.opacity(0), 0.7)],
                                  from: CGPoint(x: 0, y: size.height * 0.6), to: CGPoint(x: size.width, y: size.height * 0.4), in: context)
        }
    }

    /// `▲n` before the value for 1.6 s: glass a mint chip, neon a lit mint chip, timing a green delta. `rowRight` is the
    /// row's right edge in the value item's points.
    static func chip(style: BoardStyle, climbed: Int, rowRight: CGFloat, midY: CGFloat, palette: BoardPalette) -> BoardDecal {
        let text = "▲\(climbed)"
        let font: BoardFont
        let ink: RGBColor
        let fill: RGBColor
        let radius: CGFloat
        let right: CGFloat
        let height: CGFloat
        switch style {
        case .glass:
            font = BoardFont(size: 10, weight: .semibold, tabular: true)
            ink = palette.dark ? BoardPalette.mint : RGBColor(hex: 0x0F7A55)
            fill = BoardPalette.mint.opacity(palette.highContrast ? 0.3 : 0.16)
            radius = 6
            right = 52
            height = 16
        case .neon:
            font = BoardFont(family: .mono, size: 10, weight: .bold)
            ink = RGBColor(hex: 0x021A10)
            fill = BoardPalette.mint
            radius = 4
            right = 52
            height = 16
        case .timing:
            font = BoardFont(family: .mono, size: 10, weight: .heavy)
            ink = RGBColor(hex: 0x04130A)
            fill = BoardPalette.green
            radius = 4
            right = 92
            height = 16
        }
        let label = BoardLabel(text, font, color: ink)
        let width = label.width + 10
        let glow = style == .neon && palette.glows
        let margin: CGFloat = glow ? 12 : 0
        let frame = CGRect(x: rowRight - right - width - margin, y: midY - height / 2 - margin, width: width + 2 * margin,
                           height: height + 2 * margin)
        return BoardDecal(frame: frame, motion: .pop(seconds: 1.6)) { context, _ in
            let chip = CGRect(x: margin, y: margin, width: width, height: height)
            // Glass's tint sits on an opaque base so the capsule under it does not show through.
            if style == .glass { BoardDrawing.fill(chip, radius: radius, color: palette.dark ? RGBColor(hex: 0x14161D) : .white, in: context) }
            BoardDrawing.glowing(glow ? BoardPalette.mint.opacity(0.8) : nil, blur: 12, in: context) {
                BoardDrawing.fill(chip, radius: radius, color: fill, in: $0)
            }
            label.draw(in: context, x: chip.minX + 5, baseline: label.baseline(centeredOn: chip.midY))
        }
    }
}
