import AgentSpeedCore
@testable import AgentSpeedOrbModel
import Foundation
import Testing

/// DESIGN 13, *Orbs and boards*: the Hosts pickers and submenus, automatic names with hosts, and REMOTE 2.4 (a removed
/// host leaves every selection; an empty one tracks all).
@Suite("Orb host pickers, names and removal")
struct OrbHostPickerTests {
    static let a = "8C0D6B0A-1111-4222-8333-00000000000A"
    static let b = "8C0D6B0A-1111-4222-8333-00000000000B"
    static let c = "8C0D6B0A-1111-4222-8333-00000000000C"
    static let order = ["local", a, b, c]
    static let choices = [
        OrbHostChoice(id: "local", name: "This Mac"), OrbHostChoice(id: a, name: "devbox"),
        OrbHostChoice(id: b, name: "gpu-01"), OrbHostChoice(id: c, name: "pi"),
    ]

    static let namer = OrbNamer(
        sourceName: { $0 == .codex ? "Codex" : $0 == .claudeCode ? "Claude Code" : $0.rawValue },
        allSources: "All Sources",
        pair: { "\($0) + \($1)" },
        more: { "\($0) +\($1)" },
        hosts: choices
    )

    @Test func selectionsKeepSettingsOrderWithUnknownIdsLast() {
        let selection = OrbHostSelection.explicit([Self.c, "future", Self.a]).ordered(by: Self.order)
        #expect(selection == .explicit([Self.a, Self.c, "future"]))
        #expect(OrbHostSelection.explicit([Self.b, "local"]).ordered(by: Self.order) == .explicit(["local", Self.b]))
        #expect(OrbHostSelection.all.ordered(by: Self.order) == .all)
    }

    @Test func togglingFollowsThePickersRule() {
        #expect(OrbHostSelection.all.toggling(Self.b, known: Self.order) == .explicit([Self.b]))
        #expect(OrbHostSelection.explicit([Self.c]).toggling(Self.a, known: Self.order) == .explicit([Self.a, Self.c]))
        #expect(OrbHostSelection.explicit([Self.a, Self.c]).toggling(Self.c, known: Self.order) == .explicit([Self.a]))
        #expect(OrbHostSelection.explicit([Self.a]).toggling(Self.a, known: Self.order) == nil)
        // An id this Mac does not know never counts as the remaining host.
        #expect(OrbHostSelection.explicit([Self.a, "future"]).toggling(Self.a, known: Self.order) == nil)
    }

    @Test func entriesCheckTrackedHostsAndLockTheLastOne() {
        let all = OrbHostSelection.all.entries(Self.choices)
        #expect(all.map(\.id) == Self.order)
        let open = all.allSatisfy { !$0.checked && $0.enabled }
        #expect(open)

        let one = OrbHostSelection.explicit([Self.b, "future"]).entries(Self.choices)
        #expect(one.map(\.checked) == [false, false, true, false])
        #expect(one.map(\.enabled) == [true, true, false, true])

        let two = OrbHostSelection.explicit(["local", Self.b]).entries(Self.choices)
        let unlocked = two.filter(\.checked).allSatisfy { $0.enabled }
        #expect(unlocked)
    }

