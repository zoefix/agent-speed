import AgentSpeedCore
import CoreGraphics

/// One cached piece of a board's content (DESIGN 2c Two layers): the hero, a ranking row, the footer. Laid out and
/// rasterized only when `revision` changes; frames only move the cached layer.
struct BoardContentItem {
    struct Plate: Equatable {
        var color: CGColor
        var cornerRadius: CGFloat
    }

    enum Motion: Equatable {
        /// Stays put (hero, footer).
        case none
        /// Springs to new places, fades in from 10 pt below and out when it leaves (rows).
        case row
        /// Glides to new places (smoothstep, 0.3 s), fades in and out in place over `fade` seconds (the sub-gauge's
        /// pieces as 思考 hides and returns, DESIGN 2c Live metrics). Reduce Motion: at once.
        case glide(fade: Double)
    }

    /// The layer a piece belongs to: the hero's pieces share one, so a switch change crossfades the hero as a whole.
    enum Group: Equatable {
        case main
        case hero
    }

    /// An arc revealed up to `fraction` of its sweep by a mask the layer animates (glass's dial, 0.24 s): the image
    /// holds the whole arc. Item points, y down; angles in radians, clockwise on screen.
    struct Sweep: Equatable {
        /// A dotted arc (the proof mark): dots every `pitch` points of arc from the start, `radius` each; the reveal
        /// stops after the last whole dot that keeps `clearance` points from the value's end (the tip ring sits there).
        struct Dots: Equatable {
            var pitch: CGFloat
            var radius: CGFloat
            var clearance: CGFloat
        }

        var center: CGPoint
        var radius: CGFloat
        /// The mask's width (the stroke and its glow).
        var width: CGFloat
        var start: CGFloat
        var sweep: CGFloat
        var fraction: Double
        var dots: Dots?

        /// The mask's centre line over the whole sweep, from a little before the start so the arc's round start cap
        /// and glow stay whole; its end (under the dial's tip) is flat.
        var maskLine: CGPath {
            let lead = width / (2 * max(1, radius))
            let path = CGMutablePath()
            path.addArc(center: center, radius: radius, startAngle: start - lead, endAngle: start + sweep, clockwise: false)
            return path
        }

        /// The angle revealed for `fraction`: its share of the sweep, or for a dotted arc the end of its last whole dot
        /// before the tip, so no dot is ever cut.
        func revealed(_ fraction: Double) -> CGFloat {
            let angle = sweep * CGFloat(min(1, max(0, fraction)))
            guard let dots, angle > 0, radius > 0, dots.pitch > 0 else { return angle }
            let last = ((angle * radius - dots.clearance - dots.radius) / dots.pitch).rounded(.down)
            guard last >= 0 else { return 0 }
            let gap = max(0, dots.pitch - 2 * dots.radius)
            return min(angle, (last * dots.pitch + dots.radius + min(0.5, gap / 2)) / radius)
        }

        /// The mask's share of `maskLine` that reveals `fraction` of the sweep (a stroke end).
        func strokeEnd(_ fraction: Double) -> CGFloat {
            let lead = width / (2 * max(1, radius))
            let shown = revealed(fraction)
            return shown > 0 ? (lead + shown) / (lead + sweep) : 0
        }
    }

    /// Identity across updates (a row's plan key, `hero`, `footer`).
    var key: String
    /// Board points, y down.
    var frame: CGRect
    var motion: Motion = .row
    var group = Group.main
    /// A flat rounded fill behind the drawing, composited by the render server (a row's plate is never rasterized,
    /// so a changing value redraws only its text).
    var plate: Plate?
    /// Whatever the drawing depends on; equal revisions reuse the cached image.
    var revision: AnyHashable
    /// Draws the item in a context of `size` points (y down, origin at the item's top-left).
    var draw: (CGContext, CGSize) -> Void
    /// One-shot animations played when the layout carrying them is applied (DESIGN 2c Motion and effects).
    var accents: [BoardContentAccent] = []
    /// The white-hot variant the `roll(rising: true)` and `warm` accents start from.
    var hot: BoardContentDrawing?
    /// A continuous motion while the board has something going on; nil is still.
    var ambient: BoardContentAmbient?
    /// Clips the drawing to the frame (digits and ranks rolling in their slots).
    var clips = false
    /// Pixels per point of the image when not the window's (soft lights are kept at a fraction of it).
    var rasterScale: CGFloat?
    /// Scales the image along x about `stretchAnchor` (a share underline, a heat rail drawn at full length) with a
    /// continuous transition instead of a redraw: 1 shows it as drawn.
    var stretch: Double?
    /// Where a stretched image is pinned, points from the frame's left edge (a bar's start, after the glow's room).
    var stretchAnchor: CGFloat = 0
    /// The drawing is the coverage of one color (drawn white): the layer fills `tint` through it, so a new color is a
    /// layer property with a short transition instead of a redraw (heat-colored bars and lights).
    var tint: CGColor?
    /// Shows only the first `fraction` of an arc (a mask the layer animates instead of a redraw).
    var sweep: Sweep?
    /// Turns the image clockwise by this angle (radians) about its frame's center, animated like the sweep (glass's
    /// dial tip riding the arc).
    var turn: Double?

    /// Where the image shows in board points once stretched.
    var shownFrame: CGRect {
        guard let stretch else { return frame }
        let scale = CGFloat(min(1, max(0, stretch)))
        return CGRect(x: frame.minX + stretchAnchor * (1 - scale), y: frame.minY, width: frame.width * scale, height: frame.height)
    }
}

