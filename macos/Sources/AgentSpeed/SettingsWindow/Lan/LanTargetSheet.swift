import AgentSpeedCore
import SwiftUI

/// The fields of the Add / Edit sheet.
struct LanTargetDraft: Identifiable, Equatable {
    let id: String
    var original: LanDocument.Target?
    var kind: LanDocument.Kind
    var address: String
    var name: String
    var key: String

    init(editing target: LanDocument.Target? = nil) {
        id = target?.id ?? LanDocument.newId()
        original = target
        kind = target?.kind ?? .udp
        address = target?.address ?? ""
        name = target?.name ?? ""
        key = target?.key ?? ""
    }

    var isEditing: Bool { original != nil }
    var cleanAddress: String { address.trimmingCharacters(in: .whitespacesAndNewlines) }
    var parsed: Result<LanTargetAddress, LanAddressProblem> { LanTargetAddress.parse(kind: kind, cleanAddress) }
    var isValid: Bool { if case .success = parsed { true } else { false } }

    func target() -> LanDocument.Target {
        let key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        return LanDocument.Target(id: id, name: name.trimmingCharacters(in: .whitespacesAndNewlines), kind: kind,
                                  address: cleanAddress, key: key.isEmpty ? nil : key, enabled: original?.enabled ?? true)
    }
}

/// Add / Edit a target (DESIGN 14, 420 pt): the method, the address checked as you type, the name, the key, and
/// *Send Test* with its result inline. Save is the default button and stays disabled while the address is invalid.
struct LanTargetSheet: View {
    let model: AppModel
    let onClose: () -> Void

    static let width: CGFloat = 420
    static let fieldWidth: CGFloat = 230

    enum TestState: Equatable {
        case idle, running
        case finished(LanTargetStatus)
    }

    @State private var draft: LanTargetDraft
    @State private var addressEdited: Bool
    @State private var shownProblem: LanAddressProblem?
    @State private var validation: Task<Void, Never>?
    @State private var test: TestState
    @FocusState private var addressFocused: Bool

