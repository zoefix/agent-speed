import AgentSpeedOrbModel
import AppKit

/// What the placement pass needs from one orb window.
@MainActor
protocol OrbNameplateHost: AnyObject {
    var id: String { get }
    /// The disc at rest (its target center and size).
    var disc: OrbDisc { get }
    var screen: NSScreen? { get }
    var isShown: Bool { get }
    /// Shown, or hidden while idle: a hidden orb shows no plate but still keeps the others away from its place.
    var holdsPlace: Bool { get }
    /// True while dragged or springing to a new position: placement waits until it rests.
    var isMovingForNameplate: Bool { get }
    var nameplate: OrbNameplateController { get }
}

/// Resolves every plate's placement across the orb windows (DESIGN 2b, Name plate, Placement): per display, groups
/// of discs within 16 pt share a side when they can. Runs once per burst of changes and only while every orb rests —
/// after a drag's snap spring settles, on display changes, and when a name, size, visibility or plate setting changes.
/// During a drag each plate keeps its slot and travels with its orb.
@MainActor
final class OrbNameplateCoordinator {
    private let hosts: () -> [OrbNameplateHost]
    private var scheduled = false
    /// Called after a pass changed any slot (the undo chip stacks beyond a plate).
    var onResolved: (() -> Void)?

    init(hosts: @escaping () -> [OrbNameplateHost]) {
        self.hosts = hosts
    }

    func setNeedsLayout() {
        guard !scheduled else { return }
        scheduled = true
        DispatchQueue.main.async { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.scheduled = false
                self.resolve()
            }
        }
    }

    private func resolve() {
        let all = hosts()
        // A moving orb calls back when it settles.
        guard !all.contains(where: \.isMovingForNameplate) else { return }
        var changed = false
        var byScreen: [(screen: NSScreen, hosts: [OrbNameplateHost])] = []
        for host in all {
            guard host.holdsPlace, let screen = host.screen else {
                changed = apply(nil, to: host) || changed
                continue
            }
            if let index = byScreen.firstIndex(where: { $0.screen == screen }) {
                byScreen[index].hosts.append(host)
            } else {
                byScreen.append((screen, [host]))
            }
        }
        for (screen, hosts) in byScreen {
            let requests = hosts.map { host in
                let plate = host.nameplate
                return NameplateRequest(id: host.id, disc: host.disc, wantsPlate: host.isShown && plate.wantsPlate) { budget in
                    plate.content?.restWidth(budget: budget)
                }
            }
            let slots = NameplateLayout.solve(requests, visible: screen.visibleFrame)
            for host in hosts { changed = apply(slots[host.id], to: host) || changed }
        }
        if changed { onResolved?() }
    }

    private func apply(_ slot: NameplateSlot?, to host: OrbNameplateHost) -> Bool {
        guard host.nameplate.slot != slot else { return false }
        host.nameplate.setSlot(slot)
        return true
    }
}
