import Foundation

/// The two values agents and responses show: reasoning effort and the derived speed, each nil when unknown.
public struct EffortAndSpeed: Equatable, Hashable, Sendable {
    public var effort: ExecutionModeAttribution?
    public var speed: ExecutionModeAttribution?

    public static let unknown = EffortAndSpeed(effort: nil, speed: nil)

    public init(effort: ExecutionModeAttribution?, speed: ExecutionModeAttribution?) {
        self.effort = effort
        self.speed = speed
    }

    public init(_ modes: ExecutionModes) {
        let effort = modes.attribution(for: .effort), speed = modes.attribution(for: .speed)
        self.init(effort: effort.isUnknown ? nil : effort, speed: speed.isUnknown ? nil : speed)
    }

    /// An agent's current values (EXECUTION-MODES, Current values): each the first known one of its own facts, a
    /// session-current fact and a configured value.
    public init(facts: ExecutionModes, session: ExecutionModes = .empty, configured: ExecutionModes = .empty) {
        func current(_ kind: ExecutionModeKind) -> ExecutionModeAttribution? {
            let tiers: [(ExecutionModes, Set<ExecutionModeEvidence>)] = [
                (facts, ExecutionModeEvidence.attributable), (session, [.selected]), (configured, [.configured]),
            ]
            for (modes, evidence) in tiers {
                let result = modes.attribution(for: kind, among: evidence)
                if !result.isUnknown { return result }
            }
            return nil
        }
        self.init(effort: current(.effort), speed: current(.speed))
    }

    /// Bolts of the speed; 0 when unknown or without bolts.
    public var bolts: Int { speed.map { ModeDisplay.bolts($0.mode) } ?? 0 }

    /// Whether there is anything to draw: a known effort or a speed with bolts.
    public var isVisible: Bool { effort != nil || bolts > 0 }
}

extension ExecutionModeAttribution {
    /// A value only a config file holds, which the agent may not be using.
    public var isConfigured: Bool { evidence == ExecutionModeEvidence.configured.rawValue }
}

extension ExecutionModes {
    public var effortAndSpeed: EffortAndSpeed { EffortAndSpeed(self) }
}
