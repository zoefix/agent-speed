import AgentSpeedCore
import AppKit
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 10, Menu bar and taskbar, *Quota*: the quota block's two lines in QUOTAS 2 order, used or remaining percents,
/// the one-window and unavailable forms, red from 95 % used, pale when old or signed out, the `≈` of an extrapolated
/// percent, its cells, words and tooltip, the `settings.bar.quota` choice, and the block after the other blocks.
@Suite("Menu bar quota block")
@MainActor
struct StatusItemQuotaTests {
    static let now: Int64 = 1_794_338_773_000
    static let minute: Int64 = 60_000
    static let hour: Int64 = 3_600_000

    static func snapshot(source: SourceKind = .codex, observed: Int64 = now - 2 * minute,
                         _ windows: [QuotaWindow]) -> QuotaSnapshot {
        QuotaSnapshot(source: source, observedMs: observed, windows: windows)
    }

    static let codex = snapshot([
        QuotaWindow(kind: .sevenDay, usedPercent: 42, resetsAtMs: now + (2 * 24 + 5) * hour),
        QuotaWindow(kind: .fiveHour, usedPercent: 68.4, resetsAtMs: now + (3 * 60 + 12) * minute),
    ])

    static func block(_ snapshot: QuotaSnapshot?, source: SourceKind = .codex, read: Bool = true,
                      percent: BarQuotaPercent = .used, language: LanguagePreference = .zhHant,
                      estimate: @escaping (QuotaWindow) -> QuotaEstimateDisplay? = { _ in nil },
                      status: QuotaRouteStatus? = nil, statusText: String? = nil) -> StatusItemQuota {
        StatusItemQuota(source: source, read: read, snapshot: snapshot, estimate: estimate, status: status,
                        statusText: statusText, percent: percent, nowMs: now, l10n: Localizer(preference: language))
    }

    private static func textWidth(_ text: String) -> CGFloat {
        ceil((text as NSString).size(withAttributes: [.font: StatusItemQuota.font]).width)
    }

    // MARK: Lines

    @Test func theFirstTwoWindowsInQuotaOrderShowTheirUsedPercents() {
        let block = Self.block(Self.codex)
        #expect(block.form == .shown)
        #expect(block.lines == [.init(label: "5h", number: "68%", ink: .number), .init(label: "7d", number: "42%", ink: .number)])
        #expect(!block.alone)
    }

    @Test func remainingIsAHundredLessTheShownInteger() {
        let block = Self.block(Self.snapshot([
            QuotaWindow(kind: .fiveHour, usedPercent: 68.4, resetsAtMs: nil),
            QuotaWindow(kind: .sevenDay, usedPercent: 23.5, resetsAtMs: nil),
        ]), percent: .remaining)
        #expect(block.lines.map(\.number) == ["32%", "76%"], "68 and 24 used")
        let exhausted = Self.block(Self.snapshot([QuotaWindow(kind: .sevenDay, usedPercent: 103, resetsAtMs: nil)]),
                                   percent: .remaining)
        #expect(exhausted.lines.map(\.number) == ["0%"])
    }

    @Test func moreWindowsShowTheFirstTwoAndSpeakEveryOne() {
        let cli = Self.snapshot(source: .claudeCode, [
            QuotaWindow(kind: .modelWeek, name: "Fable", usedPercent: 12, resetsAtMs: nil),
            QuotaWindow(kind: .sevenDay, usedPercent: 40, resetsAtMs: nil),
            QuotaWindow(kind: .fiveHour, usedPercent: 20, resetsAtMs: nil),
        ])
        let block = Self.block(cli, source: .claudeCode)
        #expect(block.lines.map(\.label) == ["5h", "7d"])
        #expect(block.spoken == "Claude Code 額度：5h 已用 20%、7d 已用 40%、Fable 7d 已用 12%")
        #expect(block.tooltip.count == 4, "the header and every window")
        #expect(block.tooltip.dropFirst().elementsEqual(["5h 已用 20%", "7d 已用 40%", "Fable 7d 已用 12%"]))
    }

