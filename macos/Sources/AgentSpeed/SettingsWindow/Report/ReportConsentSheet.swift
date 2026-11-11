import AgentSpeedCore
import SwiftUI

/// The consent sheet (DESIGN 11): who receives what, the username as it will be sent, the API key when needed, the
/// section switches and a live preview of the exact next body. Nothing is created or sent before Start.
struct ReportConsentSheet: View {
    let model: AppModel

    static let width: CGFloat = 520

    @State private var previewExpanded = true
    @State private var formHeight: CGFloat?
    @State private var headerHeight: CGFloat = 0
    @State private var footerHeight: CGFloat = 0
    @FocusState private var usernameFocused: Bool

    /// The tallest the sheet may grow; nil fits the screen. Preview renders pass a large value.
    var maxHeight: CGFloat?

    var body: some View {
        let l10n = model.l10n
        let report = model.report
        if let draft = report.consent {
            VStack(spacing: 0) {
                header(draft)
                    .padding(.horizontal, Spacing.xl)
                    .padding(.top, Spacing.xl)
                    .padding(.bottom, Spacing.xs)
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { headerHeight = $0 }
                ScrollView(.vertical) {
                    form(draft)
                        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { formHeight = $0 }
                }
                .scrollBounceBehavior(.basedOnSize)
                .frame(height: scrollHeight)
                VStack(spacing: 0) {
                    Divider()
                    buttons(draft)
                        .padding(.horizontal, Spacing.xl)
                        .padding(.vertical, Spacing.m)
                }
                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { footerHeight = $0 }
            }
            .frame(width: Self.width)
            .onAppear {
                report.openPreview()
                usernameFocused = !draft.isReview
            }
            .onDisappear { report.closePreview() }
            .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: draft)
            .accessibilityElement(children: .contain)
            .accessibilityLabel(l10n("report.consent.title", draft.discovery.name))
        }
    }

    /// The form at its natural height; it scrolls only when the sheet would not fit the screen.
    private func form(_ draft: ReportController.ConsentDraft) -> some View {
        Form {
            Section {
                ReportServiceCard(model: model, service: draft.service, discovery: draft.discovery)
            }
            identity(draft)
            if draft.discovery.apiKeyMode != .none, !draft.isReview {
                apiKey(draft)
            }
            sections(draft)
            preview(draft)
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .scrollDisabled(true)
        .fixedSize(horizontal: false, vertical: true)
    }

    /// The form's own height, at most what the screen leaves after the title and the buttons.
    private var scrollHeight: CGFloat {
        let room = (maxHeight ?? Self.screenHeight) - headerHeight - footerHeight
        return max(160, min(formHeight ?? room, room))
    }

    /// The visible screen minus room for the menu bar, the parent window's title bar and a margin.
    static var screenHeight: CGFloat {
        let screen = NSScreen.main?.visibleFrame.height ?? 900
        return max(480, screen - 96)
    }

    // MARK: - Parts

    private func header(_ draft: ReportController.ConsentDraft) -> some View {
        let l10n = model.l10n
        return VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(spacing: Spacing.s) {
                Image(systemName: "dot.radiowaves.left.and.right")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 30, height: 30)
                    .background(Color.accentColor.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                    .accessibilityHidden(true)
                Text(l10n("report.consent.title", draft.discovery.name))
                    .font(.system(size: 17, weight: .semibold))
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityAddTraits(.isHeader)
            }
            Text(l10n("report.consent.body", draft.discovery.limits.intervalSeconds))
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func identity(_ draft: ReportController.ConsentDraft) -> some View {
        let l10n = model.l10n
        let rules = draft.discovery.usernameRules
        let usernameBinding = Binding(
            get: { model.report.consent?.username ?? "" },
            set: { value in
                model.report.consent?.username = value
                model.report.consent?.usernameError = nil
                model.report.consent?.offersTransfer = false
            })
        let localProblem = ReportUsernameField.problem(draft.username, rules: rules, l10n: l10n)
        let message = draft.usernameError.map { key in
            key == "report.username.invalid.length" ? l10n(key, rules.minLength, rules.maxLength) : l10n(key)
        } ?? (draft.username.isEmpty ? nil : localProblem)
        return Section {
            ReportUsernameField(model: model, text: usernameBinding, rules: rules, focused: $usernameFocused)
                .disabled(draft.isReview || draft.isStarting)
            if let message {
                ReportInlineMessage(text: message)
            }
            if draft.offersTransfer {
                HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                    Footnote(l10n("report.username.transfer.hint"))
                    Spacer(minLength: Spacing.xs)
                    Button(l10n("report.username.transfer")) { model.report.startReporting(transfer: true) }
                }
            }
        }
    }

    private func apiKey(_ draft: ReportController.ConsentDraft) -> some View {
        let l10n = model.l10n
        let mode = draft.discovery.apiKeyMode
        return Section {
            LabeledContent {
                SecureField(text: Binding(
                    get: { model.report.consent?.apiKey ?? "" },
                    set: { value in
                        model.report.consent?.apiKey = value
                        model.report.consent?.apiKeyError = nil
                    }), prompt: Text(mode == .optional ? l10n("report.apiKey.optional") : "")) {
                    Text(l10n("report.apiKey"))
                }
                .labelsHidden()
                .textFieldStyle(.roundedBorder)
                .frame(width: 210)
                .disabled(draft.isStarting)
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    RowTitle(l10n("report.apiKey"))
                    if mode == .required { Footnote(l10n("report.apiKey.required")) }
                }
            }
            if let error = draft.apiKeyError {
                ReportInlineMessage(text: l10n(error))
            }
            HStack(alignment: .firstTextBaseline) {
                Footnote(l10n("report.apiKey.hint"))
                Spacer(minLength: Spacing.xs)
                if let url = draft.discovery.apiKeyURL { ReportLink(title: l10n("report.apiKey.get"), url: url) }
            }
        }
    }

    private func sections(_ draft: ReportController.ConsentDraft) -> some View {
        let l10n = model.l10n
        return Section {
            ReportSectionToggles(model: model, selection: Binding(
                get: { model.report.consent?.sections ?? [] },
                set: { model.report.consent?.sections = $0 }
            ), quota: Binding(
                get: { model.report.consent?.shareQuota ?? true },
                set: { model.report.consent?.shareQuota = $0 }
            ), discovery: draft.discovery)
            .disabled(draft.isStarting)
        } header: {
            Text(l10n("report.sections"))
        } footer: {
            SectionFootnote(l10n("report.sections.hint"))
        }
    }

    private func preview(_ draft: ReportController.ConsentDraft) -> some View {
        let l10n = model.l10n
        return Section {
            DisclosureGroup(isExpanded: $previewExpanded) {
                TimelineView(.periodic(from: .now, by: 1)) { _ in
                    ReportPreviewBox(model: model, preview: model.report.preview(draft: draft))
                }
                .padding(.top, Spacing.xs)
            } label: {
                RowTitle(l10n("report.preview.title"))
            }
        } footer: {
            footnotes
                .padding(.top, Spacing.xs)
        }
    }

    private var footnotes: some View {
        let l10n = model.l10n
        return VStack(alignment: .leading, spacing: 6) {
            Label {
                Text(l10n("report.consent.never"))
            } icon: {
                Image(systemName: "hand.raised.fill").foregroundStyle(.secondary)
            }
            Label {
                Text(l10n("report.consent.honesty"))
            } icon: {
                Image(systemName: "info.circle.fill").foregroundStyle(.secondary)
            }
        }
        .font(.system(size: 11))
        .foregroundStyle(.secondary)
        .imageScale(.small)
        .fixedSize(horizontal: false, vertical: true)
    }

    private func buttons(_ draft: ReportController.ConsentDraft) -> some View {
        let l10n = model.l10n
        let rules = draft.discovery.usernameRules
        let valid = ReportUsernameField.problem(draft.username, rules: rules, l10n: l10n) == nil
        return HStack(spacing: Spacing.s) {
            if draft.isStarting {
                ProgressView().controlSize(.small)
                Text(l10n("report.status.registering"))
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
            } else if let failure = draft.failure {
                ReportInlineMessage(text: l10n(failure))
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: Spacing.xs)
            Button(l10n("history.cancel"), role: .cancel) { model.report.cancelConsent() }
                .keyboardShortcut(.cancelAction)
            Button(l10n("report.start")) { model.report.startReporting() }
                .keyboardShortcut(.defaultAction)
                .disabled(draft.isStarting || (!draft.isReview && !valid) || !model.settings.networkEnabled)
        }
    }
}
