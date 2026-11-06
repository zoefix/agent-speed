import AgentSpeedCore
import Foundation

/// Automatic orb names (DESIGN 2b `name`), built from localized pieces the app supplies.
public struct OrbNamer: Sendable {
    /// The display name of one source (`source.*`).
    public var sourceName: @Sendable (SourceKind) -> String
    /// `orbs.sources.all`.
    public var allSources: String
    /// `orbs.name.pair` (`%@ + %@`).
    public var pair: @Sendable (String, String) -> String
    /// `orbs.name.more` (`%@ +%d`): the first source and the number of the others.
    public var more: @Sendable (String, Int) -> String
    /// This computer and the remote hosts in settings order (DESIGN 13); without a remote host no name mentions one.
    public var hosts: [OrbHostChoice]

    public init(
        sourceName: @escaping @Sendable (SourceKind) -> String,
        allSources: String,
        pair: @escaping @Sendable (String, String) -> String,
        more: @escaping @Sendable (String, Int) -> String,
        hosts: [OrbHostChoice] = []
    ) {
        self.sourceName = sourceName
        self.allSources = allSources
        self.pair = pair
        self.more = more
        self.hosts = hosts
    }

    /// The custom name, else the automatic one.
    public func displayName(_ config: OrbConfig) -> String {
        config.name ?? automaticName(config.sources, hosts: config.hosts)
    }

    /// The sources' name followed by the hosts' (DESIGN 13).
    public func automaticName(_ sources: OrbSourceSelection, hosts selection: OrbHostSelection) -> String {
        automaticName(sources) + hostSuffix(selection)
    }

    /// ` · <host>` for one host, ` · <first> +N` for several, in settings order; nothing for all hosts, for ids this
    /// computer does not know, or while no remote host exists.
    public func hostSuffix(_ selection: OrbHostSelection) -> String {
        guard OrbHostChoice.pickerShown(hosts), case let .explicit(ids) = selection else { return "" }
        let known = hosts.filter { ids.contains($0.id) }
        guard let first = known.first else { return "" }
        return " · " + (known.count == 1 ? first.name : more(first.name, known.count - 1))
    }

    /// All sources → `orbs.sources.all`; one → its name; two → `A + B`; three or more → `A +n` with n the others,
    /// in source order. Unknown keys are ignored; a list without a known source reads as all sources.
    public func automaticName(_ sources: OrbSourceSelection) -> String {
        let known = sources.knownSources
        switch known.count {
        case 0: return allSources
        case 1: return sourceName(known[0])
        case 2: return pair(sourceName(known[0]), sourceName(known[1]))
        default: return more(sourceName(known[0]), known.count - 1)
        }
    }

    /// A duplicate's custom name: ` 2` appended (kept within the 32-character limit); automatic stays automatic.
    public static func duplicateName(_ name: String?) -> String? {
        guard let name = OrbConfig.cleanName(name) else { return nil }
        let suffix = " 2"
        let room = OrbConfig.nameLimit - suffix.count
        let base = name.count > room ? String(name.prefix(room)).trimmingCharacters(in: .whitespaces) : name
        return base + suffix
    }
}
