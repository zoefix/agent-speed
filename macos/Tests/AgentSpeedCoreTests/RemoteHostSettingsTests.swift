import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Remote host settings")
struct RemoteHostSettingsTests {
    static let id1 = "8C0D6B0A-1111-4222-8333-944455556666"
    static let id2 = "8C0D6B0A-1111-4222-8333-944455556667"

    @Test(arguments: ["devbox", "user@host", "ssh://host", "ssh://user@host:2222", "ssh://[::1]:22", "10.0.0.7",
                      "a.b-c_d%e+f=g,h~i", "user@[fe80::1]", String(repeating: "h", count: 255)])
    func acceptsDestinations(_ destination: String) {
        #expect(RemoteHostValidation.destinationIssue(destination) == nil)
    }

    @Test func refusesDestinations() {
        let cases: [(String, RemoteFieldIssue)] = [
            ("", .empty),
            (String(repeating: "h", count: 256), .tooLong),
            ("-oProxyCommand=evil", .leadingDash),
            ("-V", .leadingDash),
            ("host;rm", .forbiddenCharacter),
            ("$(x)", .forbiddenCharacter),
            ("`id`", .forbiddenCharacter),
            ("my host", .forbiddenCharacter),
            ("host\ttab", .forbiddenCharacter),
            ("host\n", .forbiddenCharacter),
            ("a|b", .forbiddenCharacter),
            ("a&b", .forbiddenCharacter),
            ("a>b", .forbiddenCharacter),
            ("a<b", .forbiddenCharacter),
            ("a'b", .forbiddenCharacter),
            ("a\"b", .forbiddenCharacter),
            ("a\\b", .forbiddenCharacter),
            ("a*", .forbiddenCharacter),
            ("a?", .forbiddenCharacter),
            ("a!", .forbiddenCharacter),
            ("{a}", .forbiddenCharacter),
            ("a#b", .forbiddenCharacter),
            ("(a)", .forbiddenCharacter),
            ("hôst", .forbiddenCharacter),
            ("host\u{7F}", .forbiddenCharacter),
        ]
        for (destination, issue) in cases {
            #expect(RemoteHostValidation.destinationIssue(destination) == issue, "\(destination.debugDescription)")
        }
    }

    @Test(arguments: ["~/.cache/agent-speed", "/opt/agent-speed", "/srv/a.b_c-d/~x", "~/x/", "/"])
    func acceptsFolders(_ folder: String) {
        #expect(RemoteHostValidation.folderIssue(folder) == nil)
    }

    @Test func refusesFolders() {
        let cases: [(String, RemoteFieldIssue)] = [
            ("", .empty),
            ("/" + String(repeating: "a", count: 200), .tooLong),
            ("relative/path", .notAbsolute),
            ("~", .notAbsolute),
            ("~root/x", .notAbsolute),
            ("-rf", .notAbsolute),
            ("~/a/../b", .unsafeComponent),
            ("/..", .unsafeComponent),
            ("~/..", .unsafeComponent),
            ("/a//b", .unsafeComponent),
            ("~/my folder", .forbiddenCharacter),
            ("~/$(x)", .forbiddenCharacter),
            ("~/a;b", .forbiddenCharacter),
            ("~/*", .forbiddenCharacter),
            ("~/a\nb", .forbiddenCharacter),
            ("~/ä", .forbiddenCharacter),
        ]
        for (folder, issue) in cases {
            #expect(RemoteHostValidation.folderIssue(folder) == issue, "\(folder.debugDescription)")
        }
    }

    @Test func idsAreUpperCaseUUIDsAndNeverLocal() {
        #expect(RemoteHostValidation.isValidId(Self.id1))
        #expect(RemoteHostValidation.isValidId(UUID().uuidString))
        #expect(!RemoteHostValidation.isValidId(Self.id1.lowercased()))
        #expect(!RemoteHostValidation.isValidId("local"))
        #expect(!RemoteHostValidation.isValidId("devbox"))
        #expect(!RemoteHostValidation.isValidId(""))
    }

    @Test func namesAreTrimmedAndLimited() {
        #expect(RemoteHostValidation.cleanName("  Dev Box  ") == "Dev Box")
        #expect(RemoteHostValidation.cleanName("   ") == nil)
        #expect(RemoteHostValidation.cleanName(nil) == nil)
        #expect(RemoteHostValidation.cleanName(String(repeating: "x", count: 40))?.count == 32)
        #expect(RemoteHostValidation.cleanName("a\u{0}b\nc") == "abc")
        #expect(RemoteHost(destination: "devbox", name: " ").name == nil)
    }

    @Test func automaticNamesAreTheHostPart() {
        let cases = [
            ("devbox", "devbox"),
            ("user@devbox", "devbox"),
            ("ssh://devbox", "devbox"),
            ("ssh://user@devbox.example.com:2222", "devbox.example.com"),
            ("ssh://[::1]:22", "::1"),
            ("SSH://user@host", "host"),
            ("a@b@host", "host"),
            ("ssh://", "ssh://"),
        ]
        for (destination, name) in cases {
            #expect(RemoteHostValidation.automaticName(for: destination) == name, "\(destination)")
        }
    }

