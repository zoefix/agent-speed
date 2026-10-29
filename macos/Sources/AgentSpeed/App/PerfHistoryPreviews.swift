import AgentSpeedCore
import AppKit
import SwiftUI

/// `--render-previews` group `perfhistory`: the Usage History 性能表現 section (DESIGN 12) as the prototype's history
/// section shows it — card widths 932 (wide, a 980 pt window), 692 (medium, 740), 672 (medium, the minimum window) and
/// 392 (narrow, 440) in Chinese, English and Japanese, both appearances; the trend's tooltip on a scored day and on a
/// gap; a single day's 14-day trend; a component sort; the analysing and empty states; Increase Contrast.
@MainActor
enum PerfHistoryPreviews {
    static let widths: [CGFloat] = [932, 692, 672, 392]

    static func render(model: AppModel, into directory: URL) {
        let saved = model.settings.language
        defer {
            model.settings.language = saved
            model.l10n.apply(saved)
            model.settings.storage.removeObject(forKey: HistoryViewModel.perfSortKey)
        }
        for language in [LanguagePreference.zhHant, .en, .ja] {
            let viewModel = prepare(model, language: language)
            let widths = language == .zhHant ? widths : language == .en ? [932, 392] : [692]
            for width in widths {
                for dark in language == .zhHant ? [true, false] : [true] {
                    write(PerfHistorySection(model: viewModel), width: width, model: model, dark: dark,
                          name: "perfhistory-\(Int(width))-\(language.rawValue.lowercased())-\(appearance(dark))", into: directory)
                }
            }
        }
        let viewModel = prepare(model, language: .zhHant)
        for dark in [true, false] {
            write(PerfHistorySection(model: viewModel).environment(\._colorSchemeContrast, .increased), width: 932,
                  model: model, dark: dark, name: "perfhistory-contrast-932-\(appearance(dark))", into: directory)
            writeTooltips(viewModel, model: model, dark: dark, into: directory)
        }
        viewModel.setPerfSort(PerfHistorySort(column: .component(.L), descending: false))
        write(PerfHistorySection(model: viewModel), width: 932, model: model, dark: true,
              name: "perfhistory-sorted-latency-dark", into: directory)
        viewModel.setPerfSort(.standard)
        if let opus = viewModel.perf.rows.first(where: { $0.model == "gpt-6.1" }) {
            viewModel.selectPerf(opus.id)
            write(PerfHistorySection(model: viewModel), width: 692, model: model, dark: false,
                  name: "perfhistory-selected-692-light", into: directory)
        }

        let single = prepare(model, language: .zhHant, range: .yesterday)
        for dark in [true, false] {
            write(PerfHistorySection(model: single), width: 932, model: model, dark: dark,
                  name: "perfhistory-yesterday-932-\(appearance(dark))", into: directory)
        }
        for (name, item) in [("analyzing", HistoryPreviews.Case(name: "perf-analyzing", state: .analyzing(0.36, 112, 304), perf: false)),
                             ("empty", HistoryPreviews.Case(name: "perf-empty", days: .none))] {
            let state = HistoryPreviews.prepare(item, model: model)
            for width in [692, 392] as [CGFloat] {
                for dark in [true, false] {
                    write(PerfHistorySection(model: state), width: width, model: model, dark: dark,
                          name: "perfhistory-\(name)-\(Int(width))-\(appearance(dark))", into: directory)
                }
            }
        }
    }

    private static func prepare(_ model: AppModel, language: LanguagePreference,
                                range: HistoryRangeKind = .days30) -> HistoryViewModel {
        HistoryPreviews.prepare(HistoryPreviews.Case(name: "perf", range: range, language: language), model: model)
    }

    /// The default row's chart with its tooltip on the latest day and on a gap.
    private static func writeTooltips(_ viewModel: HistoryViewModel, model: AppModel, dark: Bool, into directory: URL) {
        let content = viewModel.perf
        guard let row = PerfHistoryContent.selected(nil, in: viewModel.perfSort.sorted(content.rows)) else { return }
        let scored = row.trend.lastIndex { $0.score != nil } ?? 0
        let gap = row.trend.firstIndex { $0.score == nil && $0.n > 0 } ?? 0
        for (name, index) in [("scored", scored), ("gap", gap)] {
            let chart = PerfTrendChart(row: row, days: content.trendDays, singleDay: content.singleDay, height: 150,
                                       l10n: model.l10n, hovered: index)
                .frame(width: 860)
                .padding(18)
                .historyCard()
                .environment(\.locale, model.l10n.locale)
            write(chart, width: nil, model: model, dark: dark, name: "perfhistory-tooltip-\(name)-\(appearance(dark))",
                  into: directory)
        }
    }

    private static func appearance(_ dark: Bool) -> String { dark ? "dark" : "light" }

    private static func write<V: View>(_ view: V, width: CGFloat?, model: AppModel, dark: Bool, name: String,
                                       into directory: URL) {
        let framed = view
            .frame(width: width)
            .padding(HistoryLayout.outerPadding)
            .appEnvironment(model)
        snapshot(framed, dark: dark, to: directory.appendingPathComponent("\(name).png"))
    }

    /// `PreviewSnapshot.writeHosted` with the window resized to the content's fitting size again after the first layout
    /// settles, so text that wraps differently once laid out never leaves a band above or below the section.
    static func snapshot<V: View>(_ view: V, dark: Bool, to url: URL) {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let background = NSColor.windowBackgroundColor
        let host = NSHostingView(rootView: view
            .environment(\.colorScheme, dark ? .dark : .light)
            .background(Color(nsColor: background)))
        host.appearance = appearance
        host.frame = CGRect(origin: .zero, size: host.fittingSize)
        let window = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.appearance = appearance
        window.backgroundColor = background
        window.contentView = host
        defer { PreviewSnapshot.release(window) }
        for _ in 0..<3 {
            host.layoutSubtreeIfNeeded()
            RunLoop.main.run(until: Date().addingTimeInterval(0.08))
            let size = host.fittingSize
            if size != host.frame.size {
                window.setContentSize(size)
                host.frame = CGRect(origin: .zero, size: size)
            }
        }
        host.layoutSubtreeIfNeeded()
        host.displayIfNeeded()
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        guard let image = rep.cgImage else { return }
        PreviewSnapshot.write(image, to: url)
    }
}
