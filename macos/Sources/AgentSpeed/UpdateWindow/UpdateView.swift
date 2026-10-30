import AgentSpeedCore
import SwiftUI

/// What the update window shows, derived from the controller.
enum UpdateWindowMode: Equatable {
    case idle
    case checking
    case upToDate
    case checkFailed(UpdateFailure)
    case offer(Release)
    case install(Release, UpdateController.Installation)

    @MainActor
    init(_ updates: UpdateController) {
        if let release = updates.availableRelease {
            self = updates.installation == .idle ? .offer(release) : .install(release, updates.installation)
            return
        }
        switch updates.status {
        case .idle, .available: self = .idle
        case .checking: self = .checking
        case .upToDate: self = .upToDate
        case .failed(let failure): self = .checkFailed(failure)
        }
    }

    var release: Release? {
        switch self {
        case .offer(let release), .install(let release, _): release
        case .idle, .checking, .upToDate, .checkFailed: nil
        }
    }
}

/// Icon and title, release notes, then buttons, progress or an error (UPDATES section 4).
struct UpdateView: View {
    static let horizontalPadding = Spacing.xl
    static let titlebarRoom: CGFloat = 30

    let model: AppModel
    let close: @MainActor () -> Void

    var body: some View {
        let mode = UpdateWindowMode(model.updates)
        VStack(alignment: .leading, spacing: 0) {
            UpdateHeader(model: model, mode: mode)
            if let release = mode.release, !release.notes.isEmpty {
                ReleaseNotesSection(model: model, release: release)
                    .padding(.top, Spacing.l)
            }
            UpdateFooter(model: model, mode: mode, close: close)
                .padding(.top, Spacing.l)
        }
        .padding(.horizontal, Self.horizontalPadding)
        .padding(.top, Self.titlebarRoom)
        .padding(.bottom, Spacing.l)
        .frame(width: UpdateWindowController.width, alignment: .topLeading)
        .fixedSize(horizontal: false, vertical: true)
        .animation(model.accessibility.reduceMotion ? Motion.fade : Motion.layout, value: mode.phase)
        .onExitCommand { close() }
    }
}

private struct UpdateHeader: View {
    let model: AppModel
    let mode: UpdateWindowMode

    var body: some View {
        let l10n = model.l10n
        let current = l10n("update.current", model.updates.currentVersion)
        HStack(alignment: .center, spacing: Spacing.m) {
            AppMark(size: 64)
            VStack(alignment: .leading, spacing: 3) {
                Text(title(l10n))
                    .font(.system(size: 15, weight: .semibold))
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityAddTraits(.isHeader)
                Text(current)
                    .font(.system(size: 12))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
    }

    private func title(_ l10n: Localizer) -> String {
        switch mode {
        case .offer(let release), .install(let release, _): l10n("update.available.title", release.version.description)
        case .checking: l10n("update.checking")
        case .upToDate: l10n("update.upToDate")
        case .checkFailed(let failure): l10n.failureText(failure)
        case .idle: l10n("app.name")
        }
    }
}

private struct ReleaseNotesSection: View {
    let model: AppModel
    let release: Release

    var body: some View {
        let l10n = model.l10n
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(alignment: .firstTextBaseline) {
                Text(l10n("update.notes"))
                    .font(.system(size: 11, weight: .semibold))
                    .textCase(model.dashboardLatinCaps ? .uppercase : nil)
                    .tracking(model.dashboardLatinCaps ? 0.6 : 0.2)
                    .foregroundStyle(.secondary)
                    .accessibilityAddTraits(.isHeader)
                Spacer(minLength: Spacing.xs)
                if let published = release.publishedAt {
                    Text(published.formatted(Date.FormatStyle(date: .abbreviated, time: .omitted).locale(l10n.locale)))
                        .font(.system(size: 11))
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                }
            }
            ReleaseNotesView(tag: release.tag, markdown: release.notes)
                .equatable()
        }
    }
}

private struct UpdateFooter: View {
    let model: AppModel
    let mode: UpdateWindowMode
    let close: @MainActor () -> Void