extension BoardContentItem {
    /// The item as its layer shows it, for offscreen frames: tinted, revealed and turned.
    var composited: (CGContext, CGSize) -> Void {
        let draw = BoardRaster.tinted(self.draw, with: tint)
        let sweep = self.sweep, turn = self.turn
        guard sweep != nil || turn != nil else { return draw }
        return { context, size in
            context.saveGState()
            if let sweep {
                let end = sweep.strokeEnd(sweep.fraction)
                guard end > 0 else { return context.restoreGState() }
                let lead = sweep.width / (2 * max(1, sweep.radius))
                let line = CGMutablePath()
                line.addArc(center: sweep.center, radius: sweep.radius, startAngle: sweep.start - lead,
                            endAngle: sweep.start - lead + (lead + sweep.sweep) * end, clockwise: false)
                context.addPath(line.copy(strokingWithWidth: sweep.width, lineCap: .butt, lineJoin: .miter, miterLimit: 10))
                context.clip()
            }
            if let turn {
                context.translateBy(x: size.width / 2, y: size.height / 2)
                context.rotate(by: CGFloat(turn))
                context.translateBy(x: -size.width / 2, y: -size.height / 2)
            }
            draw(context, size)
            context.restoreGState()
        }
    }
}

/// A drawing in a context of `size` points (y down).
struct BoardContentDrawing {
    var draw: (CGContext, CGSize) -> Void
}

/// The content's one-shot motions, all run by the render server.
enum BoardContentAccent {
    /// The new image rolls in from below (rising) or above in 0.24 s while the old one rolls out; rising, it comes in
    /// white-hot (`hot`) and cools to its color in 0.8 s.
    case roll(rising: Bool)
    /// White-hot (`hot`), then its own color, 0.8 s.
    case warm
    /// Dims briefly and settles, 0.8 s.
    case dim
    /// Scales 1 → 1.06 → 1 in 0.42 s about `anchor` (board points).
    case punch(anchor: CGPoint)
    /// A decal shown beside the item for a while (chips, sweeps, banners).
    case decal(BoardDecal)
    /// The new image crossfades over the old in `seconds` (a readout's `–` ↔ number, a row's live ↔ resting color,
    /// the neon meter's segments).
    case crossfade(Double)
}

/// A temporary drawing over an item: an overtake chip, a light sweep, a banner, a sheen.
struct BoardDecal {
    enum Motion {
        /// Pops in (scale 0.6 → 1), holds, fades out (`seconds` in all).
        case pop(seconds: Double)
        /// A band crossing `frame` from left to right in `seconds`, clipped to it (or to `mask`).
        case sweep(seconds: Double)
        /// Wipes in from the left, holds, wipes out to the right (timing's new-leader banner).
        case banner(seconds: Double)
        /// Shows at once and fades out.
        case fade(seconds: Double)
    }

    /// Item points, y down; may reach outside the item.
    var frame: CGRect
    var motion: Motion
    /// Celebrations (new peak, new leader) are left out with `Celebrate new peaks` off.
    var celebration = false
    /// Clip of a sweep (decal points, y down), e.g. the digits' outlines for the new-peak sheen.
    var mask: CGPath?
    /// The decal (a sweep's band: drawn in a box as tall as `frame` and `bandWidth` wide).
    var bandWidth: CGFloat = 0
    var draw: (CGContext, CGSize) -> Void
}

/// A continuous motion of the content (DESIGN 2c Style ambience).
enum BoardContentAmbient: Equatable {
    /// Opacity between `low` and 1 and back over `period` seconds (neon's tube).
    case breathe(period: Double, low: Float)
    /// A slow drift by (`dx`, `dy`) points and `scale`, there and back over `period` seconds (glass's aurora).
    case drift(dx: CGFloat, dy: CGFloat, scale: CGFloat, period: Double)
    /// A ring that swells and fades from the item every `period` seconds (timing's live dot).
    case pulse(period: Double)
}

/// The effort and speed capsule of an agent (DESIGN 4 Agents) drawn into a row, so board rows and the dashboard match.
@MainActor
protocol BoardCapsuleDrawing: AnyObject {
    /// The capsule's size, nil when it shows nothing (`compact`: S boards, without its word).
    func capsuleSize(for capsule: EffortCapsule, compact: Bool) -> CGSize?
    /// Draws the capsule with its top-left at `origin` (y down).
    func drawCapsule(_ capsule: EffortCapsule, compact: Bool, dark: Bool, in context: CGContext, at origin: CGPoint)
    /// Draws the capsule stretched to `width` (the rows' shared column: meters start on one line, bolts end on
    /// another); nil draws it at its own width. `tint`: the table's colors for a configured value.
    func drawCapsule(_ capsule: EffortCapsule, compact: Bool, dark: Bool, highContrast: Bool, tint: EffortCapsuleTint?, width: CGFloat?,
                     in context: CGContext, at origin: CGPoint)
}

extension BoardCapsuleDrawing {
    func drawCapsule(_ capsule: EffortCapsule, compact: Bool, dark: Bool, in context: CGContext, at origin: CGPoint) {
        drawCapsule(capsule, compact: compact, dark: dark, highContrast: false, tint: nil, width: nil, in: context, at: origin)
    }
}
