import AgentSpeedOrbModel
import AppKit

/// `--board-frames DIR` with `AGENT_SPEED_BOARD_CHECKS=1`: offscreen checks of the content stack the app target has no
/// unit tests for — a layout applied again replays none of its one-shot accents, Reduce Motion crossfades moved rows
/// (the window passes `animated` through), and the ambience and celebration switches. Writes `board-checks.txt`.
@MainActor
enum BoardStackChecks {
    static func run(into directory: URL) {
        var lines: [String] = []
        func check(_ name: String, _ passed: Bool) { lines.append((passed ? "ok    " : "FAIL  ") + name) }
        let l10n = BoardSimFeed.harnessLocalizer()

        // One-shot accents come with the first apply of a layout only.
        let feed = BoardSimFeed(simulation: BoardSimulation(scenario: "team"), style: .glass, size: .medium, dark: true, l10n: l10n)
        var steps = 0
        while steps < 200, !feed.layout.items.contains(where: { !$0.accents.isEmpty }) {
            feed.step()
            steps += 1
        }
        let stack = BoardContentStack()
        stack.setBoard(side: 340, corner: 22)
        let first = feed.takeItems()
        check("a fresh layout carries accents", first.contains { !$0.accents.isEmpty })
        stack.apply(first, animated: true)
        let accentKeys: Set = ["roll", "cool", "dim", "punch", "change"]
        check("the first apply plays them", count(stack.layer, keys: accentKeys) > 0 || decals(stack.layer) > 0)
        clear(stack.layer)
        let decalsBefore = decals(stack.layer)
        let again = feed.takeItems()
        check("the same layout taken again has none", again.allSatisfy { $0.accents.isEmpty })
        stack.apply(again, animated: true)
        check("applying it again plays none", count(stack.layer, keys: accentKeys) == 0 && decals(stack.layer) == decalsBefore)

        // Reduce Motion: a moved row crossfades through the window controller.
        let window = BoardWindowController(config: BoardConfig(id: "checks", style: .glass, size: .medium), center: .zero, dark: true)
        window.reduceMotion = true
        var items = feed.takeItems()
        window.contentProvider = { items }
        window.contentChanged(animated: false)
        window.host.content.layer.removeAllAnimations()
        if let index = items.firstIndex(where: { $0.motion == .row }) { items[index].frame.origin.y += 32 }
        window.contentChanged()
        // Core Animation files a CATransition under "transition" whatever key it is added with.
        let fade = (window.host.content.layer.animationKeys() ?? []).compactMap { window.host.content.layer.animation(forKey: $0) as? CATransition }
        check("Reduce Motion crossfades a moved row", fade.contains { $0.type == .fade })
        check("Reduce Motion springs nothing", count(window.host.content.layer, keys: ["move"]) == 0)

        // Effects Off stops the ambience; Celebrate new peaks off drops the celebration decals.
        let neon = BoardSimFeed(simulation: BoardSimulation(scenario: "team"), style: .neon, size: .medium, dark: true, l10n: l10n)
        for _ in 0..<12 { neon.step() }
        let ambient = BoardContentStack()
        ambient.setBoard(side: 340, corner: 22)
        ambient.apply(neon.takeItems(), animated: false)
        let breathing = count(ambient.layer, keys: ["ambient"])
        ambient.ambience = false
        check("a generating neon board breathes", breathing > 0)
        check("Effects Off stops the ambience", count(ambient.layer, keys: ["ambient"]) == 0)
        let peak = BoardContentStack()
        peak.celebrations = false
        let decal = BoardDecal(frame: CGRect(x: 0, y: 0, width: 20, height: 10), motion: .pop(seconds: 1), celebration: true) { _, _ in }
        var item = BoardContentItem(key: "x", frame: CGRect(x: 0, y: 0, width: 40, height: 20), motion: .none, revision: AnyHashable(0)) { _, _ in }
        item.accents = [.decal(decal)]
        peak.apply([item], animated: true)
        check("Celebrate new peaks off drops celebration decals", decals(peak.layer) == 0)

        window.close()
        liveMetrics(check)
        let text = lines.joined(separator: "\n") + "\n"
        try? text.write(to: directory.appendingPathComponent("board-checks.txt"), atomically: true, encoding: .utf8)
        print(text, terminator: "")
    }