    var body: some View {
        let l10n = model.l10n
        let updates = model.updates
        Group {
            switch mode {
            case .offer:
                HStack(spacing: Spacing.xs) {
                    Button(l10n("update.skip")) {
                        updates.skip()
                        close()
                    }
                    Spacer(minLength: Spacing.xs)
                    Button(l10n("update.later")) { close() }
                        .keyboardShortcut(.cancelAction)
                    Button(l10n("update.install")) { updates.install() }
                        .keyboardShortcut(.defaultAction)
                }
            case .install(let release, let installation):
                if case .failed(let failure) = installation {
                    FailureFooter(model: model, failure: failure, release: release, primary: l10n("update.retry"), close: close) {
                        updates.retry()
                    }
                } else {
                    InstallProgress(model: model, installation: installation)
                }
            case .checking:
                ProgressView()
                    .progressViewStyle(.linear)
                    .frame(maxWidth: .infinity)
                    .frame(height: Self.rowHeight)
                    .accessibilityLabel(l10n("update.checking"))
            case .upToDate, .idle:
                HStack {
                    Spacer()
                    Button(l10n("mac.update.ok")) { close() }
                        .keyboardShortcut(.defaultAction)
                }
            case .checkFailed(let failure):
                FailureFooter(model: model, failure: failure, release: nil, primary: l10n("update.retry"), close: close, showsReason: false) {
                    updates.retry()
                }
            }
        }
        .controlSize(.regular)
        .frame(minHeight: Self.rowHeight)
        .transition(.opacity)
    }

    static let rowHeight: CGFloat = 32
}

/// The failure reason (unless the title already shows it), then View on GitHub, Later and Try Again.
private struct FailureFooter: View {
    let model: AppModel
    let failure: UpdateFailure
    let release: Release?
    let primary: String
    let close: @MainActor () -> Void
    var showsReason = true
    let retry: @MainActor () -> Void

    init(
        model: AppModel,
        failure: UpdateFailure,
        release: Release?,
        primary: String,
        close: @escaping @MainActor () -> Void,
        showsReason: Bool = true,
        retry: @escaping @MainActor () -> Void
    ) {
        self.model = model
        self.failure = failure
        self.release = release
        self.primary = primary
        self.close = close
        self.showsReason = showsReason
        self.retry = retry
    }

    var body: some View {
        let l10n = model.l10n
        let page = model.updates.configuration.pageURL(for: release)
        VStack(alignment: .leading, spacing: Spacing.s) {
            if showsReason {
                Label {
                    Text(l10n.failureText(failure))
                        .fixedSize(horizontal: false, vertical: true)
                } icon: {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                }
                .font(.system(size: 12))
                .accessibilityElement(children: .combine)
            }
            HStack(spacing: Spacing.xs) {
                Link(destination: page) {
                    HStack(spacing: 3) {
                        Text(l10n("update.openRelease"))
                        Image(systemName: "arrow.up.right")
                            .imageScale(.small)
                            .fontWeight(.semibold)
                            .accessibilityHidden(true)
                    }
                }
                .help(page.absoluteString)
                Spacer(minLength: Spacing.xs)
                Button(l10n("update.later")) { close() }
                    .keyboardShortcut(.cancelAction)
                Button(primary) { retry() }
                    .keyboardShortcut(.defaultAction)
            }
        }
    }
}

/// Determinate bar with "Downloading… 42%", then verifying and installing.
private struct InstallProgress: View {
    let model: AppModel
    let installation: UpdateController.Installation

    var body: some View {
        let l10n = model.l10n
        let (fraction, label) = describe(l10n)
        VStack(alignment: .leading, spacing: 6) {
            ProgressView(value: fraction)
                .progressViewStyle(.linear)
                .animation(Motion.fade, value: fraction)
            Text(label)
                .font(.system(size: 11.5))
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .contentTransition(.numericText())
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(label)
        .accessibilityValue(l10n("mac.settings.percentValue", Int((fraction * 100).rounded())))
    }

    private func describe(_ l10n: Localizer) -> (Double, String) {
        switch installation {
        case .downloading(let fraction):
            let percent = l10n("mac.settings.percentValue", Int((fraction * 100).rounded(.down)))
            return (fraction, l10n("update.downloading", percent))
        case .verifying:
            return (1, l10n("update.verifying"))
        case .relaunching:
            return (1, l10n("mac.update.relaunching"))
        case .installing, .idle, .failed:
            return (1, l10n("update.installing"))
        }
    }
}

extension UpdateWindowMode {
    /// Coarse state for animations; download progress alone does not animate the layout.
    var phase: Int {
        switch self {
        case .idle: 0
        case .checking: 1
        case .upToDate: 2
        case .checkFailed: 3
        case .offer: 4
        case .install(_, let installation):
            if case .failed = installation { 6 } else { 5 }
        }
    }
}
