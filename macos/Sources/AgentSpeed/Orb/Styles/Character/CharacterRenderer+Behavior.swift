import AgentSpeedCharacter
import AgentSpeedOrbModel
import Foundation

/// The inputs of a living character for one frame (CHARACTERS 5, Behavior, Inputs): the agents' phase, wait, idle time,
/// hour and events from the orb's runtime, and the pointer read here, only while her style draws.
extension CharacterRenderer {
    /// Slack around her drawn outline for a click, in points.
    static let clickSlack = 2.0

    func behaviorInputs(_ frame: OrbFrame, live: OrbCharacterLive?, performer: CharacterPerformer) -> BehaviorInputs? {
        guard let live, performer.living else { return nil }
        var inputs = BehaviorInputs(phase: live.phase, hour: live.hour)
        if live.phase == .generating, let since = live.waitingSince { inputs.wait = max(0, frame.time - since) }
        if live.phase == .idle { inputs.idle = max(0, frame.time - live.idleSince) }
        inputs.newPeak = Self.fresh(live.peaks, &seenPeaks) && live.celebratesPeaks
        inputs.trouble = Self.fresh(live.troubles, &seenTroubles)

        let g = geometry(frame, performer)
        let face = performer.faceCenter(feetOnScreen: (g.feet.x, g.feet.y), pointsPerPixel: g.pointsPerPixel)
        let side = 2 * Double(frame.center.x)
        let point: CGPoint? = if live.scripted {
            live.scriptedCursor.map { CGPoint(x: face.x + $0.x * g.height, y: face.y + $0.y * g.height) }
        } else {
            live.pointerOnCanvas(canvasSide: side, contentScale: Double(frame.contentScale))
        }
        lastFace = face
        lastPointer = point
        if let point {
            inputs.cursor = Self.cursor(point, face: face, height: g.height)
            inputs.over = !live.isDragging && Self.overCapsule(point, frame: frame, bodyDX: g.bodyDX)
        }
        if Self.fresh(live.clicks, &seenClicks) {
            let press = live.scripted ? point : live.clickOnCanvas(canvasSide: side, contentScale: Double(frame.contentScale))
            inputs.click = press.map { silhouette?.contains(Double($0.x), Double($0.y), slack: Self.clickSlack) == true }
                ?? false
        }
        return inputs
    }

    /// The pointer relative to her face in her heights (screen axes, y down); nil beyond her look range.
    static func cursor(_ point: CGPoint, face: (x: Double, y: Double), height: Double) -> (x: Double, y: Double)? {
        guard height > 0 else { return nil }
        let x = (Double(point.x) - face.x) / height, y = (Double(point.y) - face.y) / height
        return hypot(x, y) <= CharacterBehavior.lookRange ? (x, y) : nil
    }

    /// Whether a canvas point (y down) is inside her hit capsule (DESIGN 2a.6), moved with her side steps.
    static func overCapsule(_ point: CGPoint, frame: OrbFrame, bodyDX: Double) -> Bool {
        OrbHitShape.character.contains(dx: point.x - frame.center.x - CGFloat(bodyDX), dy: frame.center.y - point.y,
                                       diameter: frame.s, tolerance: OrbHostView.hitTolerance)
    }

    /// Her mood, pose, face, gaze, mouth and action this frame, for the frame harness's trace.
    var behaviorTrace: String {
        guard let performer = stage?.current else { return "-" }
        let d = performer.director
        let pose = performer.pack.poses[performer.activePose].name
        let angle = d.value("AngleX").map { String(format: "%.2f", $0) } ?? "-"
        return "mood=\(d.mood?.rawValue ?? "-") pose=\(pose) face=\(d.expression ?? "-") gaze=\(d.gazePatch ?? "-") "
            + "mouth=\(d.mouthShape ?? "-") action=\(d.activeGesture ?? "-") angleX=\(angle) acting=\(performer.isActing)"
            + (lastFace.map { String(format: " faceAt=%.1f,%.1f", $0.x, $0.y) } ?? "")
            + (lastPointer.map { String(format: " pointer=%.1f,%.1f", $0.x, $0.y) } ?? "")
    }

    /// Whether a count moved since the last frame; the first frame only takes it.
    static func fresh(_ count: Int, _ seen: inout Int?) -> Bool {
        defer { seen = count }
        return seen.map { $0 != count } ?? false
    }
}
