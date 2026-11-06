import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 13 and REMOTE 2.1, 2.4, 4.5 in the app: a sentence and a dot for every status, the retry countdown, the
/// clock note and tooltip, the editor's checks, the pickers' choices, names with hosts, and removing a host.
@Suite("Remote hosts UI")
@MainActor
struct RemoteHostsUITests {
    static let a = "8C0D6B0A-1111-4222-8333-00000000000A"
    static let b = "8C0D6B0A-1111-4222-8333-00000000000B"
    static let now = Date(timeIntervalSince1970: 1_780_000_000)

    static let everyStatus: [RemoteStatus] = [
        .connecting, .installing(fraction: 0.3), .seeding(fraction: 0.42), .connected, .unreachable, .authFailed,
        .hostKeyUnknown, .unsupportedSystem(system: "FreeBSD amd64"), .cannotVerify, .uploadFailed, .notExecutable,
        .versionMismatch(relayProtocol: 2), .protocolError, .exited(status: 1), .sshMissing, .invalidDestination,
        .networkOff, .disabled,
    ]

    private static func model() -> AppModel {
        _ = NSApplication.shared
        let suite = "RemoteHostsUITests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.removePersistentDomain(forName: suite)
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        model.settings.language = .en
        model.l10n.apply(.en)
        model.activate(live: false)
        return model
    }

    // MARK: Status text

