import AgentSpeedCore
import Foundation
import os

struct UpdateConfiguration: Sendable {
    static let bundleIdentifier = Bundle.main.bundleIdentifier!
    static let bundleName = "Agent Speed.app"
    static let assetName = "AgentSpeed-macOS.zip"
    static let checksumAssetName = "SHA256SUMS.txt"
    static let maxAssetSize: Int64 = 300 << 20

    let feedURL: URL
    let policy: UpdateURLPolicy
    let currentVersion: String

    var userAgent: String { "AgentSpeedUpdateIsolation/\(currentVersion)" }

    static func current(environment: [String: String] = ProcessInfo.processInfo.environment) -> UpdateConfiguration {
        UpdateConfiguration(
            feedURL: URL(string: Bundle.main.object(forInfoDictionaryKey: "ASFeedURL") as! String)!,
            policy: .testing,
            currentVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as! String
        )
    }
}

enum UpdateLog {
    static let logger = Logger(subsystem: UpdateConfiguration.bundleIdentifier, category: "update-isolation")
}

struct Localizer {
    func callAsFunction(_ key: String, _ arguments: String...) -> String { key }
}
