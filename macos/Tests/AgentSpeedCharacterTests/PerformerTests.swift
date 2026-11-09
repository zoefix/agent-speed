import Darwin
import Foundation
import Testing
@testable import AgentSpeedCharacter

/// Counts heap allocations made on one thread through the system's malloc logger hook.
enum AllocationProbe {
    typealias Logger = @convention(c) (UInt32, UInt, UInt, UInt, UInt, UInt32) -> Void

    nonisolated(unsafe) static var count = 0
    nonisolated(unsafe) static var thread: pthread_t?
    nonisolated(unsafe) static let slot = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "malloc_logger")?
        .assumingMemoryBound(to: Logger?.self)

    static func allocations(_ body: () -> Void) -> Int? {
        guard let slot else { return nil }
        count = 0
        thread = pthread_self()
        slot.pointee = { type, _, _, _, _, _ in
            if type & 2 != 0, let thread = AllocationProbe.thread, pthread_equal(pthread_self(), thread) != 0 {
                AllocationProbe.count += 1
            }
        }
        body()
        slot.pointee = nil
        thread = nil
        return count
    }
}

#if DEBUG
/// Unoptimized code allocates for generic arithmetic, so the allocation check runs in optimized builds only.
private let optimized = false
#else
private let optimized = true
#endif

/// Builds small packs on disk from the fixture rig, so the tests need no art.
enum TinyPack {
    static func rigJSON(feetY: Double = 90, faceTop: Double = 10, objectSide: String = "left") throws -> [String: Any] {
        var rig = try Fixture.json("rig.json")["rig"] as! [String: Any]
        rig["landmarks"] = ["feet": [50, feetY], "face": [40, faceTop, 60, faceTop + 20], "faceWidth": 20,
                            "objectSide": objectSide]
        var params = rig["params"] as! [[String: Any]]
        params += [["id": "PosX", "min": -1, "max": 1, "default": 0], ["id": "PosY", "min": -1, "max": 1, "default": 0],
                   ["id": "Squash", "min": -1, "max": 1, "default": 0], ["id": "Spin", "min": 0, "max": 1, "default": 0],
                   ["id": "Lean", "min": -1, "max": 1, "default": 0]]
        rig["params"] = params
        return rig
    }

    static func make(bands: [String: [String: Any]], id: String = "tiny", tamper: ((inout [String: Any]) -> Void)? = nil)
        throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("pack-\(UUID().uuidString)")
        for (band, rig) in bands {
            let dir = root.appendingPathComponent("poses/\(band)")
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: rig).write(to: dir.appendingPathComponent("rig.json"))
            try Data([0]).write(to: dir.appendingPathComponent("atlas.png"))
        }
        try Data([0]).write(to: root.appendingPathComponent("preview.png"))
        var manifest: [String: Any] = [
            "format": "agent-speed-character-pack", "version": 1, "id": id, "packVersion": 1,
            "name": ["zh-Hant": "小", "zh-Hans": "小", "en": "Tiny", "ja": "小"], "vendors": ["anthropic"],
            "accent": "#D97757", "poses": Dictionary(uniqueKeysWithValues: bands.keys.map { ($0, "poses/\($0)") }),
            "preview": "preview.png",
        ]
        tamper?(&manifest)
        try JSONSerialization.data(withJSONObject: manifest).write(to: root.appendingPathComponent("manifest.json"))
        return root
    }
}

@Suite struct CharacterPackTests {
    @Test func loadsPosesInBandOrderAndFallsBackToTheNearestLowerBand() throws {
        let rig = try TinyPack.rigJSON()
        let pack = try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig, "boost": rig, "redline": rig]))
        #expect(pack.poses.map(\.band) == [.idle, .boost, .redline])
        let performer = CharacterPerformer(pack: pack)
        #expect(performer.poseIndex(for: .idle) == 0)
        #expect(performer.poseIndex(for: .cruise) == 0)
        #expect(performer.poseIndex(for: .boost) == 1)
        #expect(performer.poseIndex(for: .overdrive) == 1)
        #expect(performer.poseIndex(for: .redline) == 2)
    }

    @Test func refusesPacksThatAreUnsafeOrIncomplete() throws {
        let rig = try TinyPack.rigJSON()
        #expect(throws: PackLoadError.missingIdlePose) {
            try CharacterPack.load(from: TinyPack.make(bands: ["boost": rig]))
        }
        #expect(throws: PackLoadError.unsafePath("../outside")) {
            try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig]) { $0["poses"] = ["idle": "../outside"] })
        }
        #expect(throws: PackLoadError.unsafePath("/etc")) {
            try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig]) { $0["preview"] = "/etc" })
        }
        #expect(throws: PackLoadError.badID("Bad ID")) {
            try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig], id: "Bad ID"))
        }
        #expect(throws: PackLoadError.unsupported(format: "agent-speed-character-pack", version: 2)) {
            try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig]) { $0["version"] = 2 })
        }
    }

    @Test func refusesSymbolicLinksThatLeaveThePack() throws {
        let rig = try TinyPack.rigJSON()
        let root = try TinyPack.make(bands: ["idle": rig])
        let outside = FileManager.default.temporaryDirectory.appendingPathComponent("outside-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        try FileManager.default.removeItem(at: root.appendingPathComponent("preview.png"))
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("preview.png"), withDestinationURL: outside)
        #expect(throws: PackLoadError.unsafePath("preview.png")) { try CharacterPack.load(from: root) }
    }

    @Test func comparesTheMinimumAppVersion() throws {
        let rig = try TinyPack.rigJSON()
        let pack = try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig]) { $0["minApp"] = "1.7.0" })
        #expect(pack.manifest.playable(byApp: "1.7.0"))
        #expect(pack.manifest.playable(byApp: "1.10"))
        #expect(!pack.manifest.playable(byApp: "1.6.3"))
    }
}

