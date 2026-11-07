@testable import AgentSpeed
import AgentSpeedCharacter
import Foundation
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
