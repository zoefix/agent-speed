import Foundation

/// Pendulum chains for hair and skirt (physics.py): anchors follow body parameters, wind pushes, outputs are angles.
/// Verlet in fixed 1/240 s substeps, a spring toward the hanging rest shape, damping, two passes of length
/// constraints; each output is tanh(segment angle relative to the previous segment / degrees).
public struct CharacterPhysics: Sendable {
    static let gravity = 1400.0
    static let substep = 1.0 / 240

    struct Chain: Sendable {
        let spec: RigDocument.Chain
        let inputs: [(slot: Int?, x: Double, y: Double)]
        let outputs: [(slot: Int?, segment: Int, degrees: Double)]
        var px: [Double]
        var py: [Double]
        var qx: [Double]
        var qy: [Double]
        var time = 0.0

        init(_ spec: RigDocument.Chain, slot: (String) -> Int?) {
            self.spec = spec
            inputs = spec.inputs.map { (slot($0.param), $0.x ?? 0, $0.y ?? 0) }
            outputs = spec.outputs.map { (slot($0.param), $0.segment, $0.degrees) }
            let n = spec.segments.count + 1
            px = [Double](repeating: 0, count: n)
            py = px
            qx = px
            qy = px
        }

        func anchor(_ values: [Double]) -> (Double, Double) {
            var sx = 0.0, sy = 0.0
            for i in inputs {
                let v = i.slot.map { values[$0] } ?? 0
                sx += v * i.x
            }
            for i in inputs {
                let v = i.slot.map { values[$0] } ?? 0
                sy += v * i.y
            }
            return (spec.anchor[0] + sx, spec.anchor[1] + sy)
        }

        mutating func reset(_ values: [Double]) {
            let (ax, ay) = anchor(values)
            px[0] = ax
            py[0] = ay
            for i in 1..<px.count {
                px[i] = px[i - 1]
                py[i] = py[i - 1] + spec.segments[i - 1]
            }
            qx = px
            qy = py
            time = 0
        }

        mutating func step(_ dt: Double, _ values: [Double], wind: Double, windSign: Double) {
            let steps = max(1, Int((dt / CharacterPhysics.substep).rounded(.toNearestOrEven)))
            let h = dt / Double(steps)
            let k = spec.stiffness
            let keep = max(0.0, 1 - spec.damping * h)
            for _ in 0..<steps {
                time += h
                let gust = 1 + 0.35 * sin(time * 7.3) + 0.2 * sin(time * 13.1 + 1.7)
                let forceX = windSign * wind * spec.wind * CharacterPhysics.gravity * gust
                let (ax, ay) = anchor(values)
                px[0] = ax
                py[0] = ay
                var rx = ax, ry = ay
                for i in 1..<px.count {
                    ry = ry + spec.segments[i - 1]
                    rx = rx + 0
                    let vx = (px[i] - qx[i]) * keep, vy = (py[i] - qy[i]) * keep
                    let accX = forceX + k * (rx - px[i])
                    let accY = CharacterPhysics.gravity + k * (ry - py[i])
                    qx[i] = px[i]
                    qy[i] = py[i]
                    px[i] = px[i] + vx + accX * h * h
                    py[i] = py[i] + vy + accY * h * h
                }
                for _ in 0..<2 {
                    for i in 1..<px.count {
                        let dx = px[i] - px[i - 1], dy = py[i] - py[i - 1]
                        let dist = max(hypot(dx, dy), 1e-6)
                        let r = spec.segments[i - 1] / dist
                        px[i] = px[i - 1] + dx * r
                        py[i] = py[i - 1] + dy * r
                    }
                }
            }
        }

        func write(into values: inout [Double]) {
            var parent = 0.0
            for o in outputs {
                let i = o.segment
                let angle = atan2(px[i + 1] - px[i], py[i + 1] - py[i]) * (180.0 / Double.pi)
                if let slot = o.slot { values[slot] = tanh((angle - parent) / o.degrees) }
                parent = angle
            }
        }
    }

    var chains: [Chain]
    let windSlot: Int?
    public let windSign: Double

    /// `slot` maps a parameter name to its index in the value array the chains read and write (nil: absent, reads 0).
    public init(_ specs: [RigDocument.Chain], windSign: Double = 1, slot: (String) -> Int?) {
        chains = specs.map { Chain($0, slot: slot) }
        windSlot = slot("Wind")
        self.windSign = windSign
    }

    public mutating func reset(_ values: [Double]) {
        for i in chains.indices { chains[i].reset(values) }
    }

    /// Steps every chain with the current values (read only).
    public mutating func step(_ dt: Double, _ values: [Double]) {
        let wind = windSlot.map { values[$0] } ?? 0
        for i in chains.indices { chains[i].step(dt, values, wind: wind, windSign: windSign) }
    }

    /// Writes the chains' output parameters.
    public func write(into values: inout [Double]) {
        for c in chains { c.write(into: &values) }
    }
}