    @Test func oneWindowStandsAloneCentredInTheBlock() {
        let weekly = Self.block(Self.snapshot([QuotaWindow(kind: .sevenDay, usedPercent: 25, resetsAtMs: nil)]))
        let pair = Self.block(Self.codex)
        #expect(weekly.alone)
        #expect(weekly.lines == [.init(label: "7d", number: "25%", ink: .number)])
        for height in [22, 24, 37] as [CGFloat] {
            let lines = pair.baselines(height: height)
            #expect(weekly.baselines(height: height).first == (lines.first + lines.second) / 2)
        }
    }

    // MARK: Colors

    @Test func fromNinetyFivePercentUsedTheNumberIsRed() {
        func ink(_ used: Double, _ percent: BarQuotaPercent = .used) -> StatusItemQuota.Ink? {
            Self.block(Self.snapshot([QuotaWindow(kind: .fiveHour, usedPercent: used, resetsAtMs: nil)]), percent: percent)
                .lines.first?.ink
        }
        #expect(ink(94.4) == .number)
        #expect(ink(94.5) == .critical, "rounded half up to 95")
        #expect(ink(100) == .critical)
        #expect(ink(97, .remaining) == .critical, "3 % remaining")
        #expect(Self.block(Self.snapshot([QuotaWindow(kind: .fiveHour, usedPercent: 97, resetsAtMs: nil)]), percent: .remaining)
            .lines.first?.number == "3%")
    }

    @Test func anHourOldWindowIsPaleAndSaysHowOld() {
        let old = Self.block(Self.snapshot(observed: Self.now - 61 * Self.minute, [
            QuotaWindow(kind: .fiveHour, usedPercent: 97, resetsAtMs: nil),
        ]))
        #expect(old.lines.first?.ink == .pale, "pale wins over red")
        #expect(old.spoken == "Codex 額度：5h 已用 97% · 1 小時前更新")
        // A joined snapshot's window keeps its own time (QUOTAS 2): only the old one is pale.
        let joined = Self.block(Self.snapshot(source: .claudeCode, observed: Self.now - Self.minute, [
            QuotaWindow(kind: .fiveHour, usedPercent: 20, resetsAtMs: nil),
            QuotaWindow(kind: .sevenDay, usedPercent: 40, resetsAtMs: nil, observedMs: Self.now - 3 * Self.hour),
        ]), source: .claudeCode)
        #expect(joined.lines.map(\.ink) == [.number, .pale])
        #expect(joined.tooltip.last == "7d 已用 40% · 3 小時前更新")
    }

    @Test func aSignInThatNoLongerWorksMakesEveryNumberPale() {
        let snapshot = Self.snapshot(source: .kimi, [
            QuotaWindow(kind: .fiveHour, usedPercent: 25, resetsAtMs: nil),
            QuotaWindow(kind: .sevenDay, usedPercent: 50, resetsAtMs: nil),
        ])
        for status in [QuotaRouteStatus.noCredential, .noBrowser, .expired, .denied(host: "api.kimi.com")] {
            let block = Self.block(snapshot, source: .kimi, status: status, statusText: "找不到登入資料")
            #expect(block.lines.allSatisfy { $0.ink == .pale }, "\(status)")
            #expect(block.tooltip.last == "找不到登入資料")
            #expect(block.spoken == "Kimi Code CLI 額度：5h 已用 25%、7d 已用 50%、找不到登入資料", "the pale numbers are said why")
        }
        let failed = Self.block(snapshot, source: .kimi, status: .failed, statusText: "上次查詢失敗，稍後重試")
        #expect(failed.lines.allSatisfy { $0.ink == .number }, "a failed request leaves the last values as they are")
        #expect(failed.tooltip.last == "上次查詢失敗，稍後重試")
    }

    // MARK: Unavailable

    @Test func offAndNoneReadADashNeverAZero() {
        let off = Self.block(Self.codex, read: false)
        #expect(off.form == .off)
        #expect(off.lines == [.init(label: "額度", number: "–", ink: .pale)])
        #expect(off.lines.first?.number.unicodeScalars.map(\.value) == [0x2013])
        #expect(off.alone)
        #expect(off.spoken == "Codex：額度已關閉（資料來源）")
        #expect(off.tooltip == ["Codex：額度已關閉（資料來源）"])
        let none = Self.block(nil, source: .gemini, status: .noCredential, statusText: "找不到登入資料")
        #expect(none.form == .none)
        #expect(none.lines == [.init(label: "額度", number: "–", ink: .pale)])
        #expect(none.spoken == "Gemini CLI：尚無額度資料、找不到登入資料")
        #expect(none.tooltip == ["Gemini CLI：尚無額度資料", "找不到登入資料"])
        #expect(Self.block(Self.snapshot([]), read: true).form == .none, "a snapshot without a window")
        let english = Self.block(nil, read: false, language: .en)
        #expect(english.lines.first?.label == "Quota")
        #expect(english.spoken == "Codex: quota is off (Data Sources)")
        #expect(Self.block(nil, language: .ja).lines.first?.label == "利用枠")
    }

    // MARK: Estimates

    @Test func anExtrapolatedPercentKeepsItsMarkAndWidensOnlyAtOneHundred() {
        func estimate(_ percent: Double) -> (QuotaWindow) -> QuotaEstimateDisplay? {
            { window in
                window.kind == .fiveHour
                    ? QuotaEstimateDisplay(percent: percent, estimated: true, resetsMs: window.resetsAtMs, usedMicroUsd: 0,
                                           confidence: .high)
                    : nil
            }
        }
        let block = Self.block(Self.codex, estimate: estimate(71.2))
        #expect(block.lines.first == .init(label: "5h", number: "71%", approximate: true, ink: .number))
        #expect(block.lines.last?.approximate == false)
        #expect(block.spoken.hasPrefix("Codex 額度：5h 已用 71%，3 小時 12 分後重置、估計值、7d 已用 42%，"))
        #expect(block.numberCell == Self.textWidth("100%"), "`≈71%` fits the usual cell")
        let remaining = Self.block(Self.codex, percent: .remaining, estimate: estimate(71.2))
        #expect(remaining.lines.first?.number == "29%" && remaining.lines.first?.approximate == true)
        let full = Self.block(Self.codex, estimate: estimate(99.6))
        #expect(full.lines.first?.number == "100%" && full.lines.first?.ink == .critical)
        #expect(full.numberCell == Self.textWidth("≈100%"), "`≈100%` widens the cell")
    }

    // MARK: Words and tooltip

    @Test func theWordsNameTheSourceAndEveryWindowWithItsReset() {
        let block = Self.block(Self.codex)
        #expect(block.spoken.hasPrefix("Codex 額度：5h 已用 68%，3 小時 12 分後重置、7d 已用 42%，"))
        #expect(block.spoken.hasSuffix(" 重置"))
        #expect(block.tooltip.count == 3)
        #expect(block.tooltip[0].hasPrefix("Codex 額度 · 更新於 "))
        #expect(block.tooltip[1] == "5h 已用 68%，3 小時 12 分後重置")
        let remaining = Self.block(Self.codex, percent: .remaining)
        #expect(remaining.tooltip[1] == "5h 剩餘 32%，3 小時 12 分後重置")
        let english = Self.block(Self.codex, percent: .remaining, language: .en)
        #expect(english.spoken.hasPrefix("Codex quota: 5h 32% left, resets in 3 h 12 min, 7d 58% left, resets "))
        #expect(english.tooltip[0].hasPrefix("Codex quota · Updated "))
        #expect(Self.block(Self.codex, language: .ja).tooltip[1] == "5h 68% 使用、3時間12分後にリセット")
    }

    // MARK: Cells

    @Test func theCellsKeepOneWidthWhateverThePercents() {
        var widths = Set<CGFloat>()
        for used in stride(from: 0.0, through: 100, by: 1) {
            let block = Self.block(Self.snapshot([
                QuotaWindow(kind: .fiveHour, usedPercent: used, resetsAtMs: nil),
                QuotaWindow(kind: .sevenDay, usedPercent: 100 - used, resetsAtMs: nil),
            ]))
            widths.insert(block.width)
        }
        #expect(widths.count == 1, "\(widths)")
        let block = Self.block(Self.codex)
        #expect(block.labelCell == max(Self.textWidth("5h"), Self.textWidth("7d")))
        #expect(block.numberCell == Self.textWidth("100%"))
        #expect(block.width == StatusItemQuota.gap + block.labelCell + StatusItemQuota.labelGap + block.numberCell)
        #expect(block.width == 50, "DESIGN 10: 50 pt with 5h / 7d in SF Pro 9 pt")
        let named = Self.block(Self.snapshot(source: .antigravity, [
            QuotaWindow(kind: .sevenDay, name: "Gemini", usedPercent: 5, resetsAtMs: nil),
            QuotaWindow(kind: .sevenDay, name: "Claude/GPT", usedPercent: 0, resetsAtMs: nil),
        ]), source: .antigravity)
        #expect(named.lines.map(\.label) == ["Gemini 7d", "Claude/GPT 7d"])
        #expect(named.labelCell == Self.textWidth("Claude/GPT 7d"))
    }

    // MARK: Drawing

    private static func pixels(_ block: StatusItemQuota, dark: Bool, height: CGFloat = 22) -> [UInt8] {
        let scale: CGFloat = 2
        let width = Int(ceil(block.width * scale)), rows = Int(height * scale)
        var bytes = [UInt8](repeating: 0, count: width * rows * 4)
        bytes.withUnsafeMutableBytes { buffer in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: rows, bitsPerComponent: 8,
                                          bytesPerRow: width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
            context.translateBy(x: 0, y: CGFloat(rows))
            context.scaleBy(x: scale, y: -scale)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
            block.draw(at: 0, height: height, palette: StatusItemPalette(isDark: dark, increasedContrast: false))
            NSGraphicsContext.restoreGraphicsState()
        }
        return bytes
    }

    @Test func aCriticalNumberIsDrawnRedAndAPaleOneFainter() {
        let critical = Self.block(Self.snapshot([QuotaWindow(kind: .fiveHour, usedPercent: 98, resetsAtMs: nil)]))
        let pale = Self.block(Self.snapshot(observed: Self.now - 2 * Self.hour, [
            QuotaWindow(kind: .fiveHour, usedPercent: 98, resetsAtMs: nil),
        ]))
        let fresh = Self.block(Self.snapshot([QuotaWindow(kind: .fiveHour, usedPercent: 48, resetsAtMs: nil)]))
        for dark in [false, true] {
            // Only the number cell: the label is secondary in every form.
            func numberPixels(_ block: StatusItemQuota) -> [(r: Int, g: Int, a: Int)] {
                let bytes = Self.pixels(block, dark: dark)
                let width = Int(ceil(block.width * 2))
                let start = Int((block.width - block.numberCell) * 2)
                return stride(from: 0, to: bytes.count, by: 4).compactMap { index in
                    index / 4 % width >= start ? (Int(bytes[index]), Int(bytes[index + 1]), Int(bytes[index + 3])) : nil
                }
            }
            #expect(numberPixels(critical).contains { $0.a > 120 && $0.r > $0.g + 80 }, "red on \(dark ? "dark" : "light")")
            #expect(!numberPixels(fresh).contains { $0.a > 120 && $0.r > $0.g + 80 })
            let freshAlpha = numberPixels(fresh).map(\.a).max() ?? 0
            let paleAlpha = numberPixels(pale).map(\.a).max() ?? 0
            #expect(paleAlpha < freshAlpha, "\(paleAlpha) vs \(freshAlpha)")
        }
    }

    @Test func theRedKeepsFourAndAHalfToOneOnALightBar() {
        let red = StatusItemPalette(isDark: false, increasedContrast: false).critical
        func linear(_ v: Double) -> Double { v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4) }
        let text = 0.2126 * linear(red.red) + 0.7152 * linear(red.green) + 0.0722 * linear(red.blue)
        let bar = linear(236.0 / 255)
        #expect((bar + 0.05) / (text + 0.05) >= 4.5, "the light bar is #ECECEC")
        #expect(StatusItemPalette(isDark: true, increasedContrast: false).critical == QuotaLevel.red(dark: true))
    }

    // MARK: Settings

    private static func defaults() throws -> (UserDefaults, String) {
        let suite = "StatusItemQuotaTests.\(UUID().uuidString)"
        return (try #require(UserDefaults(suiteName: suite)), suite)
    }

    @Test func offAndUsedByDefaultStoredChoicesReadBackAndUnknownReadsAsOff() throws {
        let (defaults, suite) = try Self.defaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        let fresh = AppSettings(defaults: defaults)
        #expect(fresh.barQuota == nil)
        #expect(fresh.barQuotaPercent == .used)
        for (stored, choice) in [("off", SourceKind?.none), ("codex", .codex), ("claudeCode", .claudeCode),
                                 ("qoderCli", .qoderCli), ("workbuddy", .workbuddy), ("opencode", nil), ("sparkles", nil),
                                 ("", nil)] {
            defaults.set(stored, forKey: "barQuota")
            #expect(AppSettings(defaults: defaults).barQuota == choice, "\(stored)")
        }
        defaults.set("half", forKey: "barQuotaPercent")
        #expect(AppSettings(defaults: defaults).barQuotaPercent == .used)
        let settings = AppSettings(defaults: defaults)
        settings.barQuota = .antigravity
        settings.barQuotaPercent = .remaining
        #expect(defaults.string(forKey: "barQuota") == "antigravity")
        #expect(defaults.string(forKey: "barQuotaPercent") == "remaining")
        let reread = AppSettings(defaults: defaults)
        #expect(reread.barQuota == .antigravity && reread.barQuotaPercent == .remaining)
        reread.barQuota = nil
        #expect(defaults.string(forKey: "barQuota") == "off")
    }

    @Test func theControlsReadAsSpecified() {
        let l10n = Localizer(preference: .zhHant)
        #expect(l10n("settings.bar.quota") == "顯示額度")
        #expect(BarQuotaPercent.allCases.map { l10n($0.titleKey) } == ["已用", "剩餘"])
        #expect(QuotaController.sources.count == 13, "every quota source is offered")
    }

    // MARK: With the other blocks

    private static func model() -> AppModel {
        _ = NSApplication.shared
        let suite = "StatusItemQuotaTests.model.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.removePersistentDomain(forName: suite)
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        model.settings.language = .zhHant
        model.l10n.apply(.zhHant)
        model.settings.trafficEnabled = true
        model.settings.barMode = .glyphValue
        var snapshot = model.snapshot
        snapshot.codexQuota = QuotaSnapshot(source: .codex, observedMs: QuotaController.nowMs(), windows: [
            QuotaWindow(kind: .fiveHour, usedPercent: 68, resetsAtMs: nil),
            QuotaWindow(kind: .sevenDay, usedPercent: 42, resetsAtMs: nil),
        ])
        model.store.publishLive(snapshot)
        return model
    }

    @Test func theQuotaBlockComesLastAndAddsItsWidth() throws {
        let model = Self.model()
        model.settings.barTraffic = .machine
        model.settings.barIntelligence = true
        let before = StatusItemState(model: model, time: 0)
        #expect(before.quota == nil)
        model.settings.barQuota = .codex
        let after = StatusItemState(model: model, time: 0)
        let quota = try #require(after.quota)
        #expect(quota.lines.map(\.number) == ["68%", "42%"])
        #expect(abs(after.layout.length - before.layout.length - quota.width) < 0.001)
        #expect(after.layout.readoutX < after.layout.intelligenceX && after.layout.intelligenceX < after.layout.quotaX)
        #expect(abs(after.layout.length - StatusItemLayout.padding - after.layout.quotaX - quota.width) < 0.001)
        #expect(abs(after.layout.quotaX - after.layout.intelligenceX - after.layout.intelligenceWidth) < 0.001)
        // The governors' rooms only widen it.
        let held = StatusItemState(model: model, time: 0, quotaCells: (40, 40))
        #expect(held.quota?.labelCell == 40 && held.quota?.numberCell == 40)
    }

    @Test func glyphOnlyHidesTheBlockAndStopsAskingForIt() {
        let model = Self.model()
        model.settings.barQuota = .kimi
        #expect(model.quota.barSource == .kimi)
        model.settings.barMode = .glyph
        #expect(StatusItemState(model: model, time: 0).quota == nil)
        #expect(model.quota.barSource == nil)
        model.settings.barMode = .value
        #expect(StatusItemState(model: model, time: 0).quota != nil)
        // A source whose quota switch is off stays chosen and reads `額度 –`.
        model.settings.barQuota = .codex
        model.settings.quotaCodex = false
        let off = StatusItemState(model: model, time: 0).quota
        #expect(off?.form == .off)
        #expect(off?.lines.first?.number == "–")
    }
}
