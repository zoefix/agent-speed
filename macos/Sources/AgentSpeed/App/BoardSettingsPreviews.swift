import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// `--render-previews` group `boards`: Settings → 懸浮窗's Boards list and editor (empty and with three boards), the
/// boards' menus as they are built (context, quick and the orb menu's add), and each style's content for the live
/// feed's tick, light and dark. Nothing touches the user's boards: the preview model has its own settings suite.
@MainActor
enum BoardSettingsPreviews {
    static func render(model: AppModel, into directory: URL) {
        PreviewRenderer.publish(model, speed: model.settings.redline * 0.55, agents: .grouped)
        settle()
        writeSettings(model, name: "empty", into: directory)

        let boards = model.boards
        guard let neon = boards.add(), let glass = boards.add(), let timing = boards.add() else { return }
        boards.setStyle(.neon, of: neon)
        boards.setStyle(.glass, of: glass)
        boards.setSources(.explicit([.claudeCode, .codex]), of: glass)
        boards.setSize(.large, of: glass)
        boards.setOpacity(0.85, of: glass)
        boards.setStyle(.timing, of: timing)
        boards.setSources(.explicit([.codex]), of: timing)
        boards.setSize(.small, of: timing)
        boards.setVisible(false, of: timing)
        // The glass board's editor shows independent switches: 思考 off.
        boards.setReadouts(BoardReadouts(intelligence: true, thinking: false, cost: true), of: glass)
        boards.settingsSelection = glass
        for step in 1...3 {
            PreviewRenderer.publish(model, snapshot: PreviewData.snapshot(
                speed: model.settings.redline * (0.5 + 0.05 * Double(step)), redline: model.settings.redline,
                agents: .grouped, now: Date().addingTimeInterval(Double(step) * 0.5)))
            settle()
        }
        writeSettings(model, name: "list", into: directory)

        for dark in [false, true] {
            let scheme = dark ? "dark" : "light"
            let sheets: [(String, NSMenu)] = [
                ("context", BoardMenus.contextMenu(model: model, boardId: glass)),
                ("quick", MenuFactory.quickMenu(model: model)),
            ]
            for (name, menu) in sheets {
                PreviewSnapshot.writeHosted(MenuSheet(menu: menu).padding(16), dark: dark,
                                            to: directory.appendingPathComponent("boards-menu-\(name)-\(scheme).png"))
            }
            if let orb = model.orbs.orbs.first?.id {
                PreviewSnapshot.writeHosted(MenuSheet(menu: OrbMenus.contextMenu(model: model, orbId: orb), expands: false)
                    .padding(16), dark: dark, to: directory.appendingPathComponent("boards-menu-orb-\(scheme).png"))
            }
        }
        for id in [neon, glass, timing] {
            guard let runtime = boards.runtime(id), let tick = runtime.tick else { continue }
            for dark in [false, true] {
                writeContent(tick, config: runtime.config, dark: dark, model: model,
                             to: directory.appendingPathComponent("boards-live-\(runtime.config.style.rawValue)-\(dark ? "dark" : "light").png"))
            }
        }
        writeMetricTexts(model, ids: [neon, glass, timing], into: directory)
        for id in [neon, glass, timing] { boards.remove(id) }
    }

    /// The live metrics' words (DESIGN 2c Live metrics, *Tooltip and score card* and *Accessibility*): each board's top
    /// group's score card (its text tooltip with `intelligence` off), and every board's and row's accessibility label
    /// and the card's lines in a text file.
    private static func writeMetricTexts(_ model: AppModel, ids: [String], into directory: URL) {
        var text = ""
        for id in ids {
            guard let runtime = model.boards.runtime(id), let tick = runtime.tick else { continue }
            let config = runtime.config
            let currency = model.cost.currencyName
            text += "[\(config.style.rawValue) \(config.size.rawValue)]\n"
            text += BoardAccessibilityReporter.join([model.l10n("a11y.board", model.boards.sourcesText(of: config, l10n: model.l10n),
                                                                tick.valueText, tick.title)]
                + BoardAccessibilityReporter.metricWords(tick.metrics, currency: currency, l10n: model.l10n), model.l10n) + "\n"
            for (rank, group) in tick.groups.prefix(3).enumerated() {
                text += "  " + BoardAccessibilityReporter.rowLabel(group, rank: rank + 1, l10n: model.l10n,
                                                                    intelligence: config.readouts.intelligence) + "\n"
            }
            guard let group = tick.groups.first else { continue }
            let name = "\(config.style.rawValue)"
            if config.readouts.intelligence {
                let card = BoardScoreCards.group(group, l10n: model.l10n)
                text += "  card: " + PerfCardText.accessibilityLines(card, model.l10n).joined(separator: " | ") + "\n"
                for dark in [false, true] {
                    PreviewSnapshot.writeHosted(PerfCardView(model: card, l10n: model.l10n).modifier(PerfCardSolidSurface())
                        .padding(12), dark: dark,
                                                to: directory.appendingPathComponent("boards-card-\(name)-\(dark ? "dark" : "light").png"))
                }
                continue
            }
            let lines = BoardTooltip.lines(group, metric: tick.metric, l10n: model.l10n)
            text += "  tooltip: " + lines.joined(separator: " | ") + "\n"
            for dark in [false, true] {
                PreviewSnapshot.writeHosted(BoardTooltipView(lines: lines), dark: dark,
                                            to: directory.appendingPathComponent("boards-tooltip-\(name)-\(dark ? "dark" : "light").png"))
            }
        }
        try? text.write(to: directory.appendingPathComponent("boards-a11y.txt"), atomically: true, encoding: .utf8)
    }

