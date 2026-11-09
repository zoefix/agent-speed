import Foundation

/// The automatic download of every character (CHARACTERS 2, *Automatic download*): which packs a run fetches and in
/// what order.
public enum CharacterAutoDownload {
    /// Settings key of the packs the user set aside (removed, or cancelled while downloading).
    public static let setAsideKey = "charactersSetAside"

    /// The catalog's packs that are neither installed nor set aside nor `skipped`: first those of `vendors` (the
    /// vendors the orb shows live, fastest first), then the rest in catalog order.
    public static func queue(_ catalog: CharacterCatalog, installed: Set<String>, setAside: Set<String>,
                             skipped: Set<String> = [], vendors: [String]) -> [String] {
        let wanted = catalog.characters.filter {
            !installed.contains($0.id) && !setAside.contains($0.id) && !skipped.contains($0.id)
        }
        var out: [String] = []
        for vendor in vendors {
            for entry in wanted where entry.vendors.contains(vendor) && !out.contains(entry.id) { out.append(entry.id) }
        }
        for entry in wanted where !out.contains(entry.id) { out.append(entry.id) }
        return out
    }

    /// Vendors by live output, fastest first (the fastest model's vendor leads on a tie).
    public static func vendors(_ reading: VendorReading) -> [String] {
        reading.rates.filter { $0.value > 0 }
            .sorted { a, b in
                if a.value != b.value { return a.value > b.value }
                if a.key == reading.fastestVendor { return true }
                if b.key == reading.fastestVendor { return false }
                return a.key < b.key
            }
            .map(\.key)
    }
}
