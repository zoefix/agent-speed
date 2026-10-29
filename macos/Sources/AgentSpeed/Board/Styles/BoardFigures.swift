import AgentSpeedOrbModel
import AppKit

/// A two-line group row's figures, the 性能 cell and the value (DESIGN 2c *The second line*): off line 1, on one
/// baseline that puts the cap-height centre of their digits on the row's centre line (both sizes' mean when the row
/// shows both, so each lies within half a point of it).
@MainActor
struct BoardFigures: Equatable {
    /// The row's centre line, points under its top.
    let centre: CGFloat
    let baseline: CGFloat

    init(centre: CGFloat, fonts: [BoardFont]) {
        self.centre = centre
        let caps = fonts.map { $0.font.capHeight }
        baseline = centre + caps.reduce(0, +) / CGFloat(max(1, caps.count)) / 2
    }

    /// The values METRICS 7 writes for a row at their widest: three digits, and the compact forms with a suffix.
    static let samples = ["888", "8.88k", "88.8k", "888k"]

    /// The widest of `samples` in `font`, whole points.
    static func widest(_ font: BoardFont, color: RGBColor) -> CGFloat {
        (samples.map { BoardLabel($0, font, color: color).width }.max() ?? 0).rounded(.up)
    }
}
