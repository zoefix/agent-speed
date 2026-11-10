#pragma once

#include <filesystem>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/character/catalog.hpp"
#include "agentspeed/character/selection.hpp"
#include "agentspeed/snapshot.hpp"

namespace agentspeed::pricing {
class PriceTable;
}

namespace agentspeed::character {

// An installed character pack: its manifest and the folder that holds it (CHARACTERS 2).
struct InstalledCharacter {
    CharacterManifest manifest;
    std::filesystem::path folder;
    // Listed from the `AGENT_SPEED_CHARACTERS` development folder; it cannot be removed or updated from the app.
    bool isDevelopment = false;

    const std::string& id() const { return manifest.id; }
    std::filesystem::path preview() const { return folder / manifest.preview; }
    bool operator==(const InstalledCharacter&) const = default;
};

// Installed packs on disk: `<root>/<id>/` (CHARACTERS 2). Installing and removing replace whole folders, so a player
// never sees a half-written pack.
class CharacterStore {
public:
    static constexpr const char* kDevelopmentVariable = "AGENT_SPEED_CHARACTERS";
    static constexpr std::uintmax_t kManifestLimit = 64 << 10;

    CharacterStore(std::filesystem::path root, std::optional<std::filesystem::path> development = std::nullopt,
                   std::optional<std::string> appVersion = std::nullopt)
        : root_(std::move(root)), development_(std::move(development)), appVersion_(std::move(appVersion)) {}

    const std::filesystem::path& root() const { return root_; }
    const std::optional<std::filesystem::path>& development() const { return development_; }

    // Every valid pack, development ones first, then installed ones by id; a development pack hides an installed one
    // with the same id.
    std::vector<InstalledCharacter> installed() const;
    std::optional<InstalledCharacter> installed(const std::string& id) const;

    // The validated manifest of an unpacked pack whose every pose holds `rig.json` and whose preview exists. Throws
    // PackFailure.
    static CharacterManifest manifest(const std::filesystem::path& folder, const std::optional<std::string>& appVersion);
    // Only regular files and folders, no links, within the size limit. Throws PackFailure.
    static void verifyTree(const std::filesystem::path& folder);

    // Moves a validated, unpacked pack into place, replacing an older copy atomically. `expectedID` must match.
    InstalledCharacter install(const std::filesystem::path& unpacked, const std::string& expectedID) const;
    // Deletes an installed (non-development) pack.
    void remove(const std::string& id) const;

private:
    std::vector<InstalledCharacter> scan(const std::filesystem::path& folder, bool development) const;

    std::filesystem::path root_;
    std::optional<std::filesystem::path> development_;
    std::optional<std::string> appVersion_;
};

// Why installing a downloaded archive failed (macOS CharacterInstallError), with the Settings error key.
enum class InstallError { DisallowedURL, Checksum, InvalidPack, NewerApp, Cancelled, Network };
const char* errorKey(InstallError e);

// Verifies a downloaded archive against its catalog entry (exact size and SHA-256), checks its entries, unpacks it
// into a private folder next to the store and installs it. Returns the error, nullopt on success; nothing is left
// behind on failure. Never executes anything from the pack.
std::optional<InstallError> installArchive(const CharacterStore& store, const CharacterCatalog::Entry& entry,
                                           const std::filesystem::path& archive);

// The automatic download of every character (CHARACTERS 2, *Automatic download*).
namespace auto_download {
// Settings key of the packs the user set aside (removed, or cancelled while downloading).
constexpr const char* kSetAsideKey = "charactersSetAside";
// The catalog's packs that are neither installed nor set aside nor `skipped`: first those of `vendors` (the vendors
// the orb shows live, fastest first), then the rest in catalog order.
std::vector<std::string> queue(const CharacterCatalog& catalog, const std::set<std::string>& installed,
                               const std::set<std::string>& setAside, const std::set<std::string>& skipped,
                               const std::vector<std::string>& vendors);
}  // namespace auto_download

// Live output per model vendor and the fastest model of a set of agents (CHARACTERS 7).
struct VendorReading {
    std::map<std::string, double> rates;
    std::optional<std::string> fastestModel;
    std::optional<std::string> fastestVendor;
    bool operator==(const VendorReading&) const = default;

    static VendorReading of(const std::vector<AgentRow>& agents, const pricing::PriceTable* prices);
    // Vendors by live output, fastest first (the fastest model's vendor leads on a tie).
    std::vector<std::string> vendorsByRate() const;
};

// The model vendor a source stands for when its model id does not resolve to a priced vendor (CHARACTERS 7).
namespace vendors {
struct Resolved {
    std::optional<std::string> vendor, name;
};
Resolved resolve(const std::optional<std::string>& model, SourceKind source, const pricing::PriceTable* prices);
// `meta-llama/Llama-3.3-70B` → `meta`, `doubao-seed-1.6` → `bytedance`; nullopt for any other id.
std::optional<std::string> family(const std::optional<std::string>& model);
std::optional<std::string> fallback(SourceKind source);
// The vendor's accent (the models card's dot, DESIGN 2a.6): the theme color of an installed character that stands for
// the vendor, else of the catalog's, as `#RRGGBB`; nullopt when no character stands for it.
std::optional<std::string> accent(const std::optional<std::string>& vendor, const std::vector<InstalledCharacter>& installed,
                                  const CharacterCatalog* catalog);
}  // namespace vendors

}  // namespace agentspeed::character
