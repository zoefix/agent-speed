import AgentSpeedCore
import SwiftUI

/// `report.sections` (DESIGN 11, What's sent): one switch per optional section. A section the service does not list is
/// off and disabled; the traffic switch waits for traffic measuring.
struct ReportSectionsSection: View {
    let model: AppModel

    var body: some View {
        let l10n = model.l10n
        let report = model.report
        Section {
            ReportSectionToggles(model: model, selection: Binding(
                get: { report.preferences.sections },
                set: { new in
                    for section in ReportSection.allCases where new.contains(section) != report.preferences.sections.contains(section) {
                        report.setSection(section, on: new.contains(section))
                    }
                }
            ), discovery: report.discovery)
            HStack {
                Spacer()
                Button(l10n("report.preview")) { ReportPreviewWindow.show(model: model) }
                    .disabled(report.selectedService == nil)
            }
        } header: {
            Text(l10n("report.sections"))
        } footer: {
            SectionFootnote(l10n("report.sections.hint"))
        }
    }
}

/// The four section switches, shared by the pane and the consent sheet.
struct ReportSectionToggles: View {
    let model: AppModel
    @Binding var selection: Set<ReportSection>
    /// The service's document; nil while it is unknown (every switch stays usable).
    let discovery: ReportDiscovery?

    /// The caption under the models switch (DESIGN 11, What's sent): `report.section.models.perfScore` while the known
    /// document lists `models` and advertises the `perfScore` extension, and before any document is known;
    /// `report.section.models.thinking` while it advertises only the retired `taskScore`; none otherwise. The switch's
    /// own state does not matter.
    static func modelsCaptionKey(_ discovery: ReportDiscovery?) -> String? {
        guard let discovery else { return "report.section.models.perfScore" }
        guard discovery.sections.contains(.models) else { return nil }
        if discovery.extensions?.perfScore == true { return "report.section.models.perfScore" }
        if discovery.extensions?.taskScore == true { return "report.section.models.thinking" }
        return nil
    }

    var body: some View {
        let l10n = model.l10n
        let trafficOn = model.settings.trafficEnabled
        let modelsCaption = Self.modelsCaptionKey(discovery)
        ForEach(ReportSection.allCases, id: \.self) { section in
            let listed = discovery?.sections.contains(section) ?? true
            let isTraffic = section == .traffic
            Toggle(isOn: Binding(
                get: { listed && selection.contains(section) },
                set: { on in
                    if on { selection.insert(section) } else { selection.remove(section) }
                }
            )) {
                VStack(alignment: .leading, spacing: 2) {
                    RowTitle(l10n(section.titleKey))
                    if !listed {
                        Footnote(l10n("report.section.notAccepted"))
                    } else if section == .models, let modelsCaption {
                        Footnote(l10n(modelsCaption))
                    } else if isTraffic, !trafficOn {
                        Footnote(l10n("report.section.trafficOff"))
                    }
                }
            }
            .disabled(!listed || (isTraffic && !trafficOn))
        }
    }
}

extension ReportSection {
    var titleKey: String { "report.section.\(rawValue)" }
}
