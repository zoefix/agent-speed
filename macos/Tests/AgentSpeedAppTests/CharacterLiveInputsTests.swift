@testable import AgentSpeed
import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Foundation
import Testing

/// The inputs of a living character in the app (CHARACTERS 5, Behavior, Inputs): the runtime's agents into the orb's
/// live inputs, the pointer onto her canvas and into her heights, the hit capsule, and the events of each frame.
@MainActor
@Suite struct CharacterLiveInputsTests {
    static let fixture = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("spec/fixtures/characters/rig.json")

    /// A living pack on disk: the fixture rig with a face box, feet and face patches (no mood poses).
    static func livingPack(patches: Bool = true) throws -> CharacterPack {
        var rig = (try JSONSerialization.jsonObject(with: Data(contentsOf: fixture)) as! [String: Any])["rig"] as! [String: Any]
        rig["landmarks"] = ["feet": [50, 90], "face": [40, 10, 60, 30], "faceWidth": 20, "objectSide": "left"]
        var params = rig["params"] as! [[String: Any]]
        params += ["expr-smile", "mouth-a"].map { ["id": "Var.\($0)", "min": 0, "max": 1, "default": 0] }
        rig["params"] = params
        rig["variants"] = patches ? [["id": "expr-smile", "kind": "patch", "param": "Var.expr-smile", "region": "face"],
                                     ["id": "mouth-a", "kind": "patch", "param": "Var.mouth-a", "region": "mouth"]] : []
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("living-\(UUID().uuidString)")
        let dir = root.appendingPathComponent("poses/idle")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: rig).write(to: dir.appendingPathComponent("rig.json"))
        try Data([0]).write(to: dir.appendingPathComponent("atlas.png"))
        try Data([0]).write(to: root.appendingPathComponent("preview.png"))
        let manifest: [String: Any] = [
            "format": "agent-speed-character-pack", "version": 1, "id": "living", "packVersion": 1,
            "name": ["zh-Hant": "活", "zh-Hans": "活", "en": "Living", "ja": "活"], "vendors": ["anthropic"],
            "accent": "#D97757", "poses": ["idle": "poses/idle"], "preview": "preview.png",
        ]
        try JSONSerialization.data(withJSONObject: manifest).write(to: root.appendingPathComponent("manifest.json"))
        return try CharacterPack.load(from: root)
    }

    static func frame(time: TimeInterval, diameter: CGFloat = 296) -> OrbFrame {
        let geometry = OrbGeometry(diameter: diameter, canvasSide: diameter * 1.5)
        return OrbFrame.scheduling(time: time, gauge: nil, effects: OrbEffects(level: .standard, reduceMotion: false,
                                                                               surface: .floating),
                                   unavailable: false, geometry: geometry)
    }

    @Test func theRuntimeHandsTheOrbsPhaseWaitAndIdleToHerInputs() throws {
        let suite = "CharacterLiveInputsTests"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let settings = AppSettings(defaults: try #require(UserDefaults(suiteName: suite)))
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        let runtime = OrbRuntime(config: OrbConfig(id: "orb", style: .character))
        var snapshot = PreviewData.snapshot(speed: 0, redline: 400)
        let start = Date(timeIntervalSinceReferenceDate: 1000)
        for i in snapshot.agents.indices {
            snapshot.agents[i].phase = i == 0 ? .generating : .idle
            snapshot.agents[i].inFlightSince = i == 0 ? start : nil
            snapshot.agents[i].outputSinceInFlight = false
            snapshot.agents[i].lastResponseEnd = start.addingTimeInterval(-30)
        }
        runtime.ingest(snapshot, settings: settings, cost: nil, isPreviewing: false, now: 1010)
        let live = runtime.feed.characterLive
        #expect(live.phase == .generating)
        #expect(live.waitingSince == 1000)
        #expect(live.idleSince == 1010, "the orb's first reading stands for the app's start")

        for i in snapshot.agents.indices {
            snapshot.agents[i].phase = .idle
            snapshot.agents[i].inFlightSince = nil
            snapshot.agents[i].lastResponseEnd = start.addingTimeInterval(20)
        }
        runtime.ingest(snapshot, settings: settings, cost: nil, isPreviewing: false, now: 1030)
        #expect(live.phase == .idle && live.waitingSince == nil)
        #expect(live.idleSince == 1020)
        #expect(live.celebratesPeaks == settings.celebratePeaks)

        let peaks = live.peaks
        runtime.handle(.newPeak(120), isPreviewing: false)
        #expect(live.peaks == peaks + 1)
    }

    @Test func aScreenPointLandsOnHerCanvasWithTheHoverScaleUndone() {
        let window = NSRect(x: 100, y: 200, width: 300, height: 300)
        let topLeft = OrbCharacterLive.canvasPoint(screen: NSPoint(x: 100, y: 500), windowFrame: window, canvasSide: 300,
                                                   contentScale: 1)
        #expect(topLeft == CGPoint(x: 0, y: 0))
        let center = OrbCharacterLive.canvasPoint(screen: NSPoint(x: 250, y: 350), windowFrame: window, canvasSide: 300,
                                                  contentScale: 1.03)
        #expect(center == CGPoint(x: 150, y: 150))
        let right = OrbCharacterLive.canvasPoint(screen: NSPoint(x: 353, y: 350), windowFrame: window, canvasSide: 300,
                                                 contentScale: 1.03)
        #expect(abs(right.x - 250) < 1e-9 && right.y == 150)
    }

    @Test func theCursorIsInHerHeightsFromHerFaceWithinTwoHeights() throws {
        let c = try #require(CharacterRenderer.cursor(CGPoint(x: 160, y: 130), face: (100, 100), height: 200))
        #expect(abs(c.x - 0.3) < 1e-12 && abs(c.y - 0.15) < 1e-12)
        #expect(CharacterRenderer.cursor(CGPoint(x: 100 + 300, y: 100 + 300), face: (100, 100), height: 200) == nil)
        #expect(CharacterRenderer.cursor(CGPoint(x: 100 + 399, y: 100), face: (100, 100), height: 200) != nil)
    }

    @Test func overIsHerHitCapsuleMovedWithHerSideSteps() {
        let frame = Self.frame(time: 0)
        let c = frame.center, d = frame.s
        #expect(CharacterRenderer.overCapsule(CGPoint(x: c.x, y: c.y), frame: frame, bodyDX: 0))
        #expect(!CharacterRenderer.overCapsule(CGPoint(x: c.x + 0.3 * d, y: c.y), frame: frame, bodyDX: 0))
        #expect(CharacterRenderer.overCapsule(CGPoint(x: c.x + 0.3 * d, y: c.y), frame: frame, bodyDX: 0.3 * Double(d)))
        #expect(!CharacterRenderer.overCapsule(CGPoint(x: c.x, y: c.y - 0.5 * d), frame: frame, bodyDX: 0))
    }

    @Test func eachFrameTakesTheLiveInputsAndFiresEachEventOnce() throws {
        let performer = CharacterPerformer(pack: try Self.livingPack())
        #expect(performer.living)
        let renderer = CharacterRenderer()
        let live = OrbCharacterLive()
        live.phase = .generating
        live.waitingSince = 95
        live.idleSince = 40
        live.hour = 3
        live.notePeak()
        let first = try #require(renderer.behaviorInputs(Self.frame(time: 100), live: live, performer: performer))
        #expect(first.phase == .generating && first.wait == 5 && first.idle == 0 && first.hour == 3)
        #expect(!first.newPeak, "an event before her first frame never fires")
        #expect(first.cursor == nil && !first.over, "no window: no pointer")

        live.notePeak()
        live.noteTrouble()
        live.phase = .idle
        let second = try #require(renderer.behaviorInputs(Self.frame(time: 101), live: live, performer: performer))
        #expect(second.newPeak && second.trouble && second.wait == nil && second.idle == 61)
        let third = try #require(renderer.behaviorInputs(Self.frame(time: 102), live: live, performer: performer))
        #expect(!third.newPeak && !third.trouble)

        live.celebratesPeaks = false
        live.notePeak()
        let quiet = try #require(renderer.behaviorInputs(Self.frame(time: 103), live: live, performer: performer))
        #expect(!quiet.newPeak, "peaks cheer her only while they are celebrated")

        live.scripted = true
        live.scriptedCursor = (0.2, 0.1)
        let scripted = try #require(renderer.behaviorInputs(Self.frame(time: 104), live: live, performer: performer))
        let cursor = try #require(scripted.cursor)
        #expect(abs(cursor.x - 0.2) < 1e-9 && abs(cursor.y - 0.1) < 1e-9)
        live.scriptedClick()
        let clicked = try #require(renderer.behaviorInputs(Self.frame(time: 105), live: live, performer: performer))
        #expect(!clicked.click, "without her traced outline a click lands on nothing")
    }

    @Test func aPackWithoutPatchesOrMoodPosesGetsNoInputs() throws {
        let performer = CharacterPerformer(pack: try Self.livingPack(patches: false))
        #expect(!performer.living)
        let live = OrbCharacterLive()
        live.phase = .generating
        #expect(CharacterRenderer().behaviorInputs(Self.frame(time: 1), live: live, performer: performer) == nil)
    }

    @Test func theHarnessSessionReadsTheFixtureScript() throws {
        let doc = try JSONSerialization.jsonObject(with: Data(contentsOf: Self.fixture.deletingLastPathComponent()
            .appendingPathComponent("behavior.json"))) as! [String: Any]
        let data = try JSONSerialization.data(withJSONObject: doc["session"]!)
        let script = try #require(CharacterSessionScript(json: data))
        #expect(script.hour == 1)
        #expect(script.phase(at: 1).phase == .idle && script.idle(at: 1) == 901)
        #expect(script.phase(at: 10).phase == .generating && script.wait(at: 10) == 7)
        #expect(script.wait(at: 18) == nil)
        #expect(script.phase(at: 40).phase == .tools)
        #expect(script.idle(at: 90) == 120 + 90 - 53)
        #expect(script.cursor(at: 58) == nil)
        let c = try #require(script.cursor(at: 59.8))
        #expect(abs(c.x - (1.5 + (0.55 - 1.5) * 0.5)) < 1e-9)
        #expect(script.seconds == 92)
    }
}
