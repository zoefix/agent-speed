#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/character/rhythm.hpp"

// The pack manifest as the library validates it, the catalog of the `characters` release and what a pack archive may
// contain (CHARACTERS 1, 2).
namespace agentspeed::character {

enum class PackError { Format, InvalidID, MissingIdle, UnsafePath, SymbolicLink, MissingFile, TooLarge, NewerApp, Mismatch };

// Thrown by the catalog, store and installer (macOS CharacterPackError).
class PackFailure : public std::exception {
public:
    PackFailure(PackError kind, std::string detail = {}) : kind_(kind), detail_(std::move(detail)) {}
    PackError kind() const { return kind_; }
    const std::string& detail() const { return detail_; }
    const char* what() const noexcept override { return detail_.c_str(); }

private:
    PackError kind_;
    std::string detail_;
};

// Display names of a pack in the four app languages.
struct CharacterNames {
    std::string zhHant, zhHans, en, ja;
    bool operator==(const CharacterNames&) const = default;
    bool isComplete() const;
    // The name for an app language (`zh-Hant`, `zh-Hans`, `en`, `ja`); English otherwise.
    const std::string& localized(std::string_view language) const;
};

struct CharacterArtist {
    std::string name;
    std::optional<std::string> url;
    bool operator==(const CharacterArtist&) const = default;
    // The credit link, only when it is an https URL.
    std::optional<std::string> link() const;
};

// A three-part version (`x.y.z`, optional `v`, pre-release and build), compared as SemVer.
struct SemanticVersion {
    int major = 0, minor = 0, patch = 0;
    std::vector<std::string> prerelease;
    static std::optional<SemanticVersion> parse(std::string_view text);
    bool operator==(const SemanticVersion&) const = default;
    bool operator<(const SemanticVersion& o) const;
};

// `manifest.json` of a pack (CHARACTERS 1).
struct CharacterManifest {
    static constexpr std::string_view kFormat = "agent-speed-character-pack";
    static constexpr int kVersion = 1;

    std::string format;
    std::int64_t version = 0;
    std::string id;
    std::int64_t packVersion = 0;
    CharacterNames name;
    std::vector<std::string> vendors;
    std::string accent;
    std::optional<CharacterArtist> artist;
    std::optional<std::string> license;
    std::optional<std::string> minApp;
    std::map<std::string, std::string> poses;
    std::string preview = "preview.png";

    bool operator==(const CharacterManifest&) const = default;

    // Throws PackFailure(Format) for anything that is not a manifest.
    static CharacterManifest decode(std::string_view json);
    // Checks everything a player relies on; `appVersion` (`x.y.z`) rejects packs that need a newer app.
    void validate(const std::optional<std::string>& appVersion) const;
    // The pose folder of a band: its own, else the nearest lower band's.
    std::string pose(Band band) const;

    struct Rgb {
        double r, g, b;
    };
    // `#RRGGBB` as 0...1 components; nullopt for anything else.
    static std::optional<Rgb> rgb(std::string_view text);
};

// `catalog.json` of the `characters` release (CHARACTERS 2).
struct CharacterCatalog {
    static constexpr std::string_view kFormat = "agent-speed-character-catalog";
    static constexpr int kVersion = 1;
    static constexpr std::string_view kUrl = "https://github.com/zoefix/agent-speed/releases/download/characters/catalog.json";
    static constexpr std::size_t kMaxCatalogBytes = 1 << 20;
    static constexpr std::int64_t kMaxPackBytes = std::int64_t{200} << 20;

    struct Entry {
        std::string id;
        std::int64_t packVersion = 0;
        CharacterNames name;
        std::vector<std::string> vendors;
        std::string accent;
        std::optional<CharacterArtist> artist;
        std::optional<std::string> minApp;
        std::int64_t size = 0;
        std::string sha256;
        std::string url;
        std::optional<std::string> preview;
        bool operator==(const Entry&) const = default;
        bool isValid() const;
    };

    std::vector<Entry> characters;
    bool operator==(const CharacterCatalog&) const = default;

    const Entry* find(const std::string& id) const;
    // Decodes and keeps only valid entries (unique ids, first wins); throws PackFailure(Format / TooLarge).
    static CharacterCatalog load(std::string_view json);
    // Hosts a catalog or pack download may touch, redirects included.
    static const std::vector<std::string>& allowedHosts();
    // HTTPS on the release hosts, default port, no user information.
    static bool permits(std::string_view url);
};

// One entry of a zip's central directory.
struct ZipEntry {
    std::string path;
    std::uint64_t uncompressedSize = 0;
    bool isSymbolicLink = false;
    bool isDirectory() const { return !path.empty() && path.back() == '/'; }
};

// What a pack archive may contain (CHARACTERS 1): safe relative paths, no links, a bounded size, a manifest at the top.
namespace pack_layout {
constexpr std::size_t kMaxEntries = 4096;
constexpr std::uint64_t kMaxExpandedSize = std::uint64_t{400} << 20;
// A relative path of plain components: no `..`, `.`, empty parts, absolute or home paths, control characters or `\`.
bool isSafe(std::string_view path);
// Throws PackFailure.
void validate(const std::vector<ZipEntry>& entries);
}  // namespace pack_layout

}  // namespace agentspeed::character
