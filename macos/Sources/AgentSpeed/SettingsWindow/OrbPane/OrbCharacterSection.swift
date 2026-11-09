import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// The selected `character` orb's character (DESIGN 5, Character; CHARACTERS 2, 7): Auto or one installed character,
/// the automatic download's progress with Stop while it runs, then every character of the catalog and every installed
/// one with its portrait, name, artist and size, and Download / Update / Remove.
struct OrbCharacterSection: View {
    /// Scroll target of the context menu's "Download more…".
    static let anchor = "orbCharacters"

    let model: AppModel
    let orb: OrbConfig

    var body: some View {
        let l10n = model.l10n
        let library = model.characters
        Section {
            PopupRow(title: l10n("characters.choice"), caption: l10n("characters.choice.hint"),
                     selection: Binding(get: { orb.character ?? "" }, set: { model.orbs.setCharacter($0.isEmpty ? nil : $0, of: orb.id) })) {
                Text(l10n("characters.auto")).tag("")
                if !library.installed.isEmpty { Divider() }
                ForEach(library.installed) { pack in
                    Text(Self.name(pack.manifest.name, l10n.language)).tag(pack.id)
                }
            }
            if library.autoTotal > 0 { autoDownload }
            ForEach(rows) { row in
                CharacterRow(model: model, row: row)
            }
            if rows.isEmpty { emptyState }
        } header: {
            Text(l10n("characters.title"))
        } footer: {
            footer
        }
        .onAppear { library.refreshForList() }
    }

    /// Catalog entries in catalog order, then installed packs the catalog does not list.
    private var rows: [CharacterRow.Model] {
        let library = model.characters
        var out = (library.catalog?.characters ?? []).map { entry in
            CharacterRow.Model(id: entry.id, entry: entry, pack: library.installed(entry.id))
        }
        let listed = Set(out.map(\.id))
        out += library.installed.filter { !listed.contains($0.id) }.map { CharacterRow.Model(id: $0.id, entry: nil, pack: $0) }
        return out
    }

    /// `characters.auto.status` with the run's progress, and Stop.
    private var autoDownload: some View {
        let l10n = model.l10n
        let library = model.characters
        return HStack(spacing: Spacing.s) {
            ProgressView(value: Double(max(0, library.autoPosition - 1)), total: Double(max(1, library.autoTotal)))
                .frame(width: 96)
            Text(l10n("characters.auto.status", library.autoPosition, library.autoTotal))
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .monospacedDigit()
            Spacer(minLength: Spacing.xs)
            Button(l10n("characters.auto.stop")) { library.stopAutomatic() }
        }
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private var emptyState: some View {
        let l10n = model.l10n
        HStack(spacing: Spacing.xs) {
            if model.characters.catalogState == .loading {
                ProgressView().controlSize(.small)
                Text(l10n("characters.loading")).foregroundStyle(.secondary)
            } else {
                Text(l10n(model.settings.networkEnabled ? "characters.unavailable" : "characters.offline"))
                    .foregroundStyle(.secondary)
                Spacer()
                if model.settings.networkEnabled {
                    Button(l10n("characters.retry")) { model.characters.refreshCatalog(maxAge: 0) }
                }
            }
        }
        .font(.system(size: 12))
    }

    @ViewBuilder
    private var footer: some View {
        let l10n = model.l10n
        HStack(spacing: Spacing.xs) {
            SectionFootnote(l10n("characters.footer"))
            Spacer()
            Button(l10n("characters.openFolder")) {
                let folder = model.characters.store.root
                try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                NSWorkspace.shared.open(folder)
            }
            .buttonStyle(.link)
            .font(.system(size: 11))
        }
    }

    static func name(_ names: CharacterNames, _ language: AppLanguage) -> String {
        switch language {
        case .zhHant: names.zhHant
        case .zhHans: names.zhHans
        case .en: names.en
        case .ja: names.ja
        }
    }
}

/// One character: portrait, name, artist credit, size and its action.
private struct CharacterRow: View {
    struct Model: Identifiable {
        let id: String
        let entry: CharacterCatalog.Entry?
        let pack: InstalledCharacter?
    }

    let model: AppModel
    let row: Model

    var body: some View {
        let l10n = model.l10n
        let library = model.characters
        let names = row.pack?.manifest.name ?? row.entry?.name
        let artist = row.pack?.manifest.artist ?? row.entry?.artist
        HStack(alignment: .center, spacing: Spacing.s) {
            CharacterPortrait(pack: row.pack, height: 52)
                .frame(width: 42)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Circle().fill(accent).frame(width: 7, height: 7)
                    Text(names.map { OrbCharacterSection.name($0, l10n.language) } ?? row.id)
                        .font(.system(size: 13, weight: .medium))
                    if row.pack?.isDevelopment == true {
                        Text(l10n("characters.development")).font(.system(size: 10)).foregroundStyle(.tertiary)
                    }
                }
                HStack(spacing: 6) {
                    if let artist {
                        if let link = artist.link {
                            Link(l10n("characters.artist", artist.name), destination: link)
                        } else {
                            Text(l10n("characters.artist", artist.name))
                        }
                    }
                    if let size = row.entry?.size {
                        Text(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))
                    }
                }
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                if let error = library.errors[row.id] {
                    Text(l10n(error)).font(.system(size: 11)).foregroundStyle(.red)
                }
            }
            Spacer(minLength: Spacing.xs)
            action
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }

    private var accent: Color {
        let hex = row.pack?.manifest.accent ?? row.entry?.accent
        guard let rgb = hex.flatMap(CharacterManifest.rgb) else { return .secondary }
        return Color(red: rgb.r, green: rgb.g, blue: rgb.b)
    }

    @ViewBuilder
    private var action: some View {
        let l10n = model.l10n
        let library = model.characters
        if let download = library.downloads[row.id] {
            HStack(spacing: 6) {
                if download.installing {
                    ProgressView().controlSize(.small)
                    Text(l10n("characters.installing")).font(.system(size: 11)).foregroundStyle(.secondary)
                } else {
                    ProgressView(value: download.progress).frame(width: 72)
                    Button { library.cancel(row.id) } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.borderless)
                        .help(l10n("characters.cancel"))
                        .accessibilityLabel(l10n("characters.cancel"))
                }
            }
        } else if let pack = row.pack {
            HStack(spacing: 6) {
                if library.hasUpdate(pack.id), let entry = row.entry {
                    Button(l10n("characters.update")) { library.download(entry) }
                }
                if !pack.isDevelopment {
                    Button(l10n("characters.remove"), role: .destructive) { library.remove(pack.id) }
                }
            }
        } else if let entry = row.entry {
            Button(l10n("characters.download")) { library.download(entry) }
                .buttonStyle(.borderedProminent)
                .disabled(!model.settings.networkEnabled)
        }
    }
}

/// A character's portrait: the installed pack's `preview.png`, else a quiet silhouette (nothing is fetched for a
/// character that is not downloaded; NETWORK 1).
struct CharacterPortrait: View {
    var pack: InstalledCharacter?
    var height: CGFloat

    var body: some View {
        Group {
            if let pack, let image = NSImage(contentsOf: pack.previewURL) {
                Image(nsImage: image).resizable().interpolation(.high).scaledToFit()
            } else {
                silhouette
            }
        }
        .frame(height: height)
        .accessibilityHidden(true)
    }

    private var silhouette: some View {
        Image(systemName: "figure.stand")
            .resizable()
            .scaledToFit()
            .padding(height * 0.14)
            .foregroundStyle(.tertiary)
    }
}