@Suite struct CharacterPerformerTests {
    @Test func posesAlignTheirFeetAndFaceToFeetDistance() throws {
        let idle = try TinyPack.rigJSON(feetY: 90, faceTop: 10)
        let tall = try TinyPack.rigJSON(feetY: 95, faceTop: -45)
        let pack = try CharacterPack.load(from: TinyPack.make(bands: ["idle": idle, "boost": tall]))
        let performer = CharacterPerformer(pack: pack)
        #expect(performer.placements[0].scale == 1)
        #expect(abs(performer.placements[1].scale - 70.0 / 130.0) < 1e-12)
        let feet = performer.placements[1].screen(50, 95, feetOnScreen: (200, 300), pointsPerPixel: 2)
        #expect(feet == (200, 300))
    }

    @Test func aJumpWithTuckedLegsKeepsItsSizeByTheMiddleRatio() throws {
        var idle = try TinyPack.rigJSON(feetY: 90, faceTop: 10)
        var jump = try TinyPack.rigJSON(feetY: 60, faceTop: 10)
        var lm = idle["landmarks"] as! [String: Any]
        lm["torsoLength"] = 50
        idle["landmarks"] = lm
        lm = jump["landmarks"] as! [String: Any]
        lm["torsoLength"] = 48
        jump["landmarks"] = lm
        let pack = try CharacterPack.load(from: TinyPack.make(bands: ["idle": idle, "redline": jump]))
        let performer = CharacterPerformer(pack: pack)
        #expect(abs(performer.placements[1].scale - 50.0 / 48.0) < 1e-12)
        #expect(CharacterPerformer.poseScale(base: [70, 28, nil], pose: [40, 28, nil]) == 70.0 / 40.0)
        #expect(CharacterPerformer.poseScale(base: [nil, 28, 50], pose: [nil, 28, 50]) == 1)
    }

