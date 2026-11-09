import Foundation

/// Turn side-on, swap the illustration, turn back (spec/CHARACTERS.md, Pose swap): `Spin` runs 0 → 0.25 (edge-on)
/// over 0.2 s, the frame that crosses 0.2 s shows the new pose at exactly 0.75 (also edge-on), then `Spin` runs on to 1
/// over 0.32 s with a small squash, lift and skirt flare, and returns to 0.
public struct PoseSwitch: Sendable {
    public static let outDuration = 0.2
    public static let inDuration = 0.32

    public private(set) var t: Double?
    public private(set) var target: Int?
    let hop = Curve([CurveKey(0, 0), CurveKey(0.08, -0.25), CurveKey(0.18, 0.35), CurveKey(0.36, 0.1),
                     CurveKey(0.46, -0.3), CurveKey(0.52, 0)])
    let lift = Curve([CurveKey(0, 0), CurveKey(0.12, 0), CurveKey(0.3, 0.07), CurveKey(0.46, 0), CurveKey(0.52, 0)])
    let flare = Curve([CurveKey(0, 0), CurveKey(0.2, 0.7), CurveKey(0.4, 0.5), CurveKey(0.7, 0)])

    public init() {}

    public var isRunning: Bool { t != nil }

    /// Starts a swap toward `target`; ignored while one runs.
    public mutating func request(_ target: Int) {
        if t == nil {
            t = 0
            self.target = target
        }
    }

    /// Starts on the edge-on instant, for a character that takes over from another one: the next update shows
    /// `Spin` = 0.75 and turns back from there.
    public mutating func enterEdgeOn() {
        t = PoseSwitch.outDuration - 1e-9
        target = nil
    }

    /// The values the swap writes this frame.
    public struct Output: Sendable {
        public var spin: Double
        public var squash: Double
        public var posY: Double
        public var skirtFlare: Double
    }

    /// Advances one frame. Returns the target on the edge-on frame that swaps (else nil) and the values to apply:
    /// `Spin` replaces, squash and lift add, the flare takes the larger value.
    public mutating func update(_ dt: Double) -> (swap: Int?, output: Output?, swapped: Bool) {
        guard let before = t else { return (nil, nil, false) }
        var now = before + dt
        var swap: Int?
        var swapped = false
        let spin: Double
        if now < PoseSwitch.outDuration {
            let k = now / PoseSwitch.outDuration
            spin = 0.25 * k * k
        } else {
            if before < PoseSwitch.outDuration {
                swap = target
                swapped = true
                now = PoseSwitch.outDuration
            }
            let k = min(1.0, (now - PoseSwitch.outDuration) / PoseSwitch.inDuration)
            spin = 0.75 + 0.25 * (1 - pow(1 - k, 3))
        }
        var out = Output(spin: spin, squash: hop(now), posY: lift(now), skirtFlare: flare(now))
        if now >= PoseSwitch.outDuration + PoseSwitch.inDuration {
            out.spin = 0
            t = nil
        } else {
            t = now
        }
        return (swap, out, swapped)
    }
}
