import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// `--orb-frames DIR`: records floating orbs frame by frame on a simulated clock with the designer's prototype
/// scenarios and layouts, so the clips can be compared with the prototype recordings side by side; encodes
/// `DIR/<clip>.mp4` with ffmpeg when installed (frames are deleted unless `AGENT_SPEED_ORB_KEEP_FRAMES=1`). Quits when
/// done.
///
/// `AGENT_SPEED_ORB_CLIPS` (comma separated, default `row-dark,row-light`):
/// - `row-<dark|light>[-<level>][-rm]`: the five styles side by side at 148 pt over the 62 s tour (hover at 50.5 s);
/// - `levels-<level>`: the row at the redline (12 s from 25 s);
/// - `hero-<style>[-light][-<level>][-rm]`: one style at 240 pt climbing to the redline (16 s from 22 s);
/// - `still-<style>-<seconds>[-light]`: a single PNG of one style at a moment of the tour;
/// - `strip-<seconds>[-light][-s<size>]`: a single PNG of the five styles side by side at a moment of the tour, at
///   148 pt or the given size (the smallest orbs of DESIGN 2a);
/// - `fx-<tok/s>[-light][-<level>][-rm][-peak][-zap][-s<size>]`: a single PNG of `led` at R = 2000 tok/s after 6 s at
///   that speed, for side-by-side review with the v1.4 prototype's still frames (`OrbFrameHarness+Led`);
/// - `character[-light][-t<seconds>][-s<size>][-still]`: the `character` style over the tour (`-still`: one PNG at
///   `-t`), with the cards of `CharacterDemoReadings`.
/// `AGENT_SPEED_ORB_FPS` (30) and `AGENT_SPEED_ORB_SECONDS` limit the clips; `AGENT_SPEED_ORB_LANGUAGE` (`zh-Hant`)
/// sets the language. `AGENT_SPEED_ORB_SPEED` holds the tour at that many tok/s; `AGENT_SPEED_ORB_SECONDS` then sets the
/// clip's length.
@MainActor
enum OrbFrameHarness {
    struct Clip {
        enum Kind {
            case row
            case hero(OrbStyleKind)
            case still(OrbStyleKind, Double)
            case strip(Double)
            case fx(LedStill)
            /// The `character` style alone over the tour (or from `-t<seconds>`), its disc 2 S.
            case character
        }

        let name: String
        let kind: Kind
        let dark: Bool
        let level: EffectsLevel
        let reduceMotion: Bool
        let start: Double
        let duration: Double
        /// The row's diameter (`-s<size>`).
        let size: CGFloat

        init?(_ name: String) {
            let parts = name.split(separator: "-").map(String.init)
            guard let first = parts.first else { return nil }
            self.name = name
            dark = !parts.contains("light")
            level = parts.contains("max") ? .max : parts.contains("soft") ? .soft : parts.contains("off") ? .off : .standard
            reduceMotion = parts.contains("rm")
            size = parts.dropFirst().compactMap { $0.hasPrefix("s") ? Double($0.dropFirst()) : nil }.first.map { CGFloat($0) } ?? 148
            switch first {
            case "row":
                kind = .row
                start = 0
                duration = OrbTour.length
            case "levels":
                kind = .row
                start = 25
                duration = 12
            case "hero":
                guard parts.count > 1, let style = OrbStyleKind(rawValue: parts[1]) else { return nil }
                kind = .hero(style)
                start = 22
                duration = 16
            case "still":
                guard parts.count > 2, let style = OrbStyleKind(rawValue: parts[1]), let at = Double(parts[2]) else { return nil }
                kind = .still(style, at)
                start = at
                duration = 0
            case "strip":
                guard parts.count > 1, let at = Double(parts[1]) else { return nil }
                kind = .strip(at)
                start = at
                duration = 0
            case "fx":
                guard let still = LedStill(parts) else { return nil }
                kind = .fx(still)
                start = 0
                duration = 0
            case "character":
                kind = .character
                start = parts.dropFirst().compactMap { $0.hasPrefix("t") ? Double($0.dropFirst()) : nil }.first ?? 0
                duration = parts.contains("still") ? 0 : OrbTour.length - start
            default:
                return nil
            }
        }
    }

