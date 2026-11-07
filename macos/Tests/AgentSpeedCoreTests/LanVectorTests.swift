import Foundation
import Testing
@testable import AgentSpeedCore

/// spec/fixtures/lan-vectors.json (LAN 7): every section, checked against the reference implementation's results.
@Suite struct LanVectorTests {
    nonisolated(unsafe) static let vectors: [String: Any] = (try? Fixtures.json("lan-vectors.json")) ?? [:]
    static let key = vectors["key"] as? String ?? ""

    private func cases(_ name: String) throws -> [[String: Any]] {
        try #require(Self.vectors[name] as? [[String: Any]])
    }

    private static func live(_ values: [String: Any]) -> LanValues {
        LanValues(outputTokS: values.double("outputTokS") ?? 0, estimatedShare: values.double("estimatedShare") ?? 0,
                  sessions: LanSessions(active: values.int("active"), generating: values.int("generating"),
                                        tools: values.int("tools")))
    }

    static func frame(_ input: [String: Any]) -> ReportJSON {
        let sources = (input["sources"] as? [String: [String: Any]] ?? [:]).reduce(into: [SourceKind: LanValues]()) {
            if let kind = SourceKind(rawValue: $1.key) { $0[kind] = live($1.value) }
        }
        let identity = LanIdentity(instance: input.string("instance") ?? "", name: input.string("name") ?? "",
                                   run: input.string("run") ?? "")
        let history = (input["history"] as? [NSNumber] ?? []).map(\.doubleValue)
        return LanFrame.build(identity: identity, seq: input.int64("seq") ?? 0, atMs: input.int64("atMs") ?? 0,
                              live: LanLiveValues(overall: live(input["live"] as? [String: Any] ?? [:]),
                                                  sources: sources),
                              history: history)
    }

    @Test func framesAreByteExact() throws {
        for vector in try cases("frames") {
            let built = Self.frame(try #require(vector["input"] as? [String: Any]))
            #expect(String(decoding: built.compactData, as: UTF8.self) == vector.string("frame"),
                    "\(vector.string("name") ?? "")")
        }
    }