    @Test func everyStatusHasItsOwnSentenceInEveryLanguage() {
        for language in [LanguagePreference.zhHant, .zhHans, .en, .ja] {
            let l10n = Localizer(preference: language)
            let sentences = Self.everyStatus.map {
                RemoteStatusText.base($0, destination: "devbox", l10n: l10n)
            }
            #expect(sentences.allSatisfy { !$0.isEmpty && !$0.hasPrefix("remote.") }, "\(language)")
            // No two statuses read alike.
            #expect(Set(sentences).count == sentences.count, "\(language) \(sentences)")
            #expect(RemoteStatusText.base(.hostKeyUnknown, destination: "devbox", l10n: l10n).contains("ssh devbox"))
            #expect(RemoteStatusText.base(.unsupportedSystem(system: "FreeBSD amd64"), destination: "", l10n: l10n)
                .contains("FreeBSD amd64"))
            #expect(RemoteStatusText.base(.seeding(fraction: 0.427), destination: "", l10n: l10n).contains("42%"))
        }
    }

    @Test func theConnectedLineCountsTodaysAgents() {
        let l10n = Localizer(preference: .en)
        let connected = RemoteHostStatus(status: .connected)
        #expect(RemoteStatusText.sentence(connected, destination: "", sessions: 0, now: Self.now, l10n: l10n) == "Connected")
        #expect(RemoteStatusText.sentence(connected, destination: "", sessions: 1, now: Self.now, l10n: l10n)
            == "Connected · 1 agent")
        #expect(RemoteStatusText.sentence(connected, destination: "", sessions: 3, now: Self.now, l10n: l10n)
            == "Connected · 3 agents")
        let zh = Localizer(preference: .zhHant)
        #expect(RemoteStatusText.sentence(connected, destination: "", sessions: 3, now: Self.now, l10n: zh)
            == "已連線 · 3 個 agent")
    }

    @Test func retryingStatusesCountDownToTheNextAttempt() {
        let zh = Localizer(preference: .zhHant)
        let status = RemoteHostStatus(status: .unreachable, retryAt: Self.now.addingTimeInterval(9.4))
        #expect(RemoteStatusText.sentence(status, destination: "", sessions: 0, now: Self.now, l10n: zh) == "無法連線，10 秒後重試")
        #expect(RemoteStatusText.sentence(status, destination: "", sessions: 0, now: Self.now.addingTimeInterval(10), l10n: zh)
            == "無法連線，正在重試…")
        // Errors that need the user retry quietly every 5 minutes: no countdown.
        let auth = RemoteHostStatus(status: .authFailed, retryAt: Self.now.addingTimeInterval(240))
        #expect(!RemoteStatusText.sentence(auth, destination: "", sessions: 0, now: Self.now, l10n: zh).contains("秒"))
    }

    @Test func aSkewedClockAddsItsNoteOnlyWhileConnected() {
        let l10n = Localizer(preference: .zhHant)
        #expect(RemoteStatusText.clockNote(RemoteHostStatus(status: .connected, clockOffsetMs: 4_999), l10n: l10n) == nil)
        #expect(RemoteStatusText.clockNote(RemoteHostStatus(status: .connected, clockOffsetMs: -12_400), l10n: l10n)
            == "遠端時鐘相差 12 秒，已自動校正")
        #expect(RemoteStatusText.clockNote(RemoteHostStatus(status: .connected, clockOffsetMs: 185_000), l10n: l10n)
            == "遠端時鐘相差 3 分鐘，已自動校正")
        #expect(RemoteStatusText.clockNote(RemoteHostStatus(status: .seeding(fraction: 0.1), clockOffsetMs: 60_000),
                                           l10n: l10n) == nil)
    }

    @Test func theTooltipNamesTheLastErrorAndTheExitStatus() throws {
        let l10n = Localizer(preference: .en)
        let exited = RemoteHostStatus(status: .exited(status: 137), lastErrorAt: Self.now.addingTimeInterval(-30))
        let tip = try #require(RemoteStatusText.tooltip(exited, now: Self.now, l10n: l10n))
        #expect(tip.hasPrefix("Last error: "))
        #expect(tip.hasSuffix("Exit status 137"))
        let connected = RemoteHostStatus(status: .connected, relayVersion: "1.7.0", relayArch: "aarch64")
        #expect(RemoteStatusText.tooltip(connected, now: Self.now, l10n: l10n) == "Helper 1.7.0 · aarch64")
        #expect(RemoteStatusText.tooltip(RemoteHostStatus(status: .disabled), now: Self.now, l10n: l10n) == nil)
    }

    @Test func dotsAndWordsFollowTheStatusGroups() {
        let l10n = Localizer(preference: .en)
        let expected: [(RemoteStatus, RemoteDotKind, String)] = [
            (.connected, .connected, "Connected"),
            (.seeding(fraction: 0.5), .progress, "Connecting"),
            (.installing(fraction: 0.5), .progress, "Connecting"),
            (.unreachable, .retrying, "Retrying"),
            (.exited(status: 1), .retrying, "Retrying"),
            (.protocolError, .retrying, "Retrying"),
            (.authFailed, .attention, "Needs action"),
            (.versionMismatch(relayProtocol: 3), .attention, "Needs action"),
            (.invalidDestination, .attention, "Needs action"),
            (.networkOff, .idle, "Offline"),
            (.disabled, .idle, "Off"),
        ]
        for (status, dot, word) in expected {
            #expect(RemoteDotKind(status) == dot, "\(status)")
            #expect(RemoteStatusText.word(status, l10n: l10n) == word, "\(status)")
        }
    }

    // MARK: Editor

    @Test func theDraftChecksFieldsAsTheUserTypes() {
        var draft = RemoteHostDraft()
        #expect(!draft.isValid)
        #expect(draft.destinationIssue == .empty)
        draft.destination = "  zoe@devbox\n"
        #expect(draft.isValid)
        #expect(draft.host().destination == "zoe@devbox")
        draft.destination = "zoe@dev box"
        #expect(draft.destinationIssue == .forbiddenCharacter)
        draft.destination = "-oProxyCommand=x"
        #expect(draft.destinationIssue == .leadingDash)
        draft.destination = "devbox"
        draft.relayFolder = "~/../tmp"
        #expect(draft.folderIssue == .unsafeComponent)
        draft.relayFolder = "/srv/agent-speed/"
        #expect(draft.isValid)
        #expect(draft.host().relayFolder == "/srv/agent-speed/")
        draft.relayFolder = " "
        #expect(draft.host().relayFolder == nil)
        draft.name = String(repeating: "x", count: 33)
        #expect(draft.nameIsTooLong && !draft.isValid)
        draft.name = "  Lab  "
        #expect(draft.host().name == "Lab")

        let l10n = Localizer(preference: .zhHant)
        #expect(RemoteStatusText.fieldError(.tooLong, folder: true, l10n: l10n) == "太長了（最多 200 個字元）")
        #expect(RemoteStatusText.fieldError(.forbiddenCharacter, folder: true, l10n: l10n).contains(". _ - / ~"))
    }

    @Test func editingKeepsTheIdAndSwitchAndTheNamePlaceholderIsTheDistinctAutomaticName() {
        let existing = RemoteHost(id: Self.a, destination: "zoe@devbox", enabled: false)
        let document = RemoteHostsDocument(hosts: [existing])
        var draft = RemoteHostDraft(editing: existing)
        draft.destination = "zoe@devbox2"
        let edited = draft.host()
        #expect(edited.id == Self.a && !edited.enabled && edited.destination == "zoe@devbox2")
        var adding = RemoteHostDraft()
        adding.destination = "other@devbox"
        #expect(adding.automaticName(in: document) == "devbox (2)")
        #expect(RemoteHostDraft(editing: existing).automaticName(in: document) == "devbox")
        adding.destination = "bad host"
        #expect(adding.automaticName(in: document).isEmpty)
    }

    // MARK: Model

    @Test func choicesAndStatusesWaitForTheFirstHost() {
        let model = Self.model()
        #expect(model.hostChoices.isEmpty && !model.hasRemoteHosts)
        model.addRemoteHost(RemoteHost(id: Self.a, destination: "devbox"))
        model.addRemoteHost(RemoteHost(id: Self.b, destination: "pi@raspberrypi.local", name: "Pi", enabled: false))
        #expect(model.hostChoices.map(\.name) == ["This Mac", "devbox", "Pi"])
        #expect(model.hostOrder == [RemoteHost.localId, Self.a, Self.b])
        let a = model.settings.remoteHosts.host(Self.a)!
        let b = model.settings.remoteHosts.host(Self.b)!
        #expect(model.remoteStatus(of: a).status == .connecting)
        #expect(model.remoteStatus(of: b).status == .disabled)
        model.settings.networkEnabled = false
        #expect(model.remoteStatus(of: a).status == .networkOff)
        model.settings.networkEnabled = true
        #expect(model.hostDotKind(RemoteHost.localId) == .connected)
        #expect(model.remoteHostName(Self.a) == "devbox" && model.remoteHostName(RemoteHost.localId) == nil)
        model.setRemoteHostEnabled(true, id: Self.b)
        #expect(model.settings.remoteHosts.host(Self.b)?.enabled == true)
    }

    @Test func aHistoryTooLargeToSyncIsNotedWhateverTheConnectionDoes() {
        let model = Self.model()
        model.addRemoteHost(RemoteHost(id: Self.a, destination: "devbox"))
        model.addRemoteHost(RemoteHost(id: Self.b, destination: "gpu", enabled: false))
        let a = model.settings.remoteHosts.host(Self.a)!
        let b = model.settings.remoteHosts.host(Self.b)!
        #expect(!model.remoteStatus(of: a).historyTooLarge)
        #expect(RemoteStatusText.historyNote(model.remoteStatus(of: a), l10n: model.l10n) == nil)
        model.history.publish(remoteTooLarge: [Self.a, Self.b])
        #expect(model.remoteStatus(of: a).historyTooLarge && model.remoteStatus(of: a).status == .connecting)
        #expect(model.remoteStatus(of: b).historyTooLarge && model.remoteStatus(of: b).status == .disabled)
        #expect(RemoteStatusText.historyNote(model.remoteStatus(of: a), l10n: model.l10n)
            == "This host has too much usage history to sync")
        for language in [LanguagePreference.zhHant, .zhHans, .ja] {
            model.l10n.apply(language)
            let note = RemoteStatusText.historyNote(model.remoteStatus(of: a), l10n: model.l10n)
            #expect(note != nil && note != "remote.status.historyTooLarge")
        }
        model.history.publish(remoteTooLarge: [])
        #expect(!model.remoteStatus(of: a).historyTooLarge)
    }

    @Test func theRemotePrivacyParagraphExistsInEveryLanguage() {
        let model = Self.model()
        for language in [LanguagePreference.zhHant, .zhHans, .en, .ja] {
            model.l10n.apply(language)
            let text = model.l10n("privacy.remote")
            #expect(text.contains("~/.cache/agent-speed") && text.contains("SSH"))
        }
    }

    @Test func orbNamesFollowTheirHostsWhileRemoteHostsExist() throws {
        let model = Self.model()
        let orbId = try #require(model.orbs.orbs.first?.id)
        model.orbs.setSources(.explicit([.codex]), of: orbId)
        model.orbs.setHosts(.explicit([RemoteHost.localId]), of: orbId)
        #expect(model.orbs.displayName(of: orbId) == "Codex")
        model.addRemoteHost(RemoteHost(id: Self.a, destination: "devbox"))
        #expect(model.orbs.displayName(of: orbId) == "Codex · This Mac")
        model.orbs.toggleHost(Self.a, of: orbId)
        #expect(model.orbs.orb(orbId)?.hosts == .explicit([RemoteHost.localId, Self.a]))
        #expect(model.orbs.displayName(of: orbId) == "Codex · This Mac +1")
        model.orbs.toggleHost(RemoteHost.localId, of: orbId)
        model.orbs.toggleHost(Self.a, of: orbId)
        #expect(model.orbs.orb(orbId)?.hosts == .explicit([Self.a]), "the last host stays")
    }

    @Test func removingAHostLeavesEveryOrbAndBoard() throws {
        let model = Self.model()
        model.addRemoteHost(RemoteHost(id: Self.a, destination: "devbox"))
        model.addRemoteHost(RemoteHost(id: Self.b, destination: "gpu"))
        let orbId = try #require(model.orbs.orbs.first?.id)
        model.orbs.setHosts(.explicit([Self.a]), of: orbId)
        let boardId = try #require(model.boards.add())
        model.boards.setHosts(.explicit([Self.b, Self.a, RemoteHost.localId]), of: boardId)
        #expect(model.boards.board(boardId)?.hosts == .explicit([RemoteHost.localId, Self.a, Self.b]))

        model.removeRemoteHost(Self.a, deleteHistory: true)
        #expect(model.settings.remoteHosts.host(Self.a) == nil)
        #expect(model.orbs.orb(orbId)?.hosts == .all)
        #expect(model.boards.board(boardId)?.hosts == .explicit([RemoteHost.localId, Self.b]))
        #expect(model.hostChoices.map(\.id) == [RemoteHost.localId, Self.b])
    }
}
