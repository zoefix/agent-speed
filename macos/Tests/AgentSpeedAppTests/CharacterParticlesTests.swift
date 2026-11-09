@testable import AgentSpeed
import Testing

/// The characters' particle kinds (spec/CHARACTERS.md, Particles): emission, the pool, bursts and what they draw.
@Suite struct CharacterParticlesTests {
    static let area = (0.0, 0.0, 600.0, 900.0)

    func run(_ p: inout CharacterParticles, seconds: Double, intensity: Double, rate: Double = 1) {
        for _ in 0..<Int(seconds * 60) {
            p.update(1.0 / 60, intensity: intensity, area: Self.area, unit: 1, rate: rate)
        }
    }

    @Test func everyKindDrawsWithinThePool() {
        for name in CharacterParticles.kinds.keys.sorted() {
            var p = CharacterParticles(seed: 3)
            p.select(name, accent: "#4D6BFE", hasSprite: false)
            #expect(p.name == name)
            run(&p, seconds: 4, intensity: name == "zzz" ? 0 : 1)
            #expect(!p.items.isEmpty, "\(name)")
            #expect(p.items.count <= CharacterParticles.limit)
            var dots: [CharacterDot] = []
            p.dots(front: true, alpha: 1, into: &dots)
            p.dots(front: false, alpha: 1, into: &dots)
            let glyphs = p.glyphs(front: true) + p.glyphs(front: false)
            if p.kind?.look == .glyph {
                #expect(dots.isEmpty && glyphs.count == p.items.count, "\(name)")
            } else {
                #expect(!dots.isEmpty && glyphs.isEmpty, "\(name)")
            }
            for dot in dots {
                #expect(dot.color.w >= 0 && dot.color.w <= 1 && dot.geometry.z > 0 && dot.geometry.w > 0, "\(name)")
            }
        }
    }

    @Test func nothingIsEmittedWhileParticlesAreOff() {
        for name in CharacterParticles.kinds.keys {
            var p = CharacterParticles()
            p.select(name, accent: "#FFFFFF", hasSprite: false)
            run(&p, seconds: 3, intensity: 0.8, rate: 0)
            #expect(p.items.isEmpty, "\(name)")
        }
    }

    @Test func sleepyKindsDriftOnlyWhileIdle() {
        var p = CharacterParticles()
        p.select("zzz", accent: "#0668E1", hasSprite: false)
        run(&p, seconds: 5, intensity: 1)
        #expect(p.items.isEmpty)
        run(&p, seconds: 5, intensity: 0)
        #expect(!p.items.isEmpty)
        #expect(!p.isLively)
    }

    @Test func burstsAskForAFluidFrameRate() {
        var p = CharacterParticles()
        p.select("bubbles", accent: "#4D6BFE", hasSprite: false)
        run(&p, seconds: 2, intensity: 0)
        #expect(!p.items.isEmpty && !p.isLively)
        p.burst(x: 300, y: 400, unit: 1)
        #expect(p.isLively)
        run(&p, seconds: 3, intensity: 0)
        #expect(!p.isLively)
    }

    @Test func petalsNeedTheirSprite() {
        var p = CharacterParticles()
        p.select("petals", accent: "#D97757", hasSprite: true)
        #expect(p.isPetals)
        run(&p, seconds: 2, intensity: 0.1)
        #expect(p.items.isEmpty)
        run(&p, seconds: 2, intensity: 1)
        #expect(!p.items.isEmpty && p.isLively)
        p.select("petals", accent: "#D97757", hasSprite: false)
        #expect(p.name == "sparkles" && p.items.isEmpty)
        p.select("unknown", accent: "#D97757", hasSprite: true)
        #expect(p.isPetals)
    }

    @Test func accentsParse() {
        #expect(CharacterParticles.rgb("#FF8000") == SIMD3(1, Float(0x80) / 255, 0))
        #expect(CharacterParticles.rgb("nope") == SIMD3(0.85, 0.47, 0.34))
    }
}