    /// The live metrics' motion (DESIGN 2c Live metrics): 思考 hiding fades its row in place while the sub-gauge glides
    /// (at once with Reduce Motion), a new 性能 moves the arc's end and tip and rolls its digits, a switch change
    /// crossfades the hero while rows spring, and an equal tick adds nothing.
    private static func liveMetrics(_ check: (String, Bool) -> Void) {
        let l10n = BoardSimFeed.harnessLocalizer()
        let feed = BoardSimFeed(simulation: BoardSimulation(scenario: "team"), style: .glass, size: .medium, dark: true, l10n: l10n,
                                metrics: .init(readouts: .all))
        for _ in 0..<40 { feed.step() }
        let shown = feed.tick
        var hidden = shown
        hidden.metrics.thinkingHidden = true
        for reduceMotion in [false, true] {
            let builder = BoardContentBuilders.make(style: .glass, size: .medium, dark: true, l10n: l10n, capsule: nil, highContrast: false)
            let stack = BoardContentStack()
            stack.setBoard(side: 340, corner: 26)
            stack.reduceMotion = reduceMotion
            var first = builder.layout(shown)
            stack.apply(first.takeItems(), animated: false)
            clear(stack.layer)
            var second = builder.layout(hidden)
            stack.apply(second.takeItems(), animated: true)
            let glides = moves(stack.layer, duration: BoardContentStack.glideSeconds)
            let fades = animations(stack.layer, key: "leave").filter { abs($0.duration - 0.25) < 0.001 || abs($0.duration - 0.15) < 0.001 }
            if reduceMotion {
                check("Reduce Motion: 思考 hides at once", glides == 0 && fades.isEmpty)
            } else {
                check("思考 hiding glides the sub-gauge (0.3 s smoothstep)", glides >= 4)
                check("思考 hiding fades its row in place", fades.count >= 2)
            }
        }

        // A small rise of 性能 (below the count threshold): the arc's mask and the tip move on the roll's curve, the digits change in place;
        // the table's changed digits roll.
        let builder = BoardContentBuilders.make(style: .glass, size: .medium, dark: true, l10n: l10n, capsule: nil, highContrast: false)
        let stack = BoardContentStack()
        stack.setBoard(side: 340, corner: 26)
        var base = builder.layout(shown)
        stack.apply(base.takeItems(), animated: false)
        clear(stack.layer)
        var next = shown
        let score = shown.metrics.intelligence
        next.metrics.intelligence = PerfSetReading(form: .live, shown: (score.shown ?? 1000) + 2,
                                                   provisional: score.provisional, count: score.count, agentId: score.agentId)
        next.metrics.thinking = "175"
        var moved = builder.layout(next)
        stack.apply(moved.takeItems(), animated: true)
        check("a new 性能 sweeps the arc", !animations(stack.layer, key: "sweep").isEmpty)
        check("a new 性能 turns the tip", !animations(stack.layer, key: "turn").isEmpty)
        // Core Animation files the rolls' push transitions under "transition".
        let pushes = animations(stack.layer, key: "transition").compactMap { $0 as? CATransition }.filter { $0.type == .push }
        // 思考 174 → 175: its last digit rolls; 性能's digits never roll (DESIGN 12 Motion).
        check("changed 思考 digits roll, 性能's do not", pushes.count == 1)
        clear(stack.layer)
        var again = builder.layout(next)
        stack.apply(again.takeItems(), animated: true)
        check("an equal tick adds no animation",
              count(stack.layer, keys: ["transition", "sweep", "turn", "move", "leave", "enter", "change"]) == 0)

        // A rise of 3 or more counts up (DESIGN 12 Motion): no sweep on the tick, the frames follow the shown value.
        clear(stack.layer)
        var climb = next
        climb.metrics.intelligence.shown = (next.metrics.intelligence.shown ?? 50) + 30 > 100 ? 100 : (next.metrics.intelligence.shown ?? 50) + 30
        var climbing = builder.layout(climb)
        stack.apply(climbing.takeItems(), animated: true)
        check("a climb starts a count-up", builder.climbsUntil(after: Clock.now) != nil)
        check("a climb's first frame does not sweep", animations(stack.layer, key: "sweep").isEmpty)
        // A fall crossfades the number in 0.2 s, never rolling down.
        clear(stack.layer)
        var fall = climb
        fall.metrics.intelligence.shown = 12
        var fallen = builder.layout(fall)
        stack.apply(fallen.takeItems(), animated: true)
        let transitions = animations(stack.layer, key: "transition").compactMap { $0 as? CATransition }
        check("a fall crossfades the number", transitions.contains { $0.type == .fade && abs($0.duration - 0.2) < 0.001 })
        check("a fall never rolls", !transitions.contains { $0.type == .push })
        check("a fall ends the count-up", builder.climbsUntil(after: Clock.now) == nil)

        // A switch change: the hero crossfades as a whole, rows spring to their new places.
        var off = next
        off.metrics.readouts = BoardReadouts(intelligence: false, thinking: true, cost: true)
        off.rows = BoardLayoutPlanner.plan(off.groups.map { group in BoardPlanGroup(group) { _ in 0 } },
                                           metrics: BoardLayoutMetrics(BoardGeometry(.glass, .medium, readouts: off.metrics.readouts)))
        var switched = builder.layout(off)
        stack.apply(switched.takeItems(), animated: true, crossfade: BoardPresenter.switchCrossfade)
        let hero = stack.layer.sublayers?.first { layer in (layer.animationKeys() ?? []).contains("transition") && layer.sublayers?.isEmpty == false }
        check("a switch change crossfades the hero", hero != nil)
        check("a switch change springs the rows", count(stack.layer, keys: ["move"]) > 0)
    }

    private static func animations(_ root: CALayer, key: String) -> [CAAnimation] {
        var found = root.animation(forKey: key).map { [$0] } ?? []
        if let mask = root.mask { found += animations(mask, key: key) }
        for child in root.sublayers ?? [] { found += animations(child, key: key) }
        return found
    }

    /// Position moves of `duration` (the sub-gauge's glide; rows spring with their settling time instead).
    private static func moves(_ root: CALayer, duration: Double) -> Int {
        animations(root, key: "move").filter { !($0 is CASpringAnimation) && abs($0.duration - duration) < 0.001 }.count
    }

    private static func count(_ root: CALayer, keys: Set<String>) -> Int {
        var total = (root.animationKeys() ?? []).filter(keys.contains).count
        for child in root.sublayers ?? [] { total += count(child, keys: keys) }
        return total
    }

    /// Decal layers (unnamed sublayers of an item's layer).
    private static func decals(_ root: CALayer) -> Int {
        (root.sublayers ?? []).reduce(0) { $0 + ($1.sublayers?.filter { $0.name == nil }.count ?? 0) }
    }

    private static func clear(_ root: CALayer) {
        root.removeAllAnimations()
        root.mask?.removeAllAnimations()
        root.sublayers?.forEach(clear)
    }
}
