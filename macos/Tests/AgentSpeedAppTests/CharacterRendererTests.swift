@testable import AgentSpeed
import AgentSpeedCharacter
import Foundation
import Metal
import Testing

/// The pure parts of the `character` renderer: the GPU vertex layout and packing, atlas resolution steps, the
/// speed level, and the scene's depth layers.
@Suite struct CharacterRendererTests {
    static let fixture = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("spec/fixtures/characters/rig.json")

    static func tinyRig() throws -> CharacterRig {
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: fixture)) as! [String: Any]
        let data = try JSONSerialization.data(withJSONObject: json["rig"]!)
        return try CharacterRig(RigDocument.decode(data))
    }

    @Test func vertexLayoutMatchesTheShader() {
        #expect(MemoryLayout<CharacterVertex>.stride == 48)
        #expect(MemoryLayout<CharacterVertex>.offset(of: \.uv) == 8)
        #expect(MemoryLayout<CharacterVertex>.offset(of: \.weights) == 16)
        #expect(MemoryLayout<CharacterVertex>.offset(of: \.bones) == 32)
        #expect(MemoryLayout<CharacterVertex>.offset(of: \.part) == 40)
        #expect(MemoryLayout<CharacterDot>.stride == 48)
        #expect(MemoryLayout<CharacterSprite>.stride == 48)
    }

    @Test func packingKeepsDrawOrderAndOffsetsTriangles() throws {
        let rig = try Self.tinyRig()
        let (vertices, indices) = CharacterMeshPacking.pack(rig)
        #expect(vertices.count == rig.parts.reduce(0) { $0 + $1.vertexCount })
        #expect(indices.count == rig.parts.reduce(0) { $0 + $1.triangles.count })
        let first = rig.parts[0].vertexCount
        #expect(vertices[first].part == 1)
        #expect(indices[rig.parts[0].triangles.count] == UInt32(first) + rig.parts[1].triangles[0])
        for v in vertices {
            #expect(abs(v.weights.sum() - 1) < 1e-4)
            #expect(v.uv.x >= 0 && v.uv.x <= 1 && v.uv.y >= 0 && v.uv.y <= 1)
        }
    }

    @Test func atlasStepsRoundUpToSharperTextures() {
        #expect(CharacterAtlasScale.factor(step: CharacterAtlasScale.step(for: 1)) == 1)
        #expect(CharacterAtlasScale.factor(step: CharacterAtlasScale.step(for: 3)) == 1)
        for scale in [0.07, 0.2, 0.33, 0.5, 0.71, 0.9] {
            let f = CharacterAtlasScale.factor(step: CharacterAtlasScale.step(for: scale))
            #expect(f >= scale && f < scale * 1.42)
        }
        #expect(CharacterAtlasScale.factor(step: CharacterAtlasScale.step(for: 0.001)) == 1.0 / 16)
        #expect(CharacterAtlasScale.step(for: 0.4) == CharacterAtlasScale.step(for: 0.42))
    }

    @Test func speedTitlesAreTheDirectorsLevels() {
        #expect(SpeedTitle.idle.rawValue == 0)
        #expect(SpeedTitle.rocket.rawValue == 13)
        #expect(SpeedBand(level: SpeedTitle.walk.rawValue) == .cruise)
        #expect(SpeedBand(level: SpeedTitle.car.rawValue) == .boost)
        #expect(SpeedBand(level: SpeedTitle.maglev.rawValue) == .overdrive)
        #expect(SpeedBand(level: SpeedTitle.hypersonic.rawValue) == .redline)
    }

    @Test func sceneLayersKeepEveryOtherStyleOnLayerZero() {
        var scene = OrbScene(capacity: 16)
        scene.add(.circle(center: .zero, radius: 1, color: RGBColor(red: 1, green: 1, blue: 1)), blend: .over)
        scene.add(.circle(center: .zero, radius: 2, color: RGBColor(red: 1, green: 1, blue: 1)), blend: .over)
        #expect(scene.maxLayer == 0)
        #expect(scene.batches.count == 1)
        scene.layer = 2
        scene.add(.circle(center: .zero, radius: 3, color: RGBColor(red: 1, green: 1, blue: 1)), blend: .over)
        #expect(scene.batches.count == 2)
        #expect(scene.batches[1].layer == 2)
        #expect(scene.maxLayer == 2)
        scene.addCharacterDot(CharacterDot(x: 0, y: 0, rx: 1, ry: 1, color: .one, softness: 1), blend: .additive)
        scene.addCharacterDot(CharacterDot(x: 0, y: 0, rx: 1, ry: 1, color: .one, softness: 1), blend: .additive)
        #expect(scene.batches.count == 3)
        #expect(scene.batches[2].count == 2)
        scene.reset()
        #expect(scene.maxLayer == 0 && scene.layer == 0 && scene.characterDots.isEmpty)
    }
}