    private static func settle() {
        RunLoop.main.run(until: Date().addingTimeInterval(0.05))
    }

    private static func writeSettings(_ model: AppModel, name: String, into directory: URL) {
        for dark in [false, true] {
            PreviewSnapshot.writeHosted(
                Form { BoardsSettings(model: model) }
                    .settingsForm()
                    .frame(width: SettingsPaneView.width(for: .orb))
                    .fixedSize(horizontal: false, vertical: true)
                    .appEnvironment(model)
                    .environment(\.orbStillTime, Clock.now),
                dark: dark,
                to: directory.appendingPathComponent("boards-settings-\(name)-\(dark ? "dark" : "light").png")
            )
        }
    }

    /// The tick drawn by its style's content builder, composited as the board's layers would be.
    private static func writeContent(_ tick: BoardTick, config: BoardConfig, dark: Bool, model: AppModel, to url: URL) {
        let side = CGFloat(config.size.side)
        let builder = BoardContentBuilders.make(style: config.style, size: config.size, dark: dark, l10n: model.l10n, capsule: nil)
        let layout = builder.layout(tick)
        let scale: CGFloat = 2
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let context = CGContext(data: nil, width: Int(side * scale), height: Int(side * scale), bitsPerComponent: 8,
                                      bytesPerRow: 0, space: space,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
        context.scaleBy(x: scale, y: scale)
        context.translateBy(x: 0, y: side)
        context.scaleBy(x: 1, y: -1)
        let corner = CGFloat(config.style.cornerRadius)
        context.addPath(CGPath(roundedRect: CGRect(x: 0, y: 0, width: side, height: side), cornerWidth: corner,
                               cornerHeight: corner, transform: nil))
        context.setFillColor(BoardHostView.slabColor(config.style, dark: dark).opacity(1).cgColor)
        context.fillPath()
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
        for item in layout.items {
            context.saveGState()
            let shown = item.shownFrame
            context.translateBy(x: shown.minX, y: shown.minY)
            context.scaleBy(x: item.frame.width > 0 ? shown.width / item.frame.width : 1, y: 1)
            if let plate = item.plate {
                context.addPath(CGPath(roundedRect: CGRect(origin: .zero, size: item.frame.size), cornerWidth: plate.cornerRadius,
                                       cornerHeight: plate.cornerRadius, transform: nil))
                context.setFillColor(plate.color)
                context.fillPath()
            }
            item.composited(context, item.frame.size)
            context.restoreGState()
        }
        NSGraphicsContext.restoreGraphicsState()
        guard let image = context.makeImage() else { return }
        PreviewSnapshot.write(image, to: url)
    }
}

/// A native menu drawn as a sheet for review: every item with its check, image, title (attributed when set),
/// key equivalent and enabled state; submenus listed indented below their item.
private struct MenuSheet: View {
    let menu: NSMenu
    var expands = true

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            rows(menu, depth: 0)
        }
        .padding(.vertical, 5)
        .frame(width: 320, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.regularMaterial))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(.separator, lineWidth: 0.5))
    }

    private func rows(_ menu: NSMenu, depth: Int) -> AnyView {
        AnyView(ForEach(Array(menu.items.enumerated()), id: \.offset) { _, item in
            if item.isSeparatorItem {
                Divider().padding(.vertical, 4).padding(.horizontal, 10)
            } else {
                row(item, depth: depth)
                if expands, let submenu = item.submenu { rows(submenu, depth: depth + 1) }
            }
        })
    }

    private func row(_ item: NSMenuItem, depth: Int) -> some View {
        HStack(spacing: 6) {
            Text(item.state == .on ? "✓" : "").frame(width: 12)
            if let image = item.image {
                Image(nsImage: image)
            }
            Text(item.attributedTitle.map { AttributedString($0) } ?? AttributedString(item.title))
                .lineLimit(1)
            Spacer(minLength: 8)
            if item.submenu != nil {
                Image(systemName: "chevron.right").font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
            } else if !item.keyEquivalent.isEmpty {
                Text("⌘" + item.keyEquivalent.uppercased()).foregroundStyle(.secondary)
            }
            if let tip = item.toolTip {
                Text("(\(tip))").font(.system(size: 10)).foregroundStyle(.tertiary).lineLimit(1)
            }
        }
        .font(.system(size: 13))
        .foregroundStyle(item.isEnabled ? AnyShapeStyle(.primary) : AnyShapeStyle(.tertiary))
        .padding(.leading, 10 + CGFloat(depth) * 18)
        .padding(.trailing, 12)
        .frame(height: 22)
    }
}
