import Foundation

/// `<data>/quota/estimate.json` (QUOTAS 10.4): the last 8 days of observations, the closed instances' intervals and
/// the key timelines. Account keys are stored, ids never.
public struct QuotaEstimateStore: Codable, Equatable, Sendable {
    public struct Observation: Codable, Equatable, Sendable {
        public var account: String
        public var source: String
        public var kind: String
        public var name: String?
        public var minutes: Int
        public var t: Int64
        public var tLo: Int64
        public var p: Double
        public var R: Int64
        public var plan: String?
        public var tier: String?

        init(_ o: QuotaObservation) {
            account = o.account
            source = o.source
            kind = o.kind.rawValue
            name = o.name
            minutes = o.minutes
            t = o.t
            tLo = o.tLo
            p = o.p
            R = o.resetsMs
            plan = o.plan
            tier = o.tier
        }

        var observation: QuotaObservation? {
            guard let kind = QuotaWindowKind(rawValue: kind), minutes > 0 else { return nil }
            return QuotaObservation(account: account, source: source, kind: kind, name: name, minutes: minutes, t: t,
                                    tLo: tLo, p: p, resetsMs: R, plan: plan, tier: tier)
        }

        enum CodingKeys: String, CodingKey { case account, source, kind, name, minutes, t, tLo, p, R, plan, tier }

        public func encode(to encoder: any Encoder) throws {
            var c = encoder.container(keyedBy: CodingKeys.self)
            try c.encode(account, forKey: .account)
            try c.encode(source, forKey: .source)
            try c.encode(kind, forKey: .kind)
            try c.encode(name, forKey: .name)
            try c.encode(minutes, forKey: .minutes)
            try c.encode(t, forKey: .t)
            try c.encode(tLo, forKey: .tLo)
            try c.encode(p, forKey: .p)
            try c.encode(R, forKey: .R)
            try c.encode(plan, forKey: .plan)
            try c.encode(tier, forKey: .tier)
        }
    }

    public struct Instance: Codable, Equatable, Sendable {
        public var `class`: [String?]
        public var account: String
        public var R: Int64
        public var lo: Double
        /// Nil for unbounded.
        public var hi: Double?
        public var pMax: Double
        public var tLast: Int64

        enum CodingKeys: String, CodingKey { case `class`, account, R, lo, hi, pMax, tLast }

        public func encode(to encoder: any Encoder) throws {
            var c = encoder.container(keyedBy: CodingKeys.self)
            try c.encode(`class`, forKey: .class)
            try c.encode(account, forKey: .account)
            try c.encode(R, forKey: .R)
            try c.encode(lo, forKey: .lo)
            try c.encode(hi, forKey: .hi)
            try c.encode(pMax, forKey: .pMax)
            try c.encode(tLast, forKey: .tLast)
        }

        var capClass: QuotaCapClass? { QuotaCapClass(list: `class`) }

        var bounds: QuotaInstanceBounds {
            QuotaInstanceBounds(resetsMs: R, lo: lo, hi: hi ?? .infinity, pMax: pMax, tLast: tLast, offset: false)
        }
    }

    public struct Key: Codable, Equatable, Sendable {
        public var source: String
        public var at: Int64
        public var key: String
        public var plan: String?
        public var tier: String?
        /// A `claudeCode` entry's organization, as its `claudeDesktop` key.
        public var orgKey: String?

        enum CodingKeys: String, CodingKey { case source, at, key, plan, tier, orgKey }

        public func encode(to encoder: any Encoder) throws {
            var c = encoder.container(keyedBy: CodingKeys.self)
            try c.encode(source, forKey: .source)
            try c.encode(at, forKey: .at)
            try c.encode(key, forKey: .key)
            try c.encode(plan, forKey: .plan)
            try c.encode(tier, forKey: .tier)
            try c.encodeIfPresent(orgKey, forKey: .orgKey)
        }
    }

    public var version: Int
    public var observations: [Observation]
    public var instances: [Instance]
    public var keys: [Key]

    public init(version: Int = QuotaEstimateConstants.storeVersion, observations: [Observation] = [],
                instances: [Instance] = [], keys: [Key] = []) {
        self.version = version
        self.observations = observations
        self.instances = instances
        self.keys = keys
    }

    public static let empty = QuotaEstimateStore()

    // MARK: Files

    public static func file(data: URL) -> URL {
        data.appendingPathComponent("quota/estimate.json")
    }

    /// The stored file of this version; nil when it is missing, of another version (version 1 keyed accounts with a
    /// random salt) or does not parse (the next write replaces it).
    public static func decode(_ data: Data) -> QuotaEstimateStore? {
        struct Head: Decodable { var version: Int? }
        guard let head = try? JSONDecoder().decode(Head.self, from: data),
              head.version == QuotaEstimateConstants.storeVersion else { return nil }
        return try? JSONDecoder().decode(QuotaEstimateStore.self, from: data)
    }

    public func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(self)
    }

    public static func load(data: URL) -> QuotaEstimateStore? {
        guard let bytes = try? Data(contentsOf: file(data: data)) else { return nil }
        return decode(bytes)
    }

    /// 0600, a temporary file then a rename.
    public func write(data: URL) throws {
        try QuotaEstimateFiles.writePrivate(try encoded(), to: Self.file(data: data))
    }

    // MARK: Thinning

    /// Each run of equal values keeps its first and last observation; above 256, each run keeps its last; still
    /// above, 256 runs evenly spaced, the first and last included.
    static func thin(_ observations: [QuotaObservation]) -> [QuotaObservation] {
        var runs: [[QuotaObservation]] = []
        for o in observations.stableSorted(by: { ($0.t, $0.tLo) < ($1.t, $1.tLo) }) {
            if let last = runs.last?.last, last.p == o.p {
                runs[runs.count - 1].append(o)
            } else {
                runs.append([o])
            }
        }
        let kept = runs.flatMap { $0.count > 1 ? [$0[0], $0[$0.count - 1]] : $0 }
        let limit = QuotaEstimateConstants.observationsPerInstance
        if kept.count <= limit { return kept }
        let lasts = runs.map { $0[$0.count - 1] }
        if lasts.count <= limit { return lasts }
        return (0..<limit).map { i in
            let position = (Double(i) * Double(lasts.count - 1) / Double(limit - 1)).rounded(.toNearestOrEven)
            return lasts[Int(position)]
        }
    }
}
