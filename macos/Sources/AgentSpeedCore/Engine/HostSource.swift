import Foundation

/// One host and one source: the cell that live values are kept in for host subsets (DESIGN 13, METRICS 6.8 evaluated
/// over (host, source) cells).
public struct HostSource: Hashable, Sendable {
    /// `local`, or a remote host's id.
    public var host: String
    public var source: SourceKind

    public init(host: String, source: SourceKind) {
        self.host = host
        self.source = source
    }

    public static func local(_ source: SourceKind) -> HostSource {
        HostSource(host: RemoteHost.localId, source: source)
    }
}

/// A set of sources on a set of hosts (nil = every host): the key of `SpeedSnapshot.subsetPeaks` and of the subsets
/// `LiveMonitor.setSubsets` requests. With every host it is exactly the source set of METRICS 6.8.
public struct SubsetKey: Hashable, Sendable {
    public var sources: SourceSet
    public var hosts: Set<String>?

    public init(_ sources: SourceSet, hosts: Set<String>? = nil) {
        self.sources = sources
        self.hosts = hosts
    }

    public func contains(_ cell: HostSource) -> Bool {
        sources.contains(cell.source) && (hosts?.contains(cell.host) ?? true)
    }
}

extension Dictionary where Key == SubsetKey {
    /// The value of `sources` on every host.
    public subscript(_ sources: SourceSet) -> Value? {
        self[SubsetKey(sources)]
    }
}
