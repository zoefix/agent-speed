import AgentSpeedCore
import SwiftUI

/// Add / Edit a host (DESIGN 13, 420 pt): the destination checked as you type, the name (placeholder = the automatic
/// name), Advanced with the relay folder, and *Test Connection* with its result inline. Save is the default button
/// and stays disabled while a field is invalid; saving an enabled host connects it at once.
struct RemoteHostSheet: View {
    let model: AppModel
    let onClose: () -> Void

    static let width: CGFloat = 420

    @State private var draft: RemoteHostDraft
    @State private var advancedExpanded: Bool
    @State private var destinationEdited: Bool
    @State private var tester: RemoteTestRunner
    @State private var hostWindow = HostWindow()
    @FocusState private var destinationFocused: Bool

    init(model: AppModel, draft: RemoteHostDraft, advancedExpanded: Bool? = nil,
         test: RemoteTestRunner.State = .idle, onClose: @escaping () -> Void) {
        self.model = model
        self.onClose = onClose
        _draft = State(initialValue: draft)
        _advancedExpanded = State(initialValue: advancedExpanded ?? !draft.relayFolder.isEmpty)
        _destinationEdited = State(initialValue: draft.isEditing || !draft.destination.isEmpty)
        _tester = State(initialValue: RemoteTestRunner(state: test))
    }

