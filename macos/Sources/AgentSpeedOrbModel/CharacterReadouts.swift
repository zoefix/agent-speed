import Foundation

/// The cards a `character` orb floats beside her (DESIGN 2a.6 *Readouts*, 2b `characterReadouts`): the speed always
/// shows; each card has its own switch. Every card is on for a new orb except the fastest model's name. Decoding is
/// tolerant: a missing key, or a value that is not a boolean, takes its default.
public struct CharacterReadouts: Equatable, Hashable, Sendable {
    /// The fastest model's name above her head.
    public var model: Bool
    /// 性能 and 思考 of the orb's agents.
    public var intelligence: Bool
    public var thinking: Bool
    /// 首字, the orb's first-content latency, as the 性能 card's third row.
    public var latency: Bool
    /// Download and upload speed; its scope follows `OrbConfig.traffic` (`machine`, else the orb's agents).
    public var network: Bool
    /// The live cost rate.
    public var cost: Bool
    /// Today's tokens and the agent count.
    public var agents: Bool
    /// The running models under the speed, each with its fastest agent's speed.
    public var models: Bool
    /// 額度: the subscription quotas of the orb's running, signed-in agents, the column's last card.
    public var quota: Bool

    public init(model: Bool = false, intelligence: Bool = true, thinking: Bool = true, latency: Bool = true,
                network: Bool = true, cost: Bool = true, agents: Bool = true, models: Bool = true, quota: Bool = true) {
        self.model = model
        self.intelligence = intelligence
        self.thinking = thinking
        self.latency = latency
        self.network = network
        self.cost = cost
        self.agents = agents
        self.models = models
        self.quota = quota
    }

    public static let `default` = CharacterReadouts()

    public subscript(_ readout: CharacterReadout) -> Bool {
        get {
            switch readout {
            case .model: model
            case .intelligence: intelligence
            case .thinking: thinking
            case .latency: latency
            case .network: network
            case .cost: cost
            case .agents: agents
            case .models: models
            case .quota: quota
            }
        }
        set {
            switch readout {
            case .model: model = newValue
            case .intelligence: intelligence = newValue
            case .thinking: thinking = newValue
            case .latency: latency = newValue
            case .network: network = newValue
            case .cost: cost = newValue
            case .agents: agents = newValue
            case .models: models = newValue
            case .quota: quota = newValue
            }
        }
    }
}

/// One switch of `CharacterReadouts`, in the menu's and editor's order.
public enum CharacterReadout: String, CaseIterable, Sendable, Identifiable {
    case intelligence, thinking, latency, network, cost, agents, models, quota, model

    public var id: String { rawValue }

    /// `character.readouts.intel`, `.thinking`, `.latency`, `.network`, `.cost`, `.agents`, `.models`, `.quota`, `.model`.
    public var titleKey: String {
        switch self {
        case .intelligence: "character.readouts.intel"
        case .thinking: "character.readouts.thinking"
        case .latency: "character.readouts.latency"
        case .network: "character.readouts.network"
        case .cost: "character.readouts.cost"
        case .agents: "character.readouts.agents"
        case .models: "character.readouts.models"
        case .quota: "character.readouts.quota"
        case .model: "character.readouts.model"
        }
    }
}

extension CharacterReadouts: Codable {
    enum CodingKeys: String, CodingKey {
        case model, intelligence, thinking, latency, network, cost, agents, models, quota
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        func flag(_ key: CodingKeys, _ fallback: Bool) -> Bool {
            ((try? container.decodeIfPresent(Bool.self, forKey: key)) ?? nil) ?? fallback
        }
        let d = CharacterReadouts.default
        self.init(model: flag(.model, d.model), intelligence: flag(.intelligence, d.intelligence),
                  thinking: flag(.thinking, d.thinking), latency: flag(.latency, d.latency), network: flag(.network, d.network),
                  cost: flag(.cost, d.cost), agents: flag(.agents, d.agents), models: flag(.models, d.models),
                  quota: flag(.quota, d.quota))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(model, forKey: .model)
        try container.encode(intelligence, forKey: .intelligence)
        try container.encode(thinking, forKey: .thinking)
        try container.encode(latency, forKey: .latency)
        try container.encode(network, forKey: .network)
        try container.encode(cost, forKey: .cost)
        try container.encode(agents, forKey: .agents)
        try container.encode(models, forKey: .models)
        try container.encode(quota, forKey: .quota)
    }
}