    @Test func historyFollowsTheTicks() throws {
        for vector in try cases("history") {
            var history = LanHistory()
            var after: [Double] = []
            for tick in try #require(vector["ticks"] as? [[NSNumber]]) {
                after = history.tick(nowMs: tick[0].int64Value, outputTokS: tick[1].doubleValue)
            }
            #expect(after == (vector["after"] as? [NSNumber] ?? []).map(\.doubleValue), "\(vector.string("name") ?? "")")
        }
    }

    @Test func datagramsDropHistoryThenSources() throws {
        for vector in try cases("datagrams") {
            let frame = try Self.parseFrame(try #require(vector.string("frame")))
            let result = LanDatagram.make(frame: frame, key: vector["key"] as? String)
            #expect(String(decoding: result.data, as: UTF8.self) == vector.string("datagram"), "\(vector.string("name") ?? "")")
            #expect(result.omitted == (vector["omitted"] as? [String] ?? []))
            #expect(result.data.count == vector.int("bytes"))
        }
    }

    @Test func firstLinesAreClassified() throws {
        let lines = try #require(Self.vectors["firstLines"] as? [[String]])
        for pair in lines {
            let result = LanFirstLine.classify(Data(pair[0].utf8), final: true)
            let expected: LanFirstLine = pair[1] == "http" ? .http : pair[1] == "line" ? .line : .close
            #expect(result == expected, "\(pair[0].prefix(40))")
        }
        #expect(LanFirstLine.classify(Data("GET /v1/live".utf8)) == nil)
    }

    @Test func requestsAreAnsweredInOrder() throws {
        var lockout = LanLockout()
        for vector in try cases("requests") {
            let name = vector.string("name") ?? ""
            let text = try #require(vector.string("head"))
            let head = try #require(LanRequestHead.parse(Data(text.utf8))?.head, "\(name)")
            let response = LanResponse.respond(to: head, peer: vector.string("peer") ?? "", nowMs: vector.int64("nowMs") ?? 0,
                                               key: Self.key, hasFrame: vector["hasFrame"] as? Bool ?? true,
                                               lockout: &lockout)
            #expect(response.status == vector.int("status"), "\(name)")
            #expect(response.error == vector["error"] as? String, "\(name)")
            let headers = vector["headers"] as? [String: String] ?? [:]
            #expect(Dictionary(uniqueKeysWithValues: response.headers) == headers, "\(name)")
        }
    }

    @Test func hellosAreChecked() throws {
        var lockout = LanLockout()
        for vector in try cases("hellos") {
            let error = LanError.hello(Data(try #require(vector.string("line")).utf8), peer: "192.168.1.77",
                                       nowMs: vector.int64("nowMs") ?? 0, key: Self.key, lockout: &lockout)
            #expect(error == vector["error"] as? String, "\(vector.string("name") ?? "")")
        }
    }

    @Test func webSocketValues() throws {
        let websocket = try #require(Self.vectors["websocket"] as? [String: Any])
        for pair in try #require(websocket["accept"] as? [[String: String]]) {
            #expect(LanWebSocket.accept(pair["key"] ?? "") == pair["accept"])
        }
        for vector in try #require(websocket["serverFrames"] as? [[String: Any]]) {
            let frame = LanWebSocket.frame(opcode: UInt8(vector.int("opcode")), Data((vector.string("payload") ?? "").utf8))
            let hex = frame.map { String(format: "%02x", $0) }.joined()
            let expected = vector.string("frame") ?? ""
            #expect(vector["prefixOnly"] as? Bool == true ? hex.hasPrefix(expected) : hex == expected)
        }
        for vector in try #require(websocket["closes"] as? [[String: Any]]) {
            let code = (vector["code"] as? NSNumber).map { UInt16($0.intValue) }
            #expect(LanWebSocket.close(code).map { String(format: "%02x", $0) }.joined() == vector.string("frame"))
        }
        for vector in try #require(websocket["clientFrames"] as? [[String: Any]]) {
            let bytes = Self.hex(vector.string("frame") ?? "")
            let result = try #require(vector["result"] as? [String: Any])
            let (action, _) = LanWebSocket.read(bytes)
            let name = vector.string("name") ?? ""
            switch result.string("action") {
            case "pong": #expect(action == .pong(Self.hex(result.string("payload") ?? "")), "\(name)")
            case "echoClose": #expect(action == .echoClose((result["code"] as? NSNumber).map { UInt16($0.intValue) }), "\(name)")
            case "close": #expect(action == .close(UInt16(result.int("code"))), "\(name)")
            default: #expect(action == .ignore, "\(name)")
            }
        }
    }

    @Test func localAddresses() throws {
        for row in try #require(Self.vectors["local"] as? [[Any]]) {
            let address = try #require(row[0] as? String)
            let udp = try #require(row[1] as? Bool)
            #expect(LanAddress.isLocal(address, udp: udp) == (row[2] as? Bool), "\(address) udp=\(udp)")
        }
    }

    @Test func targetAddresses() throws {
        for vector in try cases("targets") {
            let kind = try #require(LanDocument.Kind(rawValue: vector.string("kind") ?? ""))
            let address = vector.string("address") ?? ""
            let expected = try #require(vector["result"] as? [String: Any])
            switch LanTargetAddress.parse(kind: kind, address) {
            case .success(let parsed):
                #expect(expected["error"] == nil, "\(address)")
                #expect(parsed.host == expected.string("host"), "\(address)")
                #expect(parsed.isLiteral == expected["literal"] as? Bool, "\(address)")
                #expect(parsed.port == expected.int("port"), "\(address)")
                #expect(parsed.hostHeader == expected.string("hostHeader"), "\(address)")
                #expect(parsed.target == expected.string("target"), "\(address)")
            case .failure(let problem):
                #expect(problem.rawValue == expected.string("error"), "\(address)")
            }
        }
        for vector in try cases("resolutions") {
            let status = LanResolution.check(vector["addresses"] as? [String] ?? [], udp: vector["udp"] as? Bool ?? false)
            #expect(status.rawValue == vector.string("status"))
        }
    }

    @Test func signatures() throws {
        for vector in try cases("signatures") {
            #expect(LanSignature.line(key: vector.string("key") ?? "", body: Data((vector.string("body") ?? "").utf8))
                    == vector.string("signature"))
        }
    }

    @Test func settingsDocuments() throws {
        for vector in try cases("settings") {
            let document = LanDocument(json: vector["document"])
            let expected = try #require(vector["settings"] as? [String: Any])
            let name = vector.string("name") ?? ""
            #expect(NSDictionary(dictionary: document.jsonObject).isEqual(to: expected), "\(name): \(document.jsonObject)")
        }
    }

    @Test func pushReceivers() throws {
        for vector in try cases("push") {
            let kind: LanPushReceiver.Kind = switch vector.string("kind") {
            case "udp": .udp
            case "http": .http
            default: .stream
            }
            var receiver = LanPushReceiver(kind)
            var sent: [Int64] = []
            for tick in try #require(vector["ticks"] as? [[Any]]) {
                let at = (tick[0] as? NSNumber)?.int64Value ?? 0
                if receiver.tick(nowMs: at, changed: tick[1] as? Bool ?? false) { sent.append(at) }
            }
            #expect(sent == (vector["sent"] as? [NSNumber] ?? []).map(\.int64Value), "\(vector.string("kind") ?? "")")
        }
    }

    @Test func keysAndBackoff() throws {
        for vector in try cases("keys") {
            #expect(LanAccess.matches(vector.string("given"), key: Self.key) == (vector["matches"] as? Bool),
                    "\(vector.string("given") ?? "")")
        }
        #expect(LanDocument.displayKey(Self.key) == Self.vectors["keyDisplay"] as? String)
        let backoff = (Self.vectors["httpBackoffMs"] as? [NSNumber] ?? []).map(\.int64Value)
        #expect((0..<backoff.count).map { LanBackoff.ms(failures: $0) } == backoff)
        let key = LanDocument.newKey()
        #expect(LanDocument.isValidKey(key))
        #expect(LanIdentity.newRun().range(of: "^[0-9a-f]{16}$", options: .regularExpression) != nil)
    }

    // MARK: Helpers

    static func hex(_ text: String) -> Data {
        var data = Data()
        var index = text.startIndex
        while index < text.endIndex {
            let next = text.index(index, offsetBy: 2)
            data.append(UInt8(text[index..<next], radix: 16) ?? 0)
            index = next
        }
        return data
    }

    /// A frame's text as an ordered document, so the datagram rule can drop members and write the same bytes.
    static func parseFrame(_ text: String) throws -> ReportJSON {
        var parser = OrderedJSONParser(Array(text.utf8))
        return try parser.value()
    }
}