    init(model: AppModel, draft: LanTargetDraft, test: TestState = .idle, onClose: @escaping () -> Void) {
        self.model = model
        self.onClose = onClose
        _draft = State(initialValue: draft)
        _addressEdited = State(initialValue: draft.isEditing || !draft.address.isEmpty)
        _test = State(initialValue: test)
        if draft.isEditing || !draft.address.isEmpty, case .failure(let problem) = draft.parsed {
            _shownProblem = State(initialValue: problem)
        }
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
        .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: test)
        .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: shownProblem)
        .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: draft.kind)
        .onAppear { addressFocused = !draft.isEditing }
        .onChange(of: draft) { _, _ in if test != .idle { test = .idle } }
        .onChange(of: draft.address) { _, _ in addressEdited = true; validate() }
        .onChange(of: draft.kind) { _, _ in validate(now: true) }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(title(l10n))
    }

    // MARK: Parts

    private func title(_ l10n: Localizer) -> String {
        guard let original = draft.original else { return l10n("lan.sheet.add") }
        return l10n("lan.sheet.edit", original.displayName)
    }

    private func header(_ l10n: Localizer) -> some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(spacing: Spacing.s) {
                Image(systemName: "dot.radiowaves.left.and.right")
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
            Text(l10n("lan.sheet.body"))
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func form(_ l10n: Localizer) -> some View {
        Form {
            Section {
                LabeledContent {
                    Picker(l10n("lan.target.kind"), selection: $draft.kind) {
                        Text(verbatim: "UDP").tag(LanDocument.Kind.udp)
                        Text(verbatim: "HTTP").tag(LanDocument.Kind.http)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: Self.fieldWidth)
                } label: {
                    RowTitle(l10n("lan.target.kind"))
                }
                Footnote(l10n(draft.kind == .udp ? "lan.target.kind.udp.hint" : "lan.target.kind.http.hint"))
                    .contentTransition(.opacity)
            }
            Section {
                field(l10n("lan.target.address"), text: $draft.address,
                      prompt: l10n(draft.kind == .udp ? "lan.target.address.placeholder.udp"
                                                      : "lan.target.address.placeholder.http"),
                      monospaced: true,
                      error: addressEdited ? shownProblem.map { LanStatusText.addressProblem($0, l10n: l10n) } : nil,
                      focused: $addressFocused)
                if case .success(let address) = draft.parsed, LanStatusText.isBroadcast(address, kind: draft.kind) {
                    Label {
                        Text(l10n("lan.target.broadcast"))
                    } icon: {
                        Image(systemName: "info.circle")
                    }
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .transition(.opacity)
                }
                field(l10n("lan.target.name"), text: $draft.name, prompt: l10n("lan.target.name.placeholder"),
                      monospaced: false, error: nil)
                field(l10n("lan.target.key"), text: $draft.key, prompt: l10n("lan.target.name.placeholder"),
                      monospaced: true, error: nil)
                Footnote(l10n("lan.target.key.hint"))
            }
            Section {
                testRow(l10n)
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .scrollDisabled(true)
        .fixedSize(horizontal: false, vertical: true)
    }

    private func field(_ title: String, text: Binding<String>, prompt: String, monospaced: Bool, error: String?,
                       focused: FocusState<Bool>.Binding? = nil) -> some View {
        LabeledContent {
            VStack(alignment: .leading, spacing: 5) {
                Group {
                    if let focused {
                        TextField(text: text, prompt: Text(verbatim: prompt)) { Text(title) }
                            .focused(focused)
                    } else {
                        TextField(text: text, prompt: Text(verbatim: prompt)) { Text(title) }
                    }
                }
                .labelsHidden()
                .textFieldStyle(.roundedBorder)
                .multilineTextAlignment(.leading)
                .font(monospaced ? .system(size: 12).monospaced() : .system(size: 13))
                .autocorrectionDisabled()
                .accessibilityLabel(title)
                .accessibilityValue(Text(error ?? ""))
                if let error {
                    HStack(alignment: .firstTextBaseline, spacing: 4) {
                        Image(systemName: "exclamationmark.circle.fill")
                            .foregroundStyle(.red)
                            .imageScale(.small)
                            .accessibilityHidden(true)
                        Text(error)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .font(.system(size: 11))
                    .transition(.opacity)
                }
            }
            .frame(width: Self.fieldWidth, alignment: .leading)
        } label: {
            RowTitle(title)
        }
    }

    private func testRow(_ l10n: Localizer) -> some View {
        let unavailable = !model.settings.networkEnabled ? l10n("lan.target.status.networkOff")
            : draft.isValid ? nil : l10n("lan.target.address.invalid")
        return HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            Button(l10n("lan.target.test"), action: runTest)
                .disabled(unavailable != nil || test == .running)
                .help(unavailable ?? "")
            testResult(l10n)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder
    private func testResult(_ l10n: Localizer) -> some View {
        switch test {
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
        case .finished(let status):
            let ok = status.state == .ok
            HStack(alignment: .firstTextBaseline, spacing: 5) {
                Image(systemName: ok ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                    .foregroundStyle(ok ? Color.green : LanStatusText.dot(status.state) == .attention ? .red : .orange)
                    .imageScale(.small)
                    .accessibilityHidden(true)
                Text(ok ? l10n("lan.target.test.ok")
                        // A test does not retry, so no countdown (DESIGN 14).
                        : status.state == .unreachable ? l10n("remote.status.unreachable")
                        : LanStatusText.target(status, now: Date(), l10n: l10n))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .font(.subheadline)
            .accessibilityElement(children: .combine)
            .transition(.opacity)
        }
    }

    private func buttons(_ l10n: Localizer) -> some View {
        HStack(spacing: Spacing.s) {
            Spacer(minLength: Spacing.xs)
            Button(l10n("history.cancel"), role: .cancel, action: onClose)
                .keyboardShortcut(.cancelAction)
            Button(l10n(draft.isEditing ? "remote.save" : "remote.addButton"), action: save)
                .keyboardShortcut(.defaultAction)
                .disabled(!draft.isValid || (!draft.isEditing && !model.settings.lan.canAdd))
        }
    }

    // MARK: Actions

    /// The address error shows 0.3 s after typing stops (at once when the method changes).
    private func validate(now: Bool = false) {
        validation?.cancel()
        let problem: LanAddressProblem? = if case .failure(let problem) = draft.parsed { problem } else { nil }
        if now || problem == nil {
            shownProblem = problem
            return
        }
        validation = Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
            shownProblem = problem
        }
    }

    private func runTest() {
        test = .running
        let target = draft.target()
        Task { @MainActor in
            let status = await model.lan.test(target)
            if test == .running { test = .finished(status) }
        }
    }

    private func save() {
        guard draft.isValid, model.lan.save(draft.target()) else { return }
        onClose()
    }
}
