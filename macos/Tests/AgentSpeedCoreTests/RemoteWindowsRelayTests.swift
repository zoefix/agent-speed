import Foundation
import Testing
@testable import AgentSpeedCore

#if DEBUG
@Suite("Windows SSH relay opt-in")
struct RemoteWindowsRelayTests {
    @Test func commandRequiresTheExactHostAndAValidDigest() throws {
        let digest = String(repeating: "a", count: 64)
        let environment = [RemoteWindowsRelay.hostVariable: "windows-box", RemoteWindowsRelay.digestVariable: digest]
        #expect(RemoteWindowsRelay.command(destination: "linux-box", environment: environment) == nil)
        #expect(RemoteWindowsRelay.command(destination: "windows-box", environment: [
            RemoteWindowsRelay.hostVariable: "windows-box", RemoteWindowsRelay.digestVariable: "not-a-digest",
        ]) == nil)
        let command = try #require(RemoteWindowsRelay.command(destination: "windows-box", environment: environment))
        #expect(command.hasPrefix("powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand "))
        let encoded = try #require(command.split(separator: " ").last)
        let data = try #require(Data(base64Encoded: String(encoded)))
        let script = try #require(String(data: data, encoding: .utf16LittleEndian))
        let verified = try #require(script.range(of: "Get-FileHash -InputStream $file -Algorithm SHA256"))
        let launched = try #require(script.range(of: "& $relay serve"))
        #expect(script.contains(digest))
        #expect(verified.upperBound < launched.lowerBound)
    }
}
#endif