    @Test func swapsThePoseEdgeOnWhenTheBandChanges() throws {
        let rig = try TinyPack.rigJSON()
        var other = try TinyPack.rigJSON(objectSide: "right")
        other["name"] = "other"
        let pack = try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig, "boost": other]))
        var performer = CharacterPerformer(pack: pack)
        let spin = performer.director.space.slot("Spin")!
        for _ in 0..<60 { performer.step(1.0 / 60, level: 0) }
        #expect(performer.activePose == 0)
        var swappedAt: Double?
        for _ in 0..<60 {
            performer.step(1.0 / 60, level: 6)
            if performer.events.contains(.poseSwapped(band: .boost)) { swappedAt = performer.values[spin] }
        }
        #expect(performer.activePose == 1)
        #expect(swappedAt == 0.75)
        #expect(performer.director.book == .right)
        #expect(performer.values[spin] == 0)
    }

    @Test func turnsFromOneCharacterToAnotherEdgeOn() throws {
        let rig = try TinyPack.rigJSON()
        let a = CharacterPerformer(pack: try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig], id: "a")))
        let b = CharacterPerformer(pack: try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig, "boost": rig],
                                                                                     id: "b")))
        var stage = CharacterStage(a)
        for _ in 0..<30 { stage.step(1.0 / 60, level: 6) }
        stage.switchTo(b)
        var swapFrame: Int?
        for n in 0..<60 {
            stage.step(1.0 / 60, level: 6)
            if stage.swapped {
                swapFrame = n
                #expect(stage.current.pack.id == "b")
                #expect(stage.current.activePose == 1)
                #expect(stage.current.values[stage.current.director.space.slot("Spin")!] == 0.75)
            }
        }
        let frame = try #require(swapFrame)
        #expect(frame >= 11 && frame <= 13)
        #expect(stage.incoming == nil)
    }

    @Test(.enabled(if: optimized, "allocation check needs an optimized build: swift test -c release"))
    func stepsWithoutAllocatingOnceWarm() throws {
        let rig = try TinyPack.rigJSON()
        var performer = CharacterPerformer(pack: try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig])))
        var hud = CharacterHud()
        let layout = performer.hudLayout(feetOnScreen: (300, 600), pointsPerPixel: 2, unit: 1)
        for n in 0..<240 {
            performer.step(1.0 / 60, level: 3)
            hud.update(1.0 / 60, time: Double(n) / 60, speed: 40, heat: 0.3, intensity: 0.2, title: "x")
            hud.layout(1.0 / 60, layout, agents: [(217, 119, 87)], digitAdvance: 20, peakAt: (360, 300))
        }
        var quietWindows = 0
        for _ in 0..<40 {
            let starts = performer.director.motionStarts
            let count = AllocationProbe.allocations {
                for n in 0..<20 {
                    performer.step(1.0 / 60, level: 3)
                    hud.update(1.0 / 60, time: 2 + Double(n) / 60, speed: 40, heat: 0.3, intensity: 0.2, title: "x")
                    hud.layout(1.0 / 60, layout, agents: [(217, 119, 87)], digitAdvance: 20, peakAt: (360, 300))
                }
            }
            guard let count else { return }
            if performer.director.motionStarts == starts {
                quietWindows += 1
                #expect(count == 0, "allocations in a window without new actions")
            }
        }
        #expect(quietWindows > 0)
    }

    @Test(.enabled(if: ProcessInfo.processInfo.environment["AGENT_SPEED_CHARACTER_PACK"] != nil))
    func playsARealPackQuickly() throws {
        let url = URL(fileURLWithPath: ProcessInfo.processInfo.environment["AGENT_SPEED_CHARACTER_PACK"]!)
        let pack = try CharacterPack.load(from: url)
        var performer = CharacterPerformer(pack: pack)
        var hud = CharacterHud()
        let levels = [0, 2, 6, 10, 13, 7, 1, 0]
        let clock = ContinuousClock()
        let frames = 60 * 40
        let elapsed = clock.measure {
            for n in 0..<frames {
                let level = levels[min(levels.count - 1, n / 300)]
                performer.step(1.0 / 60, level: level)
                let layout = performer.hudLayout(feetOnScreen: (300, 600), pointsPerPixel: 0.4, unit: 0.5)
                hud.update(1.0 / 60, time: Double(n) / 60, speed: Double(level * 30), heat: Double(level) / 13,
                           intensity: performer.intensity, title: "t")
                hud.layout(1.0 / 60, layout, agents: [(217, 119, 87)], digitAdvance: 20, peakAt: (360, 300))
            }
        }
        let perFrame = Double(elapsed.components.attoseconds) / 1e15 / Double(frames)
            + Double(elapsed.components.seconds) * 1000 / Double(frames)
        print("character frame: \(String(format: "%.3f", perFrame)) ms over \(frames) frames, "
              + "\(pack.poses.count) poses, \(pack.poses[0].rig.boneCount) bones")
        #expect(performer.pack.poses.count == 5)
        #expect(perFrame < 2)
    }
}

@Suite struct CharacterHudTests {
    @Test func ringLightsFillWithHeatAndSplitFrontAndBack() {
        var hud = CharacterHud()
        let layout = HudLayout(ground: (300, 600, 200, 34), waist: (300, 400, 220, 36), characterHeight: 700, unit: 1)
        hud.update(1.0 / 60, time: 1, speed: 120, heat: 0.5, intensity: 0.5, title: "x")
        hud.layout(1.0 / 60, layout, agents: [], digitAdvance: 20, peakAt: (360, 300))
        let ringFront = hud.front.filter { $0.y >= 600 }
        let ringBack = hud.back.filter { $0.y < 600 && $0.additive }
        #expect(!ringFront.isEmpty && !ringBack.isEmpty)
        #expect(hud.odometer.digits.count >= 1)
    }

    @Test func peakPillPopsAndFades() {
        var hud = CharacterHud()
        let layout = HudLayout(ground: (300, 600, 200, 34), waist: (300, 400, 220, 36), characterHeight: 700, unit: 1)
        hud.celebratePeak()
        var seen = false
        for n in 0..<180 {
            hud.update(1.0 / 60, time: Double(n) / 60, speed: 300, heat: 0.8, intensity: 0.8, title: "x")
            hud.layout(1.0 / 60, layout, agents: [], digitAdvance: 20, peakAt: (360, 300))
            if hud.peak != nil { seen = true }
        }
        #expect(seen)
        #expect(hud.peak == nil)
    }
}
