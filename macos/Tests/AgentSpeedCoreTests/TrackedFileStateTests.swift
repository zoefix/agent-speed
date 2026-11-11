@testable import AgentSpeedCore
import Foundation
import Testing

/// A tracked file keeps its parser's state until the parser changes, so that the session's every-publish passes over
/// thousands of files read it without copying the parser each time.
@Suite("Tracked file state")
struct TrackedFileStateTests {
    private static func file(_ family: TranscriptFamily) -> TrackedFile {
        TrackedFile(path: "/tmp/agent-speed-tests/a.jsonl", family: family, root: "/tmp/agent-speed-tests", fileID: FileID(1),
                    stat: FileStat(identity: FileIdentity(device: 1, inode: 1), size: 0, modifiedNs: 0, isRegularFile: true),
                    start: .beginning)
    }

    @Test(arguments: [TranscriptFamily.claude, .codex])
    func theStateFollowsEveryChangeOfTheParser(_ family: TranscriptFamily) {
        let file = Self.file(family)
        #expect(file.state == file.parser.state)
        #expect(file.state.malformedLines == 0)
        file.parser.noteMalformedLine()
        #expect(file.state.malformedLines == 1, "a change through the parser drops the kept state")
        file.parser.noteMalformedLine()
        #expect(file.state == file.parser.state && file.state.malformedLines == 2)
        file.resetParser()
        #expect(file.state.malformedLines == 0, "a new parser starts a new state")
    }
}