    var body: some View {
        let l10n = model.l10n
        VStack(spacing: 0) {
            header(l10n)
                .padding(.horizontal, Spacing.xl)
                .padding(.top, Spacing.xl)
                .padding(.bottom, Spacing.xxs)
            form(l10n)
            Divider()
            buttons(l10n)
                .padding(.horizontal, Spacing.xl)
                .padding(.vertical, Spacing.m)
        }
        .frame(width: Self.width)
        .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: tester.state)
        .onAppear { destinationFocused = !draft.isEditing }
        .onDisappear { tester.cancel() }
        .onChange(of: draft) { _, _ in tester.reset() }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(title(l10n))
    }

    // MARK: Parts

    private func title(_ l10n: Localizer) -> String {
        guard let original = draft.original else { return l10n("remote.sheet.add") }
        let name = model.settings.remoteHosts.displayName(of: original.id) ?? original.automaticName
        return l10n("remote.sheet.edit", name)
    }

    private func header(_ l10n: Localizer) -> some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(spacing: Spacing.s) {
                Image(systemName: "server.rack")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 30, height: 30)
                    .background(Color.accentColor.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                    .accessibilityHidden(true)
                Text(title(l10n))
                    .font(.system(size: 17, weight: .semibold))
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityAddTraits(.isHeader)
            }
            Text(l10n("remote.sheet.body"))
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func form(_ l10n: Localizer) -> some View {
        Form {
            Section {
                field(l10n("remote.destination"), text: $draft.destination,
                      prompt: l10n("remote.destination.placeholder"), monospaced: true,
                      error: destinationEdited ? draft.destinationIssue.map {
                          RemoteStatusText.fieldError($0, folder: false, l10n: l10n)
                      } : nil,
                      focused: $destinationFocused)
                    .onChange(of: draft.destination) { _, _ in destinationEdited = true }
                field(l10n("remote.name"), text: $draft.name,
                      prompt: draft.automaticName(in: model.settings.remoteHosts), monospaced: false,
                      error: draft.nameIsTooLong ? l10n("remote.error.tooLong", RemoteHostValidation.nameLimit) : nil)
            }
            Section {
                advancedRow(l10n)
                if advancedExpanded {
                    field(l10n("remote.relayFolder"), text: $draft.relayFolder,
                          prompt: RemoteHost.defaultRelayFolder, monospaced: true,
                          error: draft.folderIssue.map { RemoteStatusText.fieldError($0, folder: true, l10n: l10n) })
                    Footnote(l10n("remote.relayFolder.hint"))
                }
            }
            Section {
                RemoteHostQuotaRow(model: model, draft: $draft, host: hostWindow)
            }
            Section {
                testRow(l10n)
            }
        }
        .background(WindowReader(host: hostWindow))
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .scrollDisabled(true)
        .fixedSize(horizontal: false, vertical: true)
    }

    /// A label and its field; a problem reads right under the field, in its column.
    private func field(_ title: String, text: Binding<String>, prompt: String, monospaced: Bool, error: String?,
                       focused: FocusState<Bool>.Binding? = nil) -> some View {
        LabeledContent {
            VStack(alignment: .leading, spacing: 5) {
                TextField(text: text, prompt: Text(verbatim: prompt)) { Text(title) }
                    .labelsHidden()
                    .textFieldStyle(.roundedBorder)
                    .multilineTextAlignment(.leading)
                    .font(monospaced ? .system(size: 12).monospaced() : .system(size: 13))
                    .autocorrectionDisabled()
                    .modifier(OptionalFocus(focused: focused))
                    .accessibilityLabel(title)
                    .accessibilityValue(Text(error ?? ""))
                if let error {
                    RemoteFieldError(text: error)
                }
            }
            .frame(width: Self.fieldWidth, alignment: .leading)
        } label: {
            RowTitle(title)
        }
    }

    static let fieldWidth: CGFloat = 220

    /// The collapsed Advanced group, as the orb editor's: its folder in effect while closed.
    private func advancedRow(_ l10n: Localizer) -> some View {
        let folder = draft.cleanFolder.isEmpty ? RemoteHost.defaultRelayFolder : draft.cleanFolder
        return Button {
            withAnimation(model.accessibility.reduceMotion ? nil : Motion.layout) { advancedExpanded.toggle() }
        } label: {
            HStack(spacing: Spacing.xs) {
                Text(l10n("remote.advanced"))
                    .foregroundStyle(.primary)
                Spacer(minLength: Spacing.s)
                if !advancedExpanded {
                    Text(verbatim: folder)
                        .font(.system(size: 12).monospaced())
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Image(systemName: "chevron.right")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .rotationEffect(.degrees(advancedExpanded ? 90 : 0))
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(advancedExpanded ? .isSelected : [])
        .accessibilityValue(Text(advancedExpanded ? "" : folder))
    }

    private func testRow(_ l10n: Localizer) -> some View {
        let reason = testUnavailableReason(l10n)
        return HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            Button(l10n("remote.test"), action: runTest)
                .disabled(reason != nil || tester.state == .running)
                .help(reason ?? "")
            testResult(l10n)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder
    private func testResult(_ l10n: Localizer) -> some View {
        switch tester.state {
        case .idle:
            EmptyView()
        case .running:
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text(l10n("remote.test.running"))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            .transition(.opacity)
        case .finished(let outcome):
            RemoteTestResultLabel(outcome: outcome, destination: draft.cleanDestination, l10n: l10n)
                .transition(.opacity)
        }
    }

    private func buttons(_ l10n: Localizer) -> some View {
        HStack(spacing: Spacing.s) {
            Spacer(minLength: Spacing.xs)
            Button(l10n("history.cancel"), role: .cancel, action: close)
                .keyboardShortcut(.cancelAction)
            Button(l10n(draft.isEditing ? "remote.save" : "remote.addButton"), action: save)
                .keyboardShortcut(.defaultAction)
                .disabled(!draft.isValid || (!draft.isEditing && !model.settings.remoteHosts.canAdd))
        }
    }

    // MARK: Actions

    private func testUnavailableReason(_ l10n: Localizer) -> String? {
        if !model.settings.networkEnabled { return l10n("remote.status.networkOff") }
        if let issue = draft.destinationIssue { return RemoteStatusText.fieldError(issue, folder: false, l10n: l10n) }
        if let issue = draft.folderIssue { return RemoteStatusText.fieldError(issue, folder: true, l10n: l10n) }
        return nil
    }

    private func runTest() {
        guard testUnavailableReason(model.l10n) == nil else { return }
        tester.run(draft.host())
    }

    private func save() {
        guard draft.isValid else { return }
        if draft.isEditing {
            model.updateRemoteHost(draft.host())
        } else if !model.addRemoteHost(draft.host()) {
            return
        }
        close()
    }

    private func close() {
        tester.cancel()
        onClose()
    }
}

/// Runs *Test Connection* for the sheet and keeps its inline result; editing a field clears it.
@MainActor
@Observable
final class RemoteTestRunner {
    enum State: Equatable {
        case idle
        case running
        case finished(RemoteTestOutcome)
    }

    private(set) var state: State
    @ObservationIgnored private var test: RemoteConnectionTest?

    init(state: State = .idle) {
        self.state = state
    }

    func run(_ host: RemoteHost) {
        test?.cancel()
        state = .running
        test = RemoteConnectionTest.start(host) { [weak self] outcome in
            MainActor.assumeIsolated {
                self?.state = .finished(outcome)
                self?.test = nil
            }
        }
    }

    func reset() {
        cancel()
        if state != .idle { state = .idle }
    }

    func cancel() {
        test?.cancel()
        test = nil
    }
}

/// `可以連線 · Linux x86_64`, or the status sentence the connection would show.
private struct RemoteTestResultLabel: View {
    let outcome: RemoteTestOutcome
    let destination: String
    let l10n: Localizer

    var body: some View {
        switch outcome {
        case .supported(let system, _):
            label(l10n("remote.test.ok", system), symbol: "checkmark.circle.fill", tint: .green)
        case .failed(let status):
            let kind = RemoteDotKind(status)
            label(RemoteStatusText.base(status, destination: destination, l10n: l10n),
                  symbol: "exclamationmark.triangle.fill", tint: kind == .attention ? .red : .orange)
        }
    }

    private func label(_ text: String, symbol: String, tint: Color) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            Image(systemName: symbol)
                .foregroundStyle(tint)
                .imageScale(.small)
                .accessibilityHidden(true)
            Text(text)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.leading)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
        }
        .font(.subheadline)
        .accessibilityElement(children: .combine)
    }
}

/// A field's problem under it: a small red mark and the sentence, wrapping in the field's column.
private struct RemoteFieldError: View {
    let text: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 4) {
            Image(systemName: "exclamationmark.circle.fill")
                .foregroundStyle(.red)
                .imageScale(.small)
                .accessibilityHidden(true)
            Text(text)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.leading)
                .fixedSize(horizontal: false, vertical: true)
        }
        .font(.system(size: 11))
        .transition(.opacity)
    }
}

/// Binds a field's focus when the caller passes a binding.
private struct OptionalFocus: ViewModifier {
    let focused: FocusState<Bool>.Binding?

    func body(content: Content) -> some View {
        if let focused {
            content.focused(focused)
        } else {
            content
        }
    }
}
