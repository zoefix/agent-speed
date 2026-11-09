import Foundation

/// Which character a `character` orb shows (CHARACTERS 7). Locked to an installed pack it shows that one; on Auto it
/// follows the fastest live model vendor that has an installed pack, holding a change for 3 s so two close vendors
/// never flicker, and keeps the last one when no live vendor has a pack.
public struct CharacterSelection: Equatable, Sendable {
    public static let hold: TimeInterval = 3

    /// An installed pack and the vendors it stands for, in catalog order.
    public struct Pack: Equatable, Hashable, Sendable {
        public var id: String
        public var vendors: [String]

        public init(id: String, vendors: [String]) {
            self.id = id
            self.vendors = vendors
        }
    }

    public private(set) var shown: String?
    private var candidate: String?
    private var candidateSince: TimeInterval = 0

    public init(shown: String? = nil) {
        self.shown = shown
    }

    /// The character to show now. `vendorRates` are the summed live output rates per vendor.
    public mutating func update(locked: String?, vendorRates: [String: Double], installed: [Pack],
                                at now: TimeInterval) -> String? {
        guard !installed.isEmpty else {
            shown = nil
            candidate = nil
            return nil
        }
        let ids = Set(installed.map(\.id))
        if let locked, ids.contains(locked) {
            shown = locked
            candidate = nil
            return shown
        }
        if let current = shown, !ids.contains(current) { shown = nil }
        let wanted = Self.fastest(vendorRates: vendorRates, installed: installed)
        guard let wanted else {
            candidate = nil
            if shown == nil { shown = installed[0].id }
            return shown
        }
        guard let current = shown else {
            shown = wanted
            candidate = nil
            return shown
        }
        if wanted == current {
            candidate = nil
        } else if candidate != wanted {
            candidate = wanted
            candidateSince = now
        } else if now - candidateSince >= Self.hold {
            shown = wanted
            candidate = nil
        }
        return shown
    }

    /// The pack of the fastest vendor that has one; ties go to the vendor name in order, so the choice is stable.
    public static func fastest(vendorRates: [String: Double], installed: [Pack]) -> String? {
        let live = vendorRates.filter { $0.value > 0 && $0.value.isFinite }
            .sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }
        for (vendor, _) in live {
            if let pack = installed.first(where: { $0.vendors.contains(vendor) }) { return pack.id }
        }
        return nil
    }
}
