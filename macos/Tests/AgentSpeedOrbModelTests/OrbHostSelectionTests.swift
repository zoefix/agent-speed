import AgentSpeedCore
@testable import AgentSpeedOrbModel
import Foundation
import Testing

/// The `hosts` of orbs and boards (DESIGN 13).
@Suite("Orb host selection")
struct OrbHostSelectionTests {
    static let a = "8C0D6B0A-1111-4222-8333-00000000000A"

    static func decode<T: Decodable>(_ type: T.Type, _ json: String) throws -> T {
        try JSONDecoder().decode(type, from: Data(json.utf8))
    }

    static func encode<T: Encodable>(_ value: T) throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return String(decoding: try encoder.encode(value), as: UTF8.self)
    }

    @Test func codesAsAllOrAnArrayAndKeepsUnknownIds() throws {
        #expect(try Self.decode(OrbHostSelection.self, #""all""#) == .all)
        #expect(try Self.decode(OrbHostSelection.self, #"[]"#) == .all)
        #expect(try Self.decode(OrbHostSelection.self, #""local""#) == .explicit(["local"]))
        #expect(try Self.decode(OrbHostSelection.self, "[\"\(Self.a)\",\"local\",\"\(Self.a)\",\"future-id\"]")
            == .explicit(["local", Self.a, "future-id"]))
        #expect(try Self.decode(OrbHostSelection.self, "42") == .all)
        #expect(try Self.encode(OrbHostSelection.all) == #""all""#)
        #expect(try Self.encode(OrbHostSelection.explicit(["local", Self.a])) == "[\"local\",\"\(Self.a)\"]")
    }

    @Test func togglingAndRemoving() {
        #expect(OrbHostSelection.all.toggling("local") == .explicit(["local"]))
        #expect(OrbHostSelection.explicit([Self.a]).toggling("local") == .explicit(["local", Self.a]))
        #expect(OrbHostSelection.explicit(["local", Self.a]).toggling("local") == .explicit([Self.a]))
        #expect(OrbHostSelection.explicit(["local"]).toggling("local") == nil, "the last host cannot be unchecked")
        #expect(OrbHostSelection.explicit(["local", Self.a]).removing(Self.a) == .explicit(["local"]))
        #expect(OrbHostSelection.explicit([Self.a]).removing(Self.a) == .all, "a selection left empty becomes all")
        #expect(OrbHostSelection.all.removing(Self.a) == .all)
        #expect(OrbHostSelection.explicit([Self.a]).hostSet == [Self.a] && OrbHostSelection.all.hostSet == nil)
        #expect(OrbHostSelection.all.tracks("anything") && !OrbHostSelection.explicit([Self.a]).tracks("local"))
    }

    @Test func documentsWithoutHostsTrackAllAndAllIsNotWritten() throws {
        let orb = try Self.decode(OrbConfig.self, #"{"id":"o1","sources":["codex"]}"#)
        #expect(orb.hosts == .all)
        #expect(try !Self.encode(orb).contains("hosts"))
        var remote = orb
        remote.hosts = .explicit([Self.a])
        let text = try Self.encode(remote)
        #expect(text.contains("\"hosts\":[\"\(Self.a)\"]"))
        #expect(try Self.decode(OrbConfig.self, text) == remote)

        let board = try Self.decode(BoardConfig.self, #"{"id":"b1","hosts":"all"}"#)
        #expect(board.hosts == .all)
        #expect(try !Self.encode(board).contains("hosts"))
        let local = try Self.decode(BoardConfig.self, #"{"id":"b1","hosts":["local"]}"#)
        #expect(local.hosts == .explicit(["local"]))
        #expect(try Self.decode(BoardConfig.self, Self.encode(local)) == local)
    }

    @Test func subsetKeys() {
        #expect(OrbSourceSelection.all.subsetKey(hosts: .all) == nil)
        #expect(OrbSourceSelection.explicit([.codex]).subsetKey(hosts: .all) == SubsetKey(SourceSet(.codex)))
        #expect(OrbSourceSelection.all.subsetKey(hosts: .explicit(["local"])) == SubsetKey(.everything, hosts: ["local"]))
        #expect(OrbSourceSelection.explicit([.codex, .gemini]).subsetKey(hosts: .explicit([Self.a]))
            == SubsetKey(SourceSet([.codex, .gemini]), hosts: [Self.a]))
    }

    @Test func aBoardOfOneHostRunsItsOwnPeak() {
        var live = BoardLive(sources: .all, hosts: .explicit(["local"]))
        var snapshot = SpeedSnapshot.empty
        snapshot.state = .running
        snapshot.timestamp = Date(timeIntervalSince1970: 1000)
        snapshot.dayStart = Date(timeIntervalSince1970: 0)
        snapshot.peakToday = Rate(output: 999, total: 999)
        snapshot.subsetPeaks = [SubsetKey(.everything, hosts: ["local"]): 40]
        let frame = live.ingest(snapshot, metric: .output, layout: BoardLiveTests.layout, engineNewPeak: true)
        #expect(!frame.newPeak, "the engine's all-hosts peak is not this board's")
        #expect(frame.peak == 40)
        live.setHosts(.all)
        #expect(live.hosts == .all)
        #expect(live.ingest(snapshot, metric: .output, layout: BoardLiveTests.layout).peak == 999)
    }
}