    static func run(into directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let environment = ProcessInfo.processInfo.environment
        let names = environment["AGENT_SPEED_ORB_CLIPS"]?.split(separator: ",").map(String.init) ?? ["row-dark", "row-light"]
        let fps = environment["AGENT_SPEED_ORB_FPS"].flatMap(Double.init) ?? 30
        let limit = environment["AGENT_SPEED_ORB_SECONDS"].flatMap(Double.init)
        let keep = environment["AGENT_SPEED_ORB_KEEP_FRAMES"] == "1"
        let suite = "AgentSpeedOrbFrames"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        let language = environment["AGENT_SPEED_ORB_LANGUAGE"].flatMap(LanguagePreference.init(rawValue:)) ?? .zhHant
        model.settings.language = language
        model.l10n.apply(language)
        // The designer's scenarios are written for R = 400 tok/s.
        model.settings.redlineOutput = 400
        var report = ""
        for clip in names.compactMap(Clip.init) {
            if case .fx(let still) = clip.kind {
                let started = Date()
                renderLedStill(still, clip: clip, model: model, to: directory.appendingPathComponent("\(clip.name).png"))
                report += String(format: "%@: 1 frame in %.1f s\n", clip.name, Date().timeIntervalSince(started))
                continue
            }
            let frames = directory.appendingPathComponent(clip.name, isDirectory: true)
            try? FileManager.default.removeItem(at: frames)
            try? FileManager.default.createDirectory(at: frames, withIntermediateDirectories: true)
            let started = Date()
            let held = environment["AGENT_SPEED_ORB_SPEED"] != nil && clip.duration > 0
            let seconds = held ? limit ?? clip.duration : min(limit ?? .infinity, clip.duration)
            let count = record(clip, model: model, fps: fps, seconds: seconds, into: frames, directory: directory)
            report += String(format: "%@: %d frames in %.1f s\n", clip.name, count, Date().timeIntervalSince(started))
            if clip.duration == 0 {
                try? FileManager.default.removeItem(at: frames)
                continue
            }
            if encode(frames: frames, fps: fps, to: directory.appendingPathComponent("\(clip.name).mp4")), !keep {
                try? FileManager.default.removeItem(at: frames)
            }
        }
        try? report.write(to: directory.appendingPathComponent("orb-frames.txt"), atomically: true, encoding: .utf8)
        print(report, terminator: "")
    }

    private struct Cell {
        let stage: OrbPreviewStage
        let center: CGPoint
        let label: String?
    }

    private static func record(_ clip: Clip, model: AppModel, fps: Double, seconds: Double, into frames: URL, directory: URL) -> Int {
        let size: CGSize
        var cells: [Cell] = []
        var castAt: (Double) -> Void = { _ in }
        var demoAt: () -> Void = {}
        func stage(_ style: OrbStyleKind, _ diameter: CGFloat) -> OrbPreviewStage {
            let stage = OrbPreviewStage(model: model, style: style, diameter: diameter, dark: clip.dark, level: clip.level,
                                        reduceMotion: clip.reduceMotion)
            stage.reset(value: 0)
            return stage
        }
        switch clip.kind {
        case .row, .strip:
            let s = clip.size
            let width = max(1.5 * s, 170)
            size = CGSize(width: width * 5, height: 1.5 * s + 46)
            for (index, style) in OrbStyleKind.allCases.enumerated() {
                cells.append(Cell(stage: stage(style, s), center: CGPoint(x: width * (CGFloat(index) + 0.5), y: 0.75 * s + 8),
                                  label: model.l10n(style.titleKey) + "  " + style.rawValue))
            }
        case .fx:
            return 0
        case .character:
            CharacterAssets.shared.synchronous = true
            let disc = 2 * clip.size
            size = CGSize(width: disc * 1.5, height: disc * 1.5)
            let character = stage(.character, disc)
            // `AGENT_SPEED_ORB_CHARACTER` picks the pack (a comma list changes character every
            // `AGENT_SPEED_ORB_CHARACTER_EVERY` seconds, 8 by default) and `AGENT_SPEED_ORB_MODEL` the model chip.
            let environment = ProcessInfo.processInfo.environment
            let installed = model.characters.installed
            let wanted = (environment["AGENT_SPEED_ORB_CHARACTER"] ?? "").split(separator: ",").map(String.init)
            let packs = wanted.compactMap { id in installed.first { $0.id == id } }
            let cast = packs.isEmpty ? Array(installed.prefix(1)) : packs
            let every = environment["AGENT_SPEED_ORB_CHARACTER_EVERY"].flatMap(Double.init) ?? 8
            let modelName = environment["AGENT_SPEED_ORB_MODEL"]
            castAt = { time in
                guard !cast.isEmpty else { return }
                let pack = cast[Int(time / every) % cast.count]
                guard character.feed.characterID != pack.id else { return }
                let vendor = pack.manifest.vendors.first ?? "anthropic"
                character.feed.characterID = pack.id
                character.feed.vendors = VendorReading(rates: [vendor: 1],
                                                       fastestModel: modelName ?? pack.manifest.name.en,
                                                       fastestVendor: vendor)
            }
            castAt(0)
            var readings = CharacterDemoReadings(environment: environment, prices: model.priceBook.current)
            demoAt = {
                if let next = readings.update(value: character.value, time: character.time, display: model.cost) {
                    character.feed.characterReadings = next
                }
            }
            cells.append(Cell(stage: character, center: CGPoint(x: disc * 0.75, y: disc * 0.75), label: nil))
        case .hero(let style), .still(let style, _):
            let big: CGFloat = 240
            size = CGSize(width: big * 1.8, height: big * 1.6)
            cells.append(Cell(stage: stage(style, big), center: CGPoint(x: big * 0.9, y: big * 0.8), label: nil))
        }
        defer { cells.forEach { $0.stage.stop() } }

        let tourHover = switch clip.kind {
        case .row: clip.start == 0
        case .strip: true
        default: false
        }
        var random = OrbRandom(seed: 99)
        var tourTime = 0.0
        let dt = 1.0 / 60
        let hold = ProcessInfo.processInfo.environment["AGENT_SPEED_ORB_SPEED"].flatMap(Double.init)
        func advance() {
            tourTime += dt
            castAt(tourTime)
            demoAt()
            let reference = cells[0].stage
            var fire: (Double, Int)?
            if reference.gauge.tier != .idle, random.next() < (0.3 + 1.5 * reference.gauge.heat(at: reference.time)) * dt {
                fire = (reference.value * random.range(0.5...1.3), Int(random.range(80...6000)))
            }
            for cell in cells {
                if tourHover { cell.stage.hovered = tourTime >= 50.5 && tourTime < 52.6 }
                cell.stage.step(dt) { _ in hold ?? OrbTour.value(at: tourTime.truncatingRemainder(dividingBy: OrbTour.length)) }
                if let fire { cell.stage.animatorPulse(speed: fire.0, output: fire.1) }
                cell.stage.newPeakRule(tourTime: tourTime)
            }
        }
        while tourTime < clip.start - 1e-9 { advance() }
        let total = Int((seconds * fps).rounded())
        let stepsPerFrame = max(1, Int((60 / fps).rounded()))
        var written = 0
        for index in 0..<max(1, total) {
            if total > 0 { for _ in 0..<stepsPerFrame { advance() } }
            guard let image = compose(cells, size: size, dark: clip.dark) else { continue }
            let url: URL
            if clip.duration == 0 {
                url = directory.appendingPathComponent("\(clip.name).png")
            } else {
                url = frames.appendingPathComponent(String(format: "f%05d.png", index))
            }
            PreviewSnapshot.write(image, to: url)
            written += 1
        }
        return written
    }

