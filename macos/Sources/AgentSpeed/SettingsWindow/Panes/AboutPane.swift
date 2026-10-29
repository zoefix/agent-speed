import SwiftUI

struct AboutPane: View {
    let model: AppModel
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let l10n = model.l10n
        VStack(spacing: 0) {
            AppMark(size: 96)
                .padding(.bottom, Spacing.s)

            Text(l10n("app.name"))
                .font(.title2.weight(.semibold))
            Text(l10n("mac.settings.about.tagline"))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.top, Spacing.xxs)
            Text(l10n("settings.about.version", AppInfo.version))
                .font(.callout)
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
                .padding(.top, Spacing.xs)

            VStack(spacing: Spacing.xxs) {
                Link(destination: AppInfo.repositoryURL) {
                    Label(l10n("settings.about.github"), systemImage: "arrow.up.right")
                        .labelStyle(TrailingIconLabelStyle())
                }
                .help(AppInfo.repositoryURL.absoluteString)
                Text(l10n("mac.settings.about.license"))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            .padding(.top, Spacing.l)

            NetworkSettingsCard(model: model)
                .padding(.top, Spacing.xl)

            UpdateSettingsCard(model: model)
                .padding(.top, Spacing.s)

            privacyCard(privacyParagraphs)
                .padding(.top, Spacing.s)

            VStack(spacing: Spacing.xxs) {
                Text(l10n("settings.about.update"))
                Text(l10n("mac.settings.about.copyright"))
            }
            .font(.subheadline)
            .foregroundStyle(.secondary)
            .multilineTextAlignment(.center)
            .padding(.top, Spacing.m)
        }
        .padding(.horizontal, Spacing.xxl)
        .padding(.top, Spacing.xl)
        .padding(.bottom, Spacing.l)
        .frame(maxWidth: .infinity)
        .background {
            Form {}
                .settingsForm()
                .accessibilityHidden(true)
        }
    }

    /// DESIGN 11, About: while reporting is on no sentence claims that nothing is sent. The performance score's paragraph
    /// follows the traffic one either way (DESIGN 12): it says itself what only live reporting sends.
    private var privacyParagraphs: [String] {
        let l10n = model.l10n
        let report = model.report
        guard report.isOn else {
            return [l10n("privacy.statement"), l10n("privacy.traffic"), l10n("privacy.perfScore"), l10n("privacy.report")]
        }
        let host = report.destinationHost ?? ""
        return [l10n("privacy.statement.report", host), l10n("privacy.traffic.report"), l10n("privacy.perfScore"),
                l10n("privacy.report")]
    }

    private func privacyCard(_ paragraphs: [String]) -> some View {
        let shape = RoundedRectangle(cornerRadius: 12, style: .continuous)
        return HStack(alignment: .top, spacing: Spacing.s) {
            Image(systemName: "hand.raised.fill")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 26, height: 26)
                .background(Color.accentColor, in: RoundedRectangle(cornerRadius: 7, style: .continuous))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: Spacing.xs) {
                ForEach(paragraphs, id: \.self) { Text($0) }
            }
            .font(.callout)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(Spacing.m)
        .background(Color.primary.opacity(0.04), in: shape)
        .overlay {
            shape.strokeBorder(Color.primary.opacity(contrast == .increased ? 0.3 : 0.08), lineWidth: 1)
        }
        .accessibilityElement(children: .combine)
    }
}

/// Title first, then a small icon: "View on GitHub ↗".
private struct TrailingIconLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 3) {
            configuration.title
            configuration.icon
                .imageScale(.small)
                .font(.body.weight(.semibold))
        }
    }
}
