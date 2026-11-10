import AgentSpeedCore
import Foundation

/// The Add / Edit sheet's fields (DESIGN 13) and their REMOTE 2.1 checks, as the user types.
struct RemoteHostDraft: Equatable {
    var destination: String
    var name: String
    var relayFolder: String
    /// The host's Claude Code status line bridge (REMOTE 11.3).
    var quotaClaudeCli: Bool
    /// The host being edited; nil while adding.
    let original: RemoteHost?

    init(editing host: RemoteHost? = nil) {
        original = host
        destination = host?.destination ?? ""
        name = host?.name ?? ""
        relayFolder = host?.relayFolder ?? ""
        quotaClaudeCli = host?.quotaClaudeCli ?? false
    }

    var isEditing: Bool { original != nil }

    /// Spaces and line breaks around a pasted destination are dropped; inside it they stay errors.
    var cleanDestination: String { destination.trimmingCharacters(in: .whitespacesAndNewlines) }
    var cleanFolder: String { relayFolder.trimmingCharacters(in: .whitespacesAndNewlines) }

    var destinationIssue: RemoteFieldIssue? { RemoteHostValidation.destinationIssue(cleanDestination) }

    /// An empty folder is the default one.
    var folderIssue: RemoteFieldIssue? {
        cleanFolder.isEmpty ? nil : RemoteHostValidation.folderIssue(cleanFolder)
    }

    /// The name keeps at most 32 characters after trimming (REMOTE 2.1).
    var nameIsTooLong: Bool {
        name.trimmingCharacters(in: .whitespacesAndNewlines).count > RemoteHostValidation.nameLimit
    }

    var isValid: Bool { destinationIssue == nil && folderIssue == nil && !nameIsTooLong }

    /// The host the fields describe: a new id (enabled) while adding, else the edited host's id and switch.
    func host(newId: String = UUID().uuidString) -> RemoteHost {
        RemoteHost(id: original?.id ?? newId, destination: cleanDestination, name: name,
                   enabled: original?.enabled ?? true, relayFolder: cleanFolder.isEmpty ? nil : cleanFolder,
                   quotaClaudeCli: quotaClaudeCli)
    }

    /// The name the host would get without a custom one, made distinct from the other hosts (the name's placeholder).
    func automaticName(in document: RemoteHostsDocument) -> String {
        guard destinationIssue == nil else { return "" }
        var unnamed = host(newId: original?.id ?? "00000000-0000-4000-8000-000000000000")
        unnamed.name = nil
        var preview = document
        if original != nil {
            preview.update(unnamed)
        } else if !preview.add(unnamed) {
            return unnamed.automaticName
        }
        return preview.displayName(of: unnamed.id) ?? unnamed.automaticName
    }
}
