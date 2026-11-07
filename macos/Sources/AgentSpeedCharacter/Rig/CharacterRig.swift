import Foundation

/// A 2D affine transform: x' = a x + b y + tx, y' = c x + d y + ty.
public struct Affine2: Sendable, Equatable {
    public var a, b, c, d, tx, ty: Double

    public init(a: Double, b: Double, c: Double, d: Double, tx: Double, ty: Double) {
        self.a = a
        self.b = b
        self.c = c
        self.d = d
        self.tx = tx
        self.ty = ty
    }

    public static let identity = Affine2(a: 1, b: 0, c: 0, d: 1, tx: 0, ty: 0)

    /// self · other (other applied first).
    @inlinable public func then(_ o: Affine2) -> Affine2 {
        Affine2(a: a * o.a + b * o.c, b: a * o.b + b * o.d, c: c * o.a + d * o.c, d: c * o.b + d * o.d,
                tx: a * o.tx + b * o.ty + tx, ty: c * o.tx + d * o.ty + ty)
    }

    @inlinable public func apply(_ x: Double, _ y: Double) -> (Double, Double) {
        (a * x + b * y + tx, c * x + d * y + ty)
    }
}

/// A compiled rig (spec/CHARACTERS.md, Rig): parameters by index, bones parents-first, drivers per bone and channel,
/// parts in draw order with their skinning data.
public struct CharacterRig: Sendable {
    public enum Channel: Int, Sendable {
        case rotate, x, y, scaleX, scaleY
    }

    public struct Driver: Sendable {
        public let param: Int
        public let channel: Channel
        public let xs: [Double]
        public let ys: [Double]
    }

    public struct Part: Sendable {
        public let name: String
        public let tag: String
        public let order: Double
        /// Rest positions, x y pairs in canvas pixels.
        public let vertices: [Double]
        /// Atlas pixel coordinates, x y pairs.
        public let uv: [Double]
        public let triangles: [UInt32]
        public let influences: Int
        /// `influences` bone indices and weights per vertex; weights sum to 1.
        public let bones: [UInt16]
        public let weights: [Double]
        /// Opacity drivers: parameter index and keys.
        public let opacity: [(param: Int, xs: [Double], ys: [Double])]

        public var vertexCount: Int { vertices.count / 2 }
    }

    public let name: String
    public let canvas: (width: Double, height: Double)
    public let atlasFile: String
    public let atlasSize: (width: Int, height: Int)
    public let params: [CharacterParam]
    public let paramIndex: [String: Int]
    public let boneNames: [String]
    public let parents: [Int]
    public let pivots: [(Double, Double)]
    public let drivers: [[Driver]]
    public let parts: [Part]
    public let physics: [RigDocument.Chain]
    public let landmarks: RigDocument.Landmarks

    public init(_ doc: RigDocument) throws {
        guard doc.format == RigDocument.format, doc.version == RigDocument.version else {
            throw CharacterRigError.unsupported(format: doc.format, version: doc.version)
        }
        name = doc.name
        canvas = (doc.canvas.first ?? 0, doc.canvas.count > 1 ? doc.canvas[1] : 0)
        atlasFile = doc.atlas.file
        atlasSize = (doc.atlas.size.first ?? 0, doc.atlas.size.count > 1 ? doc.atlas.size[1] : 0)
        var params: [CharacterParam] = []
        var paramIndex: [String: Int] = [:]
        for p in doc.params where paramIndex[p.id] == nil {
            paramIndex[p.id] = params.count
            params.append(p)
        }
        self.params = params
        self.paramIndex = paramIndex

        var boneIndex: [String: Int] = [:]
        var parents: [Int] = []
        var pivots: [(Double, Double)] = []
        for (i, b) in doc.bones.enumerated() {
            guard b.pivot.count == 2, boneIndex[b.name] == nil else { throw CharacterRigError.badBone(b.name) }
            var parent = -1
            if let name = b.parent {
                guard let p = boneIndex[name] else { throw CharacterRigError.parentsNotFirst(b.name) }
                parent = p
            }
            boneIndex[b.name] = i
            parents.append(parent)
            pivots.append((b.pivot[0], b.pivot[1]))
        }
        boneNames = doc.bones.map(\.name)
        self.parents = parents
        self.pivots = pivots

        var drivers = [[Driver]](repeating: [], count: doc.bones.count)
        for d in doc.drivers {
            guard let bone = boneIndex[d.bone], let param = paramIndex[d.param],
                  let channel = CharacterRig.channel(d.channel), !d.keys.isEmpty, d.keys.allSatisfy({ $0.count == 2 })
            else { throw CharacterRigError.badDriver("\(d.param) → \(d.bone).\(d.channel)") }
            drivers[bone].append(Driver(param: param, channel: channel, xs: d.keys.map { $0[0] }, ys: d.keys.map { $0[1] }))
        }
        self.drivers = drivers

        var opacity: [String: [(param: Int, xs: [Double], ys: [Double])]] = [:]
        for d in doc.partDrivers {
            guard let param = paramIndex[d.param], !d.keys.isEmpty else { continue }
            opacity[d.part, default: []].append((param, d.keys.map { $0[0] }, d.keys.map { $0[1] }))
        }
        let ordered = doc.parts.enumerated().sorted { $0.element.order != $1.element.order
            ? $0.element.order < $1.element.order : $0.offset < $1.offset }
        var parts: [Part] = []
        for (_, p) in ordered {
            let n = p.vertices.count / 2
            guard p.vertices.count % 2 == 0, p.uv.count == p.vertices.count, p.triangles.count % 3 == 0,
                  p.influences > 0, p.bones.count == n * p.influences, p.weights.count == n * p.influences,
                  p.triangles.allSatisfy({ $0 >= 0 && $0 < n }), p.bones.allSatisfy({ $0 >= 0 && $0 < doc.bones.count })
            else { throw CharacterRigError.badPart(p.name) }
            parts.append(Part(name: p.name, tag: p.tag, order: p.order, vertices: p.vertices, uv: p.uv,
                              triangles: p.triangles.map { UInt32($0) }, influences: p.influences,
                              bones: p.bones.map { UInt16($0) }, weights: p.weights,
                              opacity: opacity[p.name] ?? []))
        }
        self.parts = parts
        for chain in doc.physics {
            guard chain.anchor.count == 2, !chain.segments.isEmpty,
                  chain.outputs.allSatisfy({ $0.segment >= 0 && $0.segment < chain.segments.count && $0.degrees != 0 })
            else { throw CharacterRigError.badChain(chain.name) }
        }
        physics = doc.physics
        landmarks = doc.landmarks
    }

