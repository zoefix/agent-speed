import Foundation

/// The stored form of the location lists (DESIGN 5, settings key `sourceLocations`):
/// `{"version":1,"lists":{"codex":[{"automatic":true},{"path":"/Users/me/work/codex/sessions"}]}}`. A folder whose list
/// is `[automatic]` is left out; the automatic entry is a marker, never a path.
extension SourceLocations {
    public static let documentVersion = 1

    /// Stable JSON with sorted keys.
    public func data() -> Data {
        var stored = unknownLists
        for folder in TranscriptFolder.allCases where self[folder] != Self.automaticOnly {
            stored[folder.rawValue] = .array(self[folder].map(Self.json))
        }
        let document = PreservedJSON.object(["version": .int(Int64(Self.documentVersion)), "lists": .object(stored)])
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return (try? encoder.encode(document)) ?? Data()
    }

    /// Nil when the data is not a JSON object. Tolerant otherwise: an entry that is neither `{"automatic":true}` nor
    /// `{"path":<absolute path>}` is skipped and each list is normalized; lists of folders this version does not know
    /// are kept as stored.
    public init?(data: Data) {
        guard let document = try? JSONDecoder().decode(PreservedJSON.self, from: data),
              case .object(let members) = document else { return nil }
        self.init()
        guard case .object(let lists)? = members["lists"] else { return }
        for (name, value) in lists {
            guard let folder = TranscriptFolder(rawValue: name) else {
                unknownLists[name] = value
                continue
            }
            guard case .array(let items) = value else { continue }
            self[folder] = items.compactMap(Self.entry)
        }
    }

    private static func json(_ location: DataLocation) -> PreservedJSON {
        switch location {
        case .automatic: .object(["automatic": .bool(true)])
        case .path(let path): .object(["path": .string(path)])
        }
    }

    private static func entry(_ value: PreservedJSON) -> DataLocation? {
        guard case .object(let members) = value else { return nil }
        if case .bool(true)? = members["automatic"] { return .automatic }
        if case .string(let path)? = members["path"], let standardized = standardized(path) { return .path(standardized) }
        return nil
    }
}