    @Test func duplicateAutomaticNamesGetCounters() {
        let document = RemoteHostsDocument(hosts: [
            RemoteHost(id: Self.id1, destination: "devbox"),
            RemoteHost(id: "8C0D6B0A-1111-4222-8333-000000000003", destination: "alice@devbox"),
            RemoteHost(id: "8C0D6B0A-1111-4222-8333-000000000004", destination: "ssh://bob@devbox:22"),
            RemoteHost(id: "8C0D6B0A-1111-4222-8333-000000000005", destination: "other", name: "Build"),
            RemoteHost(id: "8C0D6B0A-1111-4222-8333-000000000006", destination: "build"),
        ])
        let names = document.displayNames()
        #expect(names[Self.id1] == "devbox")
        #expect(names["8C0D6B0A-1111-4222-8333-000000000003"] == "devbox (2)")
        #expect(names["8C0D6B0A-1111-4222-8333-000000000004"] == "devbox (3)")
        #expect(names["8C0D6B0A-1111-4222-8333-000000000005"] == "Build")
        #expect(names["8C0D6B0A-1111-4222-8333-000000000006"] == "build (2)")
    }

    @Test func atMostEightHosts() {
        var document = RemoteHostsDocument()
        for _ in 0..<8 {
            let added = document.add(RemoteHost(destination: "devbox"))
            #expect(added)
        }
        #expect(!document.canAdd)
        let ninth = document.add(RemoteHost(destination: "devbox"))
        #expect(!ninth)
        #expect(document.hosts.count == 8)
        let many = RemoteHostsDocument(hosts: (0..<12).map { _ in RemoteHost(destination: "x") })
        #expect(many.hosts.count == 8)
    }

    @Test func addRefusesReservedAndDuplicateIds() {
        var document = RemoteHostsDocument()
        let local = document.add(RemoteHost(id: "local", destination: "devbox"))
        let first = document.add(RemoteHost(id: Self.id1, destination: "devbox"))
        let duplicate = document.add(RemoteHost(id: Self.id1, destination: "other"))
        let updated = document.update(RemoteHost(id: Self.id1, destination: "other", enabled: false))
        #expect(!local && first && !duplicate && updated)
        #expect(document.host(Self.id1)?.destination == "other")
        document.remove(Self.id1)
        #expect(document.hosts.isEmpty)
    }

    @Test func roundTripKeepsUnknownFields() throws {
        let json = """
        {"version":1,"future":{"a":[1,2.5,"x",null,true]},"hosts":[
          {"id":"\(Self.id1)","destination":"devbox","name":null,"enabled":true,"relayFolder":null,"color":"teal","n":12},
          {"id":"\(Self.id2)","destination":"ssh://u@h:2","name":"Lab","enabled":false,"relayFolder":"/opt/as"}
        ]}
        """
        let document = try #require(RemoteHostsDocument(data: Data(json.utf8)))
        #expect(document.hosts.count == 2)
        #expect(document.hosts[1].name == "Lab")
        #expect(document.hosts[1].enabled == false)
        #expect(document.hosts[1].relayFolder == "/opt/as")
        let written = try #require(try JSONParser.parse(document.data()).object)
        #expect(written["future"] == (try JSONParser.parse(#"{"a":[1,2.5,"x",null,true]}"#)))
        let first = try #require(written["hosts"]?.array?.first?.object)
        #expect(first["color"]?.string == "teal")
        #expect(first["n"]?.int64 == 12)
        #expect(first["name"] == .null)
        #expect(RemoteHostsDocument(data: document.data()) == document)
    }

    @Test func decodingSkipsInvalidHostsAndKeepsInvalidDestinations() throws {
        let json = """
        {"version":1,"hosts":[
          {"id":"local","destination":"devbox"},
          {"id":"not-a-uuid","destination":"devbox"},
          {"destination":"devbox"},
          {"id":"\(Self.id1)","destination":"-oProxyCommand=x","enabled":true},
          {"id":"\(Self.id1)","destination":"again"},
          {"id":"\(Self.id2)","destination":"ok","relayFolder":"~/../etc"}
        ]}
        """
        let document = try #require(RemoteHostsDocument(data: Data(json.utf8)))
        #expect(document.hosts.map(\.id) == [Self.id1, Self.id2])
        #expect(document.hosts[0].destination == "-oProxyCommand=x")
        #expect(!document.hosts[0].isConnectable)
        #expect(!document.hosts[1].isConnectable)
        #expect(RemoteHostsDocument(data: Data("[]".utf8)) == nil)
        #expect(RemoteHostsDocument(data: Data(#"{"version":1}"#.utf8))?.hosts == [])
    }

    @Test func effectiveFolderDefaultsAndDropsTrailingSlash() {
        #expect(RemoteHost(destination: "d").effectiveRelayFolder == "~/.cache/agent-speed")
        #expect(RemoteHost(destination: "d", relayFolder: "/opt/as/").effectiveRelayFolder == "/opt/as")
        #expect(RemoteHost(destination: "d", relayFolder: "").relayFolder == nil)
    }
}
