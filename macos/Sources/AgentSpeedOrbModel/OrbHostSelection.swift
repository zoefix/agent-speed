import AgentSpeedCore
import Foundation

/// The hosts an orb or board tracks (DESIGN 13 `hosts`): every host, including ones added later, or an explicit list
/// of host ids (`local` for this computer, remote host ids). Ids this computer does not know (a host of another Mac's
/// settings, a newer version's) are kept and ignored, like unknown sources.
public enum OrbHostSelection: Equatable, Hashable, Sendable {
    case all
    /// `local` first, then the others in their order; never empty, no duplicates.
    case explicit([String])

    /// `local` first, duplicates and empty ids dropped; empty → `.all`.
    public static func normalized(_ ids: [String]) -> OrbHostSelection {
        var seen: Set<String> = []
        let unique = ids.filter { !$0.isEmpty && seen.insert($0).inserted }
        guard !unique.isEmpty else { return .all }
        let local = unique.filter { $0 == RemoteHost.localId }
        return .explicit(local + unique.filter { $0 != RemoteHost.localId })
    }

    public var isAll: Bool { self == .all }

    /// The ids of an explicit list; empty for `.all`.
    public var ids: [String] {
        guard case let .explicit(ids) = self else { return [] }
        return ids
    }

    /// The engine's host set; nil for `.all`.
    public var hostSet: Set<String>? {
        guard case let .explicit(ids) = self else { return nil }
        return Set(ids)
    }

    public func tracks(_ hostId: String) -> Bool {
        switch self {
        case .all: true
        case let .explicit(ids): ids.contains(hostId)
        }
    }

    /// The selection after the user toggles `hostId`: on an all-hosts selection it becomes that single host; otherwise
    /// the host is added or removed. Removing the last host is refused (nil).
    public func toggling(_ hostId: String) -> OrbHostSelection? {
        guard case let .explicit(ids) = self else { return .explicit([hostId]) }
        if ids.contains(hostId) {
            let remaining = ids.filter { $0 != hostId }
            guard !remaining.isEmpty else { return nil }
            return .normalized(remaining)
        }
        return .normalized(ids + [hostId])
    }

    /// A host removed from the settings leaves the selection; a selection left empty becomes `.all` (REMOTE 2.4).
    public func removing(_ hostId: String) -> OrbHostSelection {
        guard case let .explicit(ids) = self else { return self }
        return .normalized(ids.filter { $0 != hostId })
    }

    /// The ids in settings order (`order`: `local`, then the remote hosts as added); ids `order` does not know stay,
    /// after the known ones.
    public func ordered(by order: [String]) -> OrbHostSelection {
        guard case let .explicit(ids) = self else { return self }
        var rank: [String: Int] = [:]
        for (index, id) in order.enumerated() where rank[id] == nil { rank[id] = index }
        let known = ids.filter { rank[$0] != nil }.sorted { rank[$0, default: 0] < rank[$1, default: 0] }
        return .normalized(known + ids.filter { rank[$0] == nil })
    }

    /// The pickers' rule (DESIGN 13) over the hosts this computer knows (`known`, in settings order): on an all-hosts
    /// selection the host becomes the only one; otherwise it is added or removed, in settings order. Removing the last
    /// known host is refused (nil), unknown ids alone never count as a selection.
    public func toggling(_ hostId: String, known: [String]) -> OrbHostSelection? {
        guard case let .explicit(ids) = self else { return OrbHostSelection.explicit([hostId]).ordered(by: known) }
        if ids.contains(hostId) {
            let remaining = ids.filter { $0 != hostId }
            guard remaining.contains(where: known.contains) else { return nil }
            return OrbHostSelection.normalized(remaining).ordered(by: known)
        }
        return OrbHostSelection.normalized(ids + [hostId]).ordered(by: known)
    }

    /// The rows of a Hosts picker or submenu: every choice in its order, checked when an explicit selection tracks it;
    /// the last tracked known host cannot be unchecked.
    public func entries(_ choices: [OrbHostChoice]) -> [OrbHostEntry] {
        let checked = choices.filter { !isAll && tracks($0.id) }
        return choices.map { choice in
            let isChecked = !isAll && tracks(choice.id)
            return OrbHostEntry(id: choice.id, name: choice.name, checked: isChecked,
                                enabled: !(isChecked && checked.count == 1))
        }
    }
}

/// A host an orb or board can track (DESIGN 13): this computer (`local`) or a remote host of the settings, with its
/// display name.
public struct OrbHostChoice: Equatable, Hashable, Sendable {
    public var id: String
    public var name: String

    public init(id: String, name: String) {
        self.id = id
        self.name = name
    }

    /// Choices exist only while a remote host is configured (DESIGN 13: invisible until the first host is added).
    public static func pickerShown(_ choices: [OrbHostChoice]) -> Bool {
        choices.contains { $0.id != RemoteHost.localId }
    }
}

/// One row of a Hosts picker or submenu.
public struct OrbHostEntry: Equatable, Sendable {
    public var id: String
    public var name: String
    public var checked: Bool
    /// False for the last tracked host (`remote.hosts.lastOne`).
    public var enabled: Bool

    public init(id: String, name: String, checked: Bool, enabled: Bool) {
        self.id = id
        self.name = name
        self.checked = checked
        self.enabled = enabled
    }
}

extension OrbHostSelection: Codable {
    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let text = try? container.decode(String.self) {
            self = text == "all" ? .all : .normalized([text])
            return
        }
        self = .normalized((try? container.decode([String].self)) ?? [])
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .all: try container.encode("all")
        case let .explicit(ids): try container.encode(ids)
        }
    }
}

extension OrbSourceSelection {
    /// The engine's subset for these sources on `hosts` (METRICS 6.8, DESIGN 13); nil for every source on every host,
    /// which reads the snapshot's own values.
    public func subsetKey(hosts: OrbHostSelection) -> SubsetKey? {
        guard !isAll || !hosts.isAll else { return nil }
        return SubsetKey(sourceSet ?? .everything, hosts: hosts.hostSet)
    }
}