/// A small ordered JSON reader for the vectors (Foundation's parser loses member order and number forms).
struct OrderedJSONParser {
    struct Failure: Error {}
    private let bytes: [UInt8]
    private var index = 0

    init(_ bytes: [UInt8]) { self.bytes = bytes }

    mutating func value() throws -> ReportJSON {
        skipSpace()
        guard index < bytes.count else { throw Failure() }
        switch bytes[index] {
        case UInt8(ascii: "{"):
            index += 1
            var members: [ReportJSON.Member] = []
            skipSpace()
            if bytes[index] == UInt8(ascii: "}") { index += 1; return .object([]) }
            while true {
                skipSpace()
                let key = try string()
                skipSpace()
                index += 1 // :
                members.append(.init(key, try value()))
                skipSpace()
                let mark = bytes[index]
                index += 1
                if mark == UInt8(ascii: "}") { return .object(members) }
            }
        case UInt8(ascii: "["):
            index += 1
            var items: [ReportJSON] = []
            skipSpace()
            if bytes[index] == UInt8(ascii: "]") { index += 1; return .array([]) }
            while true {
                items.append(try value())
                skipSpace()
                let mark = bytes[index]
                index += 1
                if mark == UInt8(ascii: "]") { return .array(items) }
            }
        case UInt8(ascii: "\""):
            return .string(try string())
        case UInt8(ascii: "t"):
            index += 4
            return .bool(true)
        case UInt8(ascii: "f"):
            index += 5
            return .bool(false)
        case UInt8(ascii: "n"):
            index += 4
            return .null
        default:
            let start = index
            while index < bytes.count, "-+.eE0123456789".utf8.contains(bytes[index]) { index += 1 }
            let text = String(decoding: bytes[start..<index], as: UTF8.self)
            if let integer = Int64(text) { return .int(integer) }
            let decimals = text.split(separator: ".").last.map(\.count) ?? 0
            return .decimal(Double(text) ?? 0, decimals: decimals)
        }
    }

    private mutating func string() throws -> String {
        guard bytes[index] == UInt8(ascii: "\"") else { throw Failure() }
        let start = index
        index += 1
        while bytes[index] != UInt8(ascii: "\"") {
            if bytes[index] == UInt8(ascii: "\\") { index += 1 }
            index += 1
        }
        index += 1
        let literal = Data(bytes[start..<index])
        return try JSONSerialization.jsonObject(with: literal, options: [.fragmentsAllowed]) as? String ?? ""
    }

    private mutating func skipSpace() {
        while index < bytes.count, [0x20, 0x0A, 0x0D, 0x09].contains(bytes[index]) { index += 1 }
    }
}