    private static func compose(_ cells: [Cell], size: CGSize, dark: Bool) -> CGImage? {
        let background = dark ? NSColor(srgbRed: 0x1E / 255, green: 0x1F / 255, blue: 0x24 / 255, alpha: 1)
            : NSColor(srgbRed: 0xE9 / 255, green: 0xEA / 255, blue: 0xEE / 255, alpha: 1)
        guard let canvas = PreviewCanvas(size: size, background: background) else { return nil }
        for cell in cells {
            guard let image = cell.stage.image() else { continue }
            let side = cell.stage.canvasSide
            canvas.draw(image, at: CGPoint(x: cell.center.x - side / 2, y: cell.center.y - side / 2), size: CGSize(width: side, height: side))
            if let label = cell.label {
                canvas.text(label, at: CGPoint(x: cell.center.x, y: size.height - 14), size: 13, weight: .semibold,
                            color: dark ? NSColor(white: 1, alpha: 0.62) : NSColor(white: 0, alpha: 0.58), alignment: .center)
            }
        }
        return canvas.image
    }

    static func encode(frames: URL, fps: Double, to video: URL) -> Bool {
        let candidates = ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg"]
        guard let ffmpeg = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { return false }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: ffmpeg)
        process.arguments = ["-loglevel", "error", "-y", "-framerate", String(Int(fps)), "-i", frames.appendingPathComponent("f%05d.png").path,
                             "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-movflags", "+faststart", video.path]
        do {
            try process.run()
        } catch {
            return false
        }
        process.waitUntilExit()
        return process.terminationStatus == 0
    }
}

extension OrbPreviewStage {
    /// The prototype's new-peak rule for recordings: ≥ 20 tok/s, 1 above the previous peak, 15 s demo cooldown.
    func newPeakRule(tourTime: Double) {
        if value >= 20, value > recordedPeak + 1, tourTime - lastRecordedPeak > 15 {
            animator.celebrate(at: time)
            lastRecordedPeak = tourTime
        }
        recordedPeak = max(recordedPeak, value)
    }
}