@Suite("Character frame class")
struct CharacterFrameClassTests {
    @Test func calmBandsDrawFewFramesAndActingDrawsSixty() {
        #expect(CharacterRenderer.frameClass(band: .idle, acting: false, unavailable: false) == .fps15)
        #expect(CharacterRenderer.frameClass(band: .cruise, acting: false, unavailable: false) == .fps30)
        #expect(CharacterRenderer.frameClass(band: .boost, acting: false, unavailable: false) == .fps60)
        #expect(CharacterRenderer.frameClass(band: .redline, acting: false, unavailable: false) == .fps60)
        #expect(CharacterRenderer.frameClass(band: .idle, acting: true, unavailable: false) == .fps60)
        #expect(CharacterRenderer.frameClass(band: nil, acting: false, unavailable: true) == .fps15)
    }
}

@Suite("Character lightning")
struct CharacterLightningTests {
    private func digits(_ text: String) -> [Odometer.Digit] {
        text.map { Odometer.Digit(current: $0, previous: nil, t: 1) }
    }

    @Test func strikesOnlyWhenDigitsChange() {
        var lightning = CharacterLightning()
        lightning.update(1.0 / 60, digits: digits("39"), heat: 0.5, strength: .standard)
        #expect(!lightning.isActive)
        lightning.update(1.0 / 60, digits: digits("39"), heat: 0.5, strength: .standard)
        #expect(!lightning.isActive)
        lightning.update(1.0 / 60, digits: digits("38"), heat: 0.5, strength: .standard)
        #expect(lightning.strikes.count == 1)
        #expect(lightning.strikes[0].low == 0 && lightning.strikes[0].high == 0 && !lightning.strikes[0].bridge)
        lightning.update(1.0 / 60, digits: digits("102"), heat: 0.5, strength: .standard)
        let wide = lightning.strikes[1]
        #expect(wide.low == 0 && wide.high == 2 && wide.bridge)
        #expect(lightning.flash(fromRight: 2) > 0)
    }

    @Test func strikesFadeOutAndStopTheBusyFrames() {
        var lightning = CharacterLightning()
        lightning.update(1.0 / 60, digits: digits("5"), heat: 1, strength: .max)
        lightning.update(1.0 / 60, digits: digits("6"), heat: 1, strength: .max)
        #expect(lightning.isActive)
        for _ in 0..<30 { lightning.update(1.0 / 60, digits: digits("6"), heat: 1, strength: .max) }
        #expect(!lightning.isActive)
        #expect(lightning.flash(fromRight: 0) == 0)
    }

    @Test func strengthSetsTheBolts() {
        var lightning = CharacterLightning()
        lightning.update(1.0 / 60, digits: digits("99"), heat: 1, strength: .standard)
        lightning.update(1.0 / 60, digits: digits("100"), heat: 1, strength: .standard)
        let strike = lightning.strikes[0]
        #expect(lightning.boltCount(strike, strength: .flashOnly) == 0)
        #expect(lightning.boltCount(strike, strength: .off) == 0)
        #expect(lightning.boltCount(strike, strength: .standard) == 4)
        #expect(lightning.boltCount(strike, strength: .max) == 5)
        var count = 0
        while lightning.bolt(strike, index: count, strength: .standard, digitCount: 3, advance: 10, cap: 14) {
            #expect(lightning.path.count >= 9)
            #expect(lightning.path.allSatisfy { abs($0.x) < 30 && abs($0.y) < 40 })
            count += 1
        }
        #expect(count == 4)
    }

    @Test func reduceMotionFlashesWithoutBolts() {
        let reduced = OrbEffects(level: .standard, reduceMotion: true, surface: .floating)
        #expect(CharacterRenderer.lightningStrength(reduced, small: false) == .flashOnly)
        let off = OrbEffects(level: .off, reduceMotion: false, surface: .floating)
        #expect(CharacterRenderer.lightningStrength(off, small: false) == .off)
    }

    @Test @MainActor func outlinedTextUsesTheOutlineMode() throws {
        guard let device = MTLCreateSystemDefaultDevice(),
              let glyphs = device.makeTexture(descriptor: .texture2DDescriptor(pixelFormat: .rgba8Unorm, width: 1, height: 1,
                                                                               mipmapped: false)) else { return }
        let texture = OrbTextTexture(texture: glyphs, size: CGSize(width: 10, height: 12), baseline: 10, advance: 8, padding: 1)
        let draw = OrbTextDraw(texture, x: 0, baseline: 10, color: .white).outlined(.black, width: 1.5)
        #expect(draw.instance.gradient.z == 3)
        #expect(draw.instance.gradient.x == 1.5)
        #expect(draw.instance.fill2.w == 1)
    }
}
