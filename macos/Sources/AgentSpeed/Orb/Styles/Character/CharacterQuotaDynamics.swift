import AgentSpeedCore
import Foundation

/// What the 額度 card carries from frame to frame (DESIGN 2a.6, 額度 *Motion*): a bar eases to its new length (0.4 s,
/// ease-out), a reset empties it over 0.6 s while its reset cell fades out, a changed percent or reset crossfades
/// (0.2 s), and a source or window joining or leaving fades (0.25 s) as the card eases to its height. Reduce Motion
/// changes lengths at once; the fades stay. Sources and lines that leave keep their place and text while they fade.
struct CharacterQuotaDynamics {
    static let fade = 0.25
    static let barEase = 0.4
    static let resetEmpty = 0.6
    static let crossfade = 0.2

    struct Line {
        var label: String
        var presence: Double
        var wanted: Bool
        /// The bar's fill as drawn, 0…1, easing from `from` to `to`.
        var bar: Double
        var from: Double
        var to: Double
        var progress = 1.0
        var duration = CharacterQuotaDynamics.barEase
        var percent: Int
        var oldPercent: Int?
        var percentMix = 1.0
        /// The reset text drawn (kept while its cell fades out), the one it replaces, and the cell's presence.
        var reset: String?
        var oldReset: String?
        var resetMix = 1.0
        var resetPresence: Double
        var resetWanted: Bool
        var passed: Bool

        var isMoving: Bool {
            (presence > 0 && presence < 1) || progress < 1 || percentMix < 1 || resetMix < 1
                || resetPresence != (resetWanted ? 1 : 0)
        }
    }

    struct Source {
        var source: SourceKind
        var key: String
        var name: String
        var host: String?
        var tint: RGBColor
        var presence: Double
        var wanted: Bool
        var lines: [Line]
    }

    private(set) var sources: [Source] = []
    private var last: [CharacterQuotaSource]?
    private var lastSmall = false

    var isMoving: Bool { sources.contains { ($0.presence > 0 && $0.presence < 1) || $0.lines.contains(where: \.isMoving) } }

    /// The sources' room, in shares of their height.
    var extent: Double { sources.reduce(0) { $0 + $1.presence } }

    /// Advances one frame toward `quota`. `small`: below S = 96 the card lists its first source alone (DESIGN 2a.6,
    /// LOD). `instant` changes lengths at once (Reduce Motion).
    mutating func update(_ dt: Double, quota: [CharacterQuotaSource], small: Bool, instant: Bool) {
        let target = small ? Array(quota.prefix(1)) : quota
        if target == last, small == lastSmall, !isMoving { return }
        let appearing = sources.isEmpty
        if target != last || small != lastSmall {
            last = target
            lastSmall = small
            merge(target, appearing: appearing, instant: instant)
        }
        advance(dt, instant: instant)
    }

    /// The card has faded out: its next sources appear with it, their bars growing from empty.
    mutating func reset() {
        sources = []
        last = nil
    }

    private mutating func merge(_ target: [CharacterQuotaSource], appearing: Bool, instant: Bool) {
        let wanted = Set(target.map(\.key))
        var merged: [Source] = target.map { next in
            guard var source = sources.first(where: { $0.key == next.key }) else {
                return Source(source: next.source, key: next.key, name: next.name, host: next.host, tint: next.tint,
                              presence: appearing ? 1 : 0,
                              wanted: true, lines: Self.merged([], next.lines, appearing: true, instant: instant))
            }
            source.name = next.name
            source.host = next.host
            source.tint = next.tint
            source.wanted = true
            source.lines = Self.merged(source.lines, next.lines, appearing: false, instant: instant)
            return source
        }
        for (index, source) in sources.enumerated() where !wanted.contains(source.key) {
            var leaving = source
            leaving.wanted = false
            merged.insert(leaving, at: min(index, merged.count))
        }
        sources = merged
    }

    /// A source's lines toward `next`, in its order; a new line's bar grows from empty.
    private static func merged(_ lines: [Line], _ next: [CharacterQuotaLine], appearing: Bool, instant: Bool) -> [Line] {
        let wanted = Set(next.map(\.label))
        var merged: [Line] = next.map { target in
            guard var line = lines.first(where: { $0.label == target.label }) else {
                return Line(label: target.label, presence: appearing ? 1 : 0, wanted: true, bar: instant ? target.bar : 0,
                            from: instant ? target.bar : 0, to: target.bar, progress: instant ? 1 : 0,
                            percent: target.percent, reset: target.reset, resetPresence: target.reset == nil ? 0 : 1,
                            resetWanted: target.reset != nil, passed: target.passed)
            }
            line.wanted = true
            if target.bar != line.to {
                line.from = line.bar
                line.to = target.bar
                line.progress = instant ? 1 : 0
                line.duration = target.passed && !line.passed ? resetEmpty : barEase
            }
            if target.percent != line.percent {
                line.oldPercent = line.percent
                line.percent = target.percent
                line.percentMix = 0
            }
            if let reset = target.reset, reset != line.reset {
                line.oldReset = line.resetPresence > 0 ? line.reset : nil
                line.reset = reset
                line.resetMix = line.oldReset == nil ? 1 : 0
            }
            line.resetWanted = target.reset != nil
            line.passed = target.passed
            return line
        }
        for (index, line) in lines.enumerated() where !wanted.contains(line.label) {
            var leaving = line
            leaving.wanted = false
            merged.insert(leaving, at: min(index, merged.count))
        }
        return merged
    }

    private mutating func advance(_ dt: Double, instant: Bool) {
        let fade = dt / Self.fade
        for s in sources.indices {
            sources[s].presence = sources[s].wanted ? min(1, sources[s].presence + fade) : max(0, sources[s].presence - fade)
            for l in sources[s].lines.indices {
                var line = sources[s].lines[l]
                line.presence = line.wanted ? min(1, line.presence + fade) : max(0, line.presence - fade)
                line.progress = instant ? 1 : min(1, line.progress + dt / line.duration)
                let k = 1 - pow(1 - line.progress, 3)
                line.bar = line.from + (line.to - line.from) * k
                line.percentMix = min(1, line.percentMix + dt / Self.crossfade)
                if line.percentMix >= 1 { line.oldPercent = nil }
                line.resetMix = min(1, line.resetMix + dt / Self.crossfade)
                if line.resetMix >= 1 { line.oldReset = nil }
                // A passed reset fades its cell with the emptying bar; a new one comes in like a crossfade.
                line.resetPresence = line.resetWanted ? min(1, line.resetPresence + dt / Self.crossfade)
                    : max(0, line.resetPresence - dt / Self.resetEmpty)
                if line.resetPresence == 0, !line.resetWanted { line.reset = nil }
                sources[s].lines[l] = line
            }
            sources[s].lines.removeAll { !$0.wanted && $0.presence <= 0 }
        }
        sources.removeAll { !$0.wanted && $0.presence <= 0 }
    }
}
