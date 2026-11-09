import AgentSpeedCore
import Foundation
import Testing

@Suite struct CharacterAutoDownloadTests {
    static func entry(_ id: String, _ vendors: [String]) -> CharacterCatalog.Entry {
        CharacterCatalog.Entry(id: id, packVersion: 1, name: CharacterNames(zhHant: id, zhHans: id, en: id, ja: id),
                               vendors: vendors, accent: "#888888", size: 10, sha256: String(repeating: "0", count: 64),
                               url: URL(string: "https://github.com/zoefix/agent-speed/releases/download/characters/\(id)-1.zip")!)
    }

    static let catalog = CharacterCatalog(characters: [
        entry("claude", ["anthropic"]), entry("deepseek", ["deepseek"]), entry("gemini", ["google"]),
        entry("gpt", ["openai"]), entry("kimi", ["moonshot"]),
    ])

    @Test func liveVendorsComeFirstThenCatalogOrder() {
        let queue = CharacterAutoDownload.queue(Self.catalog, installed: [], setAside: [], vendors: ["openai", "anthropic"])
        #expect(queue == ["gpt", "claude", "deepseek", "gemini", "kimi"])
    }

    @Test func installedSetAsideAndSkippedPacksStayOut() {
        let queue = CharacterAutoDownload.queue(Self.catalog, installed: ["claude"], setAside: ["gemini"], skipped: ["kimi"],
                                                vendors: ["anthropic"])
        #expect(queue == ["deepseek", "gpt"])
    }

    @Test func vendorsAreOrderedByLiveRate() {
        let reading = VendorReading(rates: ["openai": 40, "anthropic": 120, "google": 0, "moonshot": 40],
                                    fastestModel: nil, fastestVendor: "moonshot")
        #expect(CharacterAutoDownload.vendors(reading) == ["anthropic", "moonshot", "openai"])
    }
}
