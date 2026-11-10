import Foundation

/// A settings row group with its own location list (METRICS 2.1): Claude Code and Claude Desktop share `claude`, every
/// other source has its own. The raw values are the member names of the stored document.
public enum TranscriptFolder: String, CaseIterable, Sendable {
    case codex
    case claude
    case dsh
    case opencode
    case gemini
    case omp
    case antigravity
    case workbuddy
    case workbuddyAi
    case zcode
    case kimi
    case grokBuild
    case pi
    case openclaw
    case hermes
    case droid
    case devin
    case qoder
    case qoderCli

    /// The sources read through this folder's list, in source order.
    public var sources: [SourceKind] { SourceKind.allCases.filter { $0.transcriptFolder == self } }

    /// omp, Antigravity, Pi and OpenClaw: one chosen folder is narrower than the automatic set, so choosing the default
    /// location adds it as a user location instead of bringing the automatic entry back.
    public var automaticIsWiderThanDefault: Bool { self == .omp || self == .antigravity || self == .pi || self == .openclaw }

    /// The picker also accepts a database file: `openclaw-agent.sqlite` (DESIGN 5).
    public var acceptsDatabaseFile: Bool { self == .openclaw }
}

extension SourceKind {
    /// The folder whose location list this source is read from.
    public var transcriptFolder: TranscriptFolder {
        switch self {
        case .codex: .codex
        case .claudeCode, .claudeDesktop: .claude
        case .deepseek: .dsh
        case .opencode: .opencode
        case .gemini: .gemini
        case .omp: .omp
        case .antigravity: .antigravity
        case .workbuddy: .workbuddy
        case .workbuddyAi: .workbuddyAi
        case .zcode: .zcode
        case .kimi: .kimi
        case .grokBuild: .grokBuild
        case .pi: .pi
        case .openclaw: .openclaw
        case .hermes: .hermes
        case .droid: .droid
        case .devin: .devin
        case .qoder: .qoder
        case .qoderCli: .qoderCli
        }
    }
}

/// One entry of a location list (METRICS 2.1).
public enum DataLocation: Hashable, Sendable {
    /// Every automatic root of the source, derived from the environment (METRICS 2).
    case automatic
    /// A location the user added: an absolute, standardized path of a folder or (OpenCode, ZCode, OpenClaw) a database.
    case path(String)

    public var path: String? {
        if case .path(let path) = self { return path }
        return nil
    }
}

/// Every source's location list (METRICS 2.1). A folder without a list of its own reads `[automatic]`.
public struct SourceLocations: Equatable, Sendable {
    public static let maxEntries = 8
    public static let automaticOnly: [DataLocation] = [.automatic]

    private var lists: [TranscriptFolder: [DataLocation]] = [:]
    /// Lists of folders this version does not know, kept as stored so a newer version's choice survives (document).
    var unknownLists: [String: PreservedJSON] = [:]

    public init() {}

    public init(_ lists: [TranscriptFolder: [DataLocation]]) {
        for (folder, list) in lists { self[folder] = list }
    }

    /// The lists of the single-path settings of earlier versions: a stored path becomes the list `[path]`, without the
    /// automatic entry, so exactly the same folders are read after an upgrade.
    public init(legacy paths: [TranscriptFolder: String]) {
        for (folder, path) in paths { self[folder] = [.path(path)] }
    }

    public subscript(folder: TranscriptFolder) -> [DataLocation] {
        get { lists[folder] ?? Self.automaticOnly }
        set {
            let list = Self.normalized(newValue)
            lists[folder] = list == Self.automaticOnly ? nil : list
        }
    }

    /// True when every folder reads its automatic roots only.
    public var isAutomaticOnly: Bool { lists.isEmpty }

    public static func == (lhs: SourceLocations, rhs: SourceLocations) -> Bool {
        lhs.lists == rhs.lists
    }

    /// The rules every stored or edited list follows: paths absolute and standardized (no `.`/`..`, no trailing
    /// slash), each entry once, the automatic entry first when present, at most `maxEntries`; a list left empty reads
    /// `[automatic]`.
    public static func normalized(_ list: [DataLocation]) -> [DataLocation] {
        var seen: Set<DataLocation> = []
        var result: [DataLocation] = []
        for entry in list {
            let entry: DataLocation? = switch entry {
            case .automatic: .automatic
            case .path(let path): standardized(path).map(DataLocation.path)
            }
            guard let entry, seen.insert(entry).inserted else { continue }
            if entry == .automatic { result.insert(entry, at: 0) } else { result.append(entry) }
        }
        result = Array(result.prefix(maxEntries))
        return result.isEmpty ? automaticOnly : result
    }

    /// An absolute path, standardized; nil for an empty or relative one.
    public static func standardized(_ path: String) -> String? {
        guard path.hasPrefix("/") else { return nil }
        return TranscriptDiscovery.normalizedRoot(URL(fileURLWithPath: path, isDirectory: false).standardized.path)
    }
}