    static func channel(_ name: String) -> Channel? {
        switch name {
        case "rotate": .rotate
        case "x": .x
        case "y": .y
        case "scaleX": .scaleX
        case "scaleY": .scaleY
        default: nil
        }
    }

    public var boneCount: Int { parents.count }

    /// Defaults of every parameter, in parameter order.
    public func defaults() -> [Double] { params.map(\.default) }

    @inline(__always) func clamped(_ values: [Double], _ i: Int) -> Double {
        let p = params[i]
        return i < values.count ? min(max(values[i], p.min), p.max) : p.default
    }

    /// World transform of every bone for parameter values in parameter order (clamped to their ranges).
    public func worldTransforms(_ values: [Double], into world: inout [Affine2]) {
        if world.count != boneCount { world = [Affine2](repeating: .identity, count: boneCount) }
        for i in 0..<boneCount {
            var rotate = 0.0, x = 0.0, y = 0.0, scaleX = 0.0, scaleY = 0.0
            for d in drivers[i] {
                let v = CharacterMath.interpolate(d.xs, d.ys, clamped(values, d.param))
                switch d.channel {
                case .rotate: rotate += v
                case .x: x += v
                case .y: y += v
                case .scaleX: scaleX += v
                case .scaleY: scaleY += v
                }
            }
            let (px, py) = pivots[i]
            let r = rotate * (Double.pi / 180)
            let c = cos(r), s = sin(r)
            let sx = 1 + scaleX, sy = 1 + scaleY
            let a = c * sx, b = -s * sy, cc = s * sx, d = c * sy
            let local = Affine2(a: a, b: b, c: cc, d: d, tx: px - (a * px + b * py) + x, ty: py - (cc * px + d * py) + y)
            world[i] = parents[i] < 0 ? local : world[parents[i]].then(local)
        }
    }

    /// Opacity of every part (the product of its drivers, 1 without any), in draw order.
    public func opacities(_ values: [Double], into out: inout [Double]) {
        if out.count != parts.count { out = [Double](repeating: 1, count: parts.count) }
        for (i, part) in parts.enumerated() {
            var o = 1.0
            for d in part.opacity {
                o *= CharacterMath.interpolate(d.xs, d.ys, clamped(values, d.param))
            }
            out[i] = o
        }
    }

    /// Skinned positions of a part's vertices (x y pairs), as the GPU computes them.
    public func skin(part index: Int, world: [Affine2], into out: inout [Double]) {
        let part = parts[index]
        let n = part.vertexCount, k = part.influences
        if out.count != n * 2 { out = [Double](repeating: 0, count: n * 2) }
        for v in 0..<n {
            let x = part.vertices[2 * v], y = part.vertices[2 * v + 1]
            var sx = 0.0, sy = 0.0
            for j in 0..<k {
                let w = part.weights[v * k + j]
                if w == 0 { continue }
                let m = world[Int(part.bones[v * k + j])]
                let (px, py) = m.apply(x, y)
                sx += w * px
                sy += w * py
            }
            out[2 * v] = sx
            out[2 * v + 1] = sy
        }
    }
}
