import Foundation

/// Where the readout cards are anchored on her this frame, in window points (y down).
public struct HudAnchors: Sendable, Equatable {
    /// Her center line, the top of her head, her chest (the speed's row) and her shoulders (the column's top).
    public var centerX: Double
    public var headTop: Double
    public var chest: Double
    public var shoulders: Double

    public init(centerX: Double, headTop: Double, chest: Double, shoulders: Double) {
        self.centerX = centerX
        self.headTop = headTop
        self.chest = chest
        self.shoulders = shoulders
    }
}

extension CharacterPerformer {
    /// Parts whose top is the top of her head (the model chip clears it).
    static let headTags: Set<String> = ["face", "front hair", "back hair", "headwear", "ears"]

    /// Traces her drawn outline this frame into `silhouette` (cleared first): every vertex of each visible part of the
    /// active pose, skinned as the GPU skins it, and every triangle edge long enough to skip a band, so a thin staff
    /// or a raised arm marks each band it crosses. Returns the anchors of the cards. `scratch` holds skinned positions
    /// and only grows, so a warm trace never allocates.
    public func trace(into silhouette: inout HudSilhouette, feetOnScreen: (Double, Double), pointsPerPixel: Double,
                      scratch: inout [Double]) -> HudAnchors {
        silhouette.clear()
        let rig = activeRig
        let placement = activePlacement
        let k = pointsPerPixel * placement.scale
        let ox = feetOnScreen.0 - placement.feet.x * k, oy = feetOnScreen.1 - placement.feet.y * k
        var headTop = Double.infinity
        let step = silhouette.step
        for index in rig.parts.indices where index >= opacities.count || opacities[index] > 0.05 {
            let part = rig.parts[index]
            let n = part.vertexCount, m = part.influences
            if scratch.count < 2 * n { scratch.append(contentsOf: repeatElement(0, count: 2 * n - scratch.count)) }
            let head = Self.headTags.contains(part.tag)
            for v in 0..<n {
                let x = part.vertices[2 * v], y = part.vertices[2 * v + 1]
                var sx = 0.0, sy = 0.0
                for j in 0..<m {
                    let w = part.weights[v * m + j]
                    if w == 0 { continue }
                    let t = world[Int(part.bones[v * m + j])]
                    sx += w * (t.a * x + t.b * y + t.tx)
                    sy += w * (t.c * x + t.d * y + t.ty)
                }
                let px = ox + sx * k, py = oy + sy * k
                scratch[2 * v] = px
                scratch[2 * v + 1] = py
                silhouette.add(px, py)
                if head, py < headTop { headTop = py }
            }
            var t = 0
            let triangles = part.triangles
            while t + 2 < triangles.count {
                let a = Int(triangles[t]), b = Int(triangles[t + 1]), c = Int(triangles[t + 2])
                edge(&silhouette, scratch, a, b, step)
                edge(&silhouette, scratch, b, c, step)
                edge(&silhouette, scratch, c, a, step)
                t += 3
            }
        }
        var anchors = restAnchors(feetOnScreen: feetOnScreen, pointsPerPixel: pointsPerPixel)
        if headTop.isFinite { anchors.headTop = headTop }
        return anchors
    }

    @inline(__always)
    private func edge(_ s: inout HudSilhouette, _ p: [Double], _ a: Int, _ b: Int, _ step: Double) {
        let y0 = p[2 * a + 1], y1 = p[2 * b + 1]
        guard abs(y1 - y0) > step else { return }
        s.add(p[2 * a], y0, p[2 * b], y1)
    }

    /// The anchors of the active pose at rest (face, chin and waist landmarks), moved with her side steps; without
    /// landmarks, shares of her height.
    public func restAnchors(feetOnScreen: (Double, Double), pointsPerPixel: Double) -> HudAnchors {
        let placement = activePlacement
        let lm = activeRig.landmarks
        let dx = bodyOffset.dx * pointsPerPixel
        let h = idleHeight * pointsPerPixel
        func screen(_ x: Double, _ y: Double) -> (Double, Double) {
            placement.screen(x, y, feetOnScreen: feetOnScreen, pointsPerPixel: pointsPerPixel)
        }
        guard let face = lm.face, face.count == 4 else {
            let fy = feetOnScreen.1
            return HudAnchors(centerX: feetOnScreen.0 + dx, headTop: fy - h, chest: fy - 0.68 * h, shoulders: fy - 0.78 * h)
        }
        let chin = lm.chin ?? face[3]
        let waist = lm.waist.flatMap { $0.count == 2 ? $0[1] : nil } ?? chin + 0.32 * idleHeight / placement.scale
        let center = screen((face[0] + face[2]) / 2, face[1])
        let shoulders = screen(0, chin + 0.25 * (waist - chin)).1
        let chest = screen(0, chin + 0.45 * (waist - chin)).1
        return HudAnchors(centerX: center.0 + dx, headTop: center.1, chest: chest, shoulders: shoulders)
    }
}
