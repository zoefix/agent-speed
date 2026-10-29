import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// The hero's 性能 number (DESIGN 2c Live metrics; DESIGN 12 *Number formats*, *Provisional mark*): one cached image in
/// a box as wide as the widest text of its form (`8,888`, or `99,999` in the full form), so neither a count nor a new
/// digit count moves another piece, and the dotted underline of a provisional score as a piece of its own, so it fades
/// in and out with the state (at once with Reduce Motion).
@MainActor
struct BoardScoreNumber {
    enum Anchor {
        case center(CGFloat)
        case right(CGFloat)
    }

    let key: String
    let label: BoardLabel
    let anchor: Anchor
    let baseline: CGFloat
    let provisional: Bool
    let highContrast: Bool
    /// The widest text the box holds.
    let widest: String

    init(key: String, shown: Int?, form: PerfNumberForm, font: BoardFont, color: RGBColor, anchor: Anchor,
         baseline: CGFloat, provisional: Bool, highContrast: Bool) {
        self.key = key
        label = BoardLabel(shown.map(form.text) ?? "–", font, color: color)
        self.anchor = anchor
        self.baseline = baseline
        self.provisional = provisional && shown != nil
        self.highContrast = highContrast
        widest = form == .compact ? PerfFormat.measureText : "99,999"
    }

    /// The text's left edge, board points.
    var x: CGFloat {
        switch anchor {
        case .center(let center): center - label.width / 2
        case .right(let right): right - label.width
        }
    }

    /// The box the number can paint whatever it shows, board points.
    var box: CGRect {
        let widest = max(label.width, BoardLabel(self.widest, label.style, color: label.color).width)
        let left: CGFloat = switch anchor {
        case .center(let center): center - widest / 2
        case .right(let right): right - widest
        }
        return CGRect(x: left, y: baseline - label.font.ascender, width: widest,
                      height: label.font.ascender - label.font.descender).insetBy(dx: -2, dy: -2).integral
    }

    /// The number and, while provisional, its underline. `fade`: the number's image crossfades (`–` ↔ a number,
    /// live ↔ resting, any change under Reduce Motion).
    func items(fade: Double?) -> [BoardContentItem] {
        let label = self.label, frame = box
        let glyphX = x - frame.minX, glyphBaseline = baseline - frame.minY
        let color = label.color
        var number = BoardContentItem(key: key, frame: frame, motion: .glide(fade: BoardRollingText.crossfade),
                                      revision: AnyHashable([AnyHashable(label.text), AnyHashable(label.style), AnyHashable(glyphX),
                                                             AnyHashable(glyphBaseline),
                                                             AnyHashable([color.red, color.green, color.blue, color.alpha])])) { context, _ in
            label.draw(in: context, x: glyphX, baseline: glyphBaseline)
        }
        number.group = .hero
        if let fade { number.accents.append(.crossfade(fade)) }
        guard provisional else { return [number] }
        return [number, mark()]
    }

    /// The dotted underline: dots `0.22 ×` the size under the baseline across the digits' advance, in the number's color
    /// at 70 % (100 % with Increase Contrast).
    private func mark() -> BoardContentItem {
        let label = self.label, highContrast = self.highContrast
        let size = label.font.pointSize
        let left = x, width = label.width, baseline = self.baseline
        let reach = ProvisionalMark.dotDiameter(textSize: size, scale: 1, increasedContrast: highContrast) + 1
        let center = baseline + ProvisionalMark.textDepth * size
        let rect = CGRect(x: left - 1, y: center - reach, width: width + 2, height: 2 * reach).integral
        let color = label.color
        var item = BoardContentItem(key: key + ".mark", frame: rect, motion: .glide(fade: ProvisionalMark.fadeSeconds),
                                    revision: AnyHashable([AnyHashable(width), AnyHashable(size), AnyHashable(left - rect.minX),
                                                           AnyHashable(center - rect.minY), AnyHashable(highContrast),
                                                           AnyHashable([color.red, color.green, color.blue, color.alpha])])) { context, _ in
            BoardScoreNumber.drawUnderline(in: context, x: left - rect.minX, baseline: baseline - rect.minY, width: width,
                                           textSize: size, color: color, highContrast: highContrast)
        }
        item.group = .hero
        return item
    }

    /// Draws the provisional mark under a number in a board's y-down context (`x`: its left edge, on its baseline).
    static func drawUnderline(in context: CGContext, x: CGFloat, baseline: CGFloat, width: CGFloat, textSize: CGFloat,
                              color: RGBColor, highContrast: Bool) {
        let ctm = context.ctm
        let scale = max(1, (ctm.a * ctm.a + ctm.b * ctm.b).squareRoot())
        ProvisionalMark.drawUnderline(in: context, origin: CGPoint(x: x, y: baseline), width: width, textSize: textSize,
                                color: color.cgColor, scale: scale, increasedContrast: highContrast)
    }
}