    @Test func automaticNamesAppendTheHosts() {
        #expect(Self.namer.automaticName(.explicit([.codex]), hosts: .all) == "Codex")
        #expect(Self.namer.automaticName(.explicit([.codex]), hosts: .explicit([Self.a])) == "Codex · devbox")
        #expect(Self.namer.automaticName(.all, hosts: .explicit(["local"])) == "All Sources · This Mac")
        #expect(Self.namer.automaticName(.explicit([.codex, .claudeCode]), hosts: .explicit([Self.c, "local", Self.b]))
            == "Codex + Claude Code · This Mac +2")
        #expect(Self.namer.automaticName(.explicit([.codex]), hosts: .explicit(["future"])) == "Codex")
        let custom = OrbConfig(id: "o", name: "Mine", sources: .explicit([.codex]), hosts: .explicit([Self.a]))
        #expect(Self.namer.displayName(custom) == "Mine")
        let automatic = OrbConfig(id: "o", sources: .explicit([.codex]), hosts: .explicit([Self.a]))
        #expect(Self.namer.displayName(automatic) == "Codex · devbox")
    }

    @Test func withoutRemoteHostsNoNameMentionsAHost() {
        var namer = Self.namer
        namer.hosts = [OrbHostChoice(id: "local", name: "This Mac")]
        #expect(namer.automaticName(.explicit([.codex]), hosts: .explicit(["local"])) == "Codex")
        namer.hosts = []
        #expect(namer.automaticName(.explicit([.codex]), hosts: .explicit([Self.a])) == "Codex")
        #expect(!OrbHostChoice.pickerShown([OrbHostChoice(id: "local", name: "This Mac")]))
        #expect(OrbHostChoice.pickerShown(Self.choices))
    }

    @Test func removingAHostLeavesEveryOrbAndEmptySelectionsTrackAll() {
        let doc = OrbsDocument(orbs: [
            OrbConfig(id: "o1", hosts: .explicit([Self.a])),
            OrbConfig(id: "o2", hosts: .explicit(["local", Self.a, Self.b])),
            OrbConfig(id: "o3"),
        ])
        let next = OrbEditing.removingHost(Self.a, from: doc)
        #expect(next.orbs.map(\.hosts) == [.all, .explicit(["local", Self.b]), .all])
        #expect(OrbEditing.setHosts(.explicit([Self.b]), of: "o3", in: next).orb("o3")?.hosts == .explicit([Self.b]))
    }

    @Test func splitAndMergeKeepTheirOrbsHosts() throws {
        let doc = OrbsDocument(orbs: [
            OrbConfig(id: "o1", sources: .explicit([.codex, .claudeCode]), hosts: .explicit([Self.a])),
            OrbConfig(id: "o2", hosts: .explicit([Self.b])),
        ])
        let availability = OrbAvailability(detected: [.codex, .claudeCode])
        let split = try #require(OrbEditing.split("o1", in: doc, availability: availability, newIds: ["n1", "n2"]))
        #expect(split.document.orbs.filter { $0.id != "o2" }.allSatisfy { $0.hosts == .explicit([Self.a]) })
        let bySource = try #require(OrbEditing.splitBySource(doc, selectedId: "o1", availability: availability, ids: ["n1"]))
        #expect(bySource.document.orbs.allSatisfy { $0.hosts == .explicit([Self.a]) })
        let merged = try #require(OrbEditing.merge(doc, keeping: "o2"))
        #expect(merged.orbs.map(\.hosts) == [.explicit([Self.b])])
    }

    @Test func boardsToggleAndLoseRemovedHosts() throws {
        var document = BoardsDocument.empty
        document.add(id: "b0")
        document.add(id: "b1")
        let first = document.toggleHost(Self.b, known: Self.order, of: "b0")
        let second = document.toggleHost(Self.a, known: Self.order, of: "b0")
        #expect(first && second)
        #expect(document.board("b0")?.hosts == .explicit([Self.a, Self.b]))
        document.setHosts(.explicit([Self.b]), of: "b1")
        let refused = document.toggleHost(Self.b, known: Self.order, of: "b1")
        #expect(!refused)
        document.removeHost(Self.b)
        #expect(document.board("b0")?.hosts == .explicit([Self.a]))
        #expect(document.board("b1")?.hosts == .all)
    }

    @Test func theBoardMenuOffersHostsOnlyWithARemoteHost() throws {
        var document = BoardsDocument.empty
        document.add(id: "b0")
        document.setHosts(.explicit([Self.a]), of: "b0")
        let board = try #require(document.board("b0"))
        let local = [OrbHostChoice(id: "local", name: "This Mac")]
        let plain = BoardMenuModel.context(board, document: document, hosts: local)
        #expect(!plain.contains { if case .hosts = $0 { true } else { false } })

        let items = BoardMenuModel.context(board, document: document, hosts: Self.choices)
        let index = try #require(items.firstIndex { if case .hosts = $0 { true } else { false } })
        guard case .sources = items[index - 1], case .hosts(let all, let entries) = items[index] else {
            Issue.record("Hosts must follow Sources")
            return
        }
        #expect(!all)
        #expect(entries.map(\.name) == ["This Mac", "devbox", "gpu-01", "pi"])
        #expect(entries[1].checked && !entries[1].enabled)
    }
}
