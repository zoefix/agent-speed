import Foundation

/// Developer command-line flags.
struct LaunchOptions: Sendable {
    var demo = false
    var previewDirectory: URL?
    /// `--orb-frames DIR`: records floating orbs frame by frame, see `OrbFrameHarness`.
    var orbFramesDirectory: URL?
    /// `--orb-bench DIR`: measures the floating orbs' CPU and GPU time on screen, see `OrbBenchmark`.
    var orbBenchDirectory: URL?
    /// `--board-frames DIR`: offscreen stills of the board effects, see `BoardFrameHarness`.
    var boardFramesDirectory: URL?
    /// `--board-bench DIR`: measures a board's CPU, frame rate and GPU time on screen, see `BoardBenchmark`.
    var boardBenchDirectory: URL?
    /// `--board-demo [glass|timing|neon] [S|M|L]`: one board fed by the prototype simulation, see `BoardDemoHarness`.
    var boardDemo: BoardDemoOptions?
    /// `--check-updates-now`: run a manual update check right after launch.
    var checkUpdatesNow = false
    /// `--install-update-now`: check, then install an available update without waiting for the button.
    var installUpdateNow = false
    #if DEBUG
    /// `--motion-harness DIR`: records the agents list expanding and collapsing, see `MotionHarness`.
    var motionHarnessDirectory: URL?
    /// `--screenshots DIR`: shows every surface with sample data on screen and captures it, see `ScreenshotHarness`.
    /// `--history-screenshots DIR` captures only the history window.
    var screenshotsDirectory: URL?
    var screenshotsOnlyHistory = false
    /// `--hero-frames DIR`: records the dashboard hero vehicle frame by frame, see `HeroFrameHarness`.
    var heroFramesDirectory: URL?
    /// `--hero-cpu DIR`: measures the app's CPU with the dashboard open and closed, see `HeroLiveHarness`.
    var heroCPUDirectory: URL?
    /// `--orbs-demo DIR`: shows three orbs, drags, splits and merges them and captures the region, see `OrbsDemoHarness`.
    var orbsDemoDirectory: URL?
    /// `--nameplates-demo DIR`: four orbs with name plates on the desktop, see `NameplatesDemoHarness`.
    var nameplatesDemoDirectory: URL?
    /// `--report-e2e URL`: live reporting against a local reference server, see `ReportHarness`.
    var reportEndToEndURL: String?
    /// `--lan-e2e`: the local network feed on synthetic values, see `LanHarness`.
    var lanEndToEnd = false
    #endif

    static func parse(_ arguments: [String] = CommandLine.arguments) -> LaunchOptions {
        var options = LaunchOptions()
        var iterator = arguments.dropFirst().makeIterator()
        while let argument = iterator.next() {
            switch argument {
            case "--demo":
                options.demo = true
            case "--check-updates-now":
                options.checkUpdatesNow = true
            case "--install-update-now":
                options.installUpdateNow = true
            case "--orb-frames":
                if let path = iterator.next() {
                    options.orbFramesDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--orb-bench":
                if let path = iterator.next() {
                    options.orbBenchDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--board-frames":
                if let path = iterator.next() {
                    options.boardFramesDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--board-bench":
                if let path = iterator.next() {
                    options.boardBenchDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--board-demo":
                options.boardDemo = BoardDemoOptions()
            case _ where options.boardDemo?.accepts(argument) == true:
                options.boardDemo?.take(argument)
            case "--render-previews":
                if let path = iterator.next() {
                    options.previewDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            #if DEBUG
            case "--motion-harness":
                if let path = iterator.next() {
                    options.motionHarnessDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--hero-cpu":
                if let path = iterator.next() {
                    options.heroCPUDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--orbs-demo":
                if let path = iterator.next() {
                    options.orbsDemoDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--nameplates-demo":
                if let path = iterator.next() {
                    options.nameplatesDemoDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--hero-frames":
                if let path = iterator.next() {
                    options.heroFramesDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                }
            case "--report-e2e":
                options.reportEndToEndURL = iterator.next()
            case "--lan-e2e":
                options.lanEndToEnd = true
            case "--screenshots", "--history-screenshots":
                if let path = iterator.next() {
                    options.screenshotsDirectory = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
                    options.screenshotsOnlyHistory = argument == "--history-screenshots"
                }
            #endif
            default:
                continue
            }
        }
        return options
    }
}


/// The style and size words after `--board-demo`.
struct BoardDemoOptions: Sendable {
    var style: String?
    var size: String?

    func accepts(_ word: String) -> Bool {
        (style == nil && ["glass", "timing", "neon"].contains(word)) || (size == nil && ["S", "M", "L"].contains(word))
    }

    mutating func take(_ word: String) {
        if ["S", "M", "L"].contains(word) { size = word } else { style = word }
    }
}
