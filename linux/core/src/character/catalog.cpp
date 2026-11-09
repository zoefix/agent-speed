#include "agentspeed/character/catalog.hpp"

#include <algorithm>
#include <set>

#include "agentspeed/json.hpp"

namespace agentspeed::character {

namespace {

bool blank(const std::string& s) {
    return std::all_of(s.begin(), s.end(), [](char c) { return c == ' ' || c == '\t'; });
}

std::optional<CharacterNames> namesOf(const json::Value& v) {
    if (!v.isObject()) return std::nullopt;
    auto a = v["zh-Hant"].string(), b = v["zh-Hans"].string(), c = v["en"].string(), d = v["ja"].string();
    if (!a || !b || !c || !d) return std::nullopt;
    return CharacterNames{*a, *b, *c, *d};
}

std::optional<CharacterArtist> artistOf(const json::Value& v) {
    if (!v.isObject()) return std::nullopt;
    auto n = v["name"].string();
    if (!n) return std::nullopt;
    return CharacterArtist{*n, v["url"].string()};
}

std::optional<std::vector<std::string>> stringList(const json::Value& v) {
    if (!v.isArray()) return std::nullopt;
    std::vector<std::string> out;
    for (const auto& e : v.elements()) {
        auto s = e.string();
        if (!s) return std::nullopt;
        out.push_back(*s);
    }
    return out;
}

std::optional<std::int64_t> integer(const json::Value& v) {
    if (!v.isNumber()) return std::nullopt;
    auto d = v.finiteNumber();
    if (!d || *d != static_cast<double>(static_cast<std::int64_t>(*d))) return std::nullopt;
    return static_cast<std::int64_t>(*d);
}

std::string lowerAscii(std::string_view s) {
    std::string out(s);
    for (auto& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

bool isHex(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }

bool validID(std::string_view id) {
    if (id.empty() || id.size() > 64 || id.front() == '-' || id.back() == '-') return false;
    return std::all_of(id.begin(), id.end(), [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'; });
}

}  // namespace

bool CharacterNames::isComplete() const { return !blank(zhHant) && !blank(zhHans) && !blank(en) && !blank(ja); }

const std::string& CharacterNames::localized(std::string_view language) const {
    if (language == "zh-Hant") return zhHant;
    if (language == "zh-Hans") return zhHans;
    if (language == "ja") return ja;
    return en;
}

std::optional<std::string> CharacterArtist::link() const {
    if (!url) return std::nullopt;
    const auto lower = lowerAscii(*url);
    if (lower.rfind("https://", 0) != 0) return std::nullopt;
    if (url->find_first_of(" \t\r\n\"<>\\") != std::string::npos) return std::nullopt;
    const auto authority = lower.substr(8, lower.find_first_of("/?#", 8) - 8);
    const auto host = authority.substr(authority.find('@') == std::string::npos ? 0 : authority.find('@') + 1);
    if (host.empty() || host.front() == ':') return std::nullopt;
    return url;
}

std::optional<SemanticVersion> SemanticVersion::parse(std::string_view text) {
    if (!text.empty() && text.front() == 'v') text.remove_prefix(1);
    if (auto plus = text.find('+'); plus != std::string_view::npos) text = text.substr(0, plus);
    SemanticVersion v;
    if (auto dash = text.find('-'); dash != std::string_view::npos) {
        auto rest = text.substr(dash + 1);
        text = text.substr(0, dash);
        std::size_t start = 0;
        while (true) {
            auto dot = rest.find('.', start);
            auto id = rest.substr(start, dot == std::string_view::npos ? std::string_view::npos : dot - start);
            if (id.empty()) return std::nullopt;
            v.prerelease.emplace_back(id);
            if (dot == std::string_view::npos) break;
            start = dot + 1;
        }
    }
    int parts[3];
    std::size_t start = 0;
    for (int i = 0; i < 3; ++i) {
        auto dot = text.find('.', start);
        if ((i < 2) != (dot != std::string_view::npos)) return std::nullopt;
        auto part = text.substr(start, i < 2 ? dot - start : std::string_view::npos);
        if (part.empty() || part.size() > 9) return std::nullopt;
        int n = 0;
        for (char c : part) {
            if (c < '0' || c > '9') return std::nullopt;
            n = n * 10 + (c - '0');
        }
        parts[i] = n;
        start = dot + 1;
    }
    v.major = parts[0];
    v.minor = parts[1];
    v.patch = parts[2];
    return v;
}

bool SemanticVersion::operator<(const SemanticVersion& o) const {
    if (major != o.major) return major < o.major;
    if (minor != o.minor) return minor < o.minor;
    if (patch != o.patch) return patch < o.patch;
    if (prerelease.empty() != o.prerelease.empty()) return !prerelease.empty();
    for (std::size_t i = 0; i < std::min(prerelease.size(), o.prerelease.size()); ++i) {
        const auto& a = prerelease[i];
        const auto& b = o.prerelease[i];
        if (a == b) continue;
        const bool an = std::all_of(a.begin(), a.end(), ::isdigit), bn = std::all_of(b.begin(), b.end(), ::isdigit);
        if (an && bn) return a.size() != b.size() ? a.size() < b.size() : a < b;
        if (an != bn) return an;
        return a < b;
    }
    return prerelease.size() < o.prerelease.size();
}

CharacterManifest CharacterManifest::decode(std::string_view text) {
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) throw PackFailure(PackError::Format, "manifest.json");
    const auto c = doc.root();
    CharacterManifest m;
    auto format = c["format"].string();
    auto version = integer(c["version"]);
    auto id = c["id"].string();
    auto packVersion = integer(c["packVersion"]);
    auto names = namesOf(c["name"]);
    auto vendors = stringList(c["vendors"]);
    auto accent = c["accent"].string();
    if (!format || !version || !id || !packVersion || !names || !vendors || !accent || !c["poses"].isObject()) {
        throw PackFailure(PackError::Format, "manifest.json");
    }
    m.format = *format;
    m.version = *version;
    m.id = *id;
    m.packVersion = *packVersion;
    m.name = *names;
    m.vendors = *vendors;
    m.accent = *accent;
    m.artist = artistOf(c["artist"]);
    m.license = c["license"].string();
    m.minApp = c["minApp"].string();
    for (const auto& [band, path] : c["poses"].members()) {
        auto p = path.string();
        if (!p) throw PackFailure(PackError::Format, "poses");
        m.poses[band] = *p;
    }
    auto preview = c["preview"].string();
    if (!preview) throw PackFailure(PackError::Format, "preview");
    m.preview = *preview;
    return m;
}

void CharacterManifest::validate(const std::optional<std::string>& appVersion) const {
    if (format != kFormat || version != kVersion) throw PackFailure(PackError::Format, format);
    if (!validID(id) || packVersion < 1 || !name.isComplete() || !rgb(accent)) throw PackFailure(PackError::InvalidID, id);
    if (!poses.count("idle")) throw PackFailure(PackError::MissingIdle);
    for (const auto& [band, path] : poses) {
        if (!bandNamed(band)) throw PackFailure(PackError::Format, band);
        if (!pack_layout::isSafe(path)) throw PackFailure(PackError::UnsafePath, path);
    }
    if (!pack_layout::isSafe(preview)) throw PackFailure(PackError::UnsafePath, preview);
    if (minApp && appVersion) {
        const auto needed = SemanticVersion::parse(*minApp);
        const auto current = SemanticVersion::parse(*appVersion);
        if (needed && current && *current < *needed && !(*current == SemanticVersion{})) {
            throw PackFailure(PackError::NewerApp, *minApp);
        }
    }
}

std::string CharacterManifest::pose(Band band) const {
    for (auto it = kBands.rbegin(); it != kBands.rend(); ++it) {
        if (!(*it <= band)) continue;
        if (auto p = poses.find(character::name(*it)); p != poses.end()) return p->second;
    }
    if (auto p = poses.find("idle"); p != poses.end()) return p->second;
    return "poses/idle";
}

std::optional<CharacterManifest::Rgb> CharacterManifest::rgb(std::string_view text) {
    if (text.size() != 7 || text[0] != '#') return std::nullopt;
    unsigned value = 0;
    for (std::size_t i = 1; i < 7; ++i) {
        const char c = text[i];
        if (!isHex(c)) return std::nullopt;
        value = value * 16 + static_cast<unsigned>(c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
    }
    return Rgb{static_cast<double>((value >> 16) & 0xFF) / 255, static_cast<double>((value >> 8) & 0xFF) / 255,
               static_cast<double>(value & 0xFF) / 255};
}

bool CharacterCatalog::Entry::isValid() const {
    return validID(id) && packVersion >= 1 && name.isComplete() && CharacterManifest::rgb(accent) && size >= 1 &&
           size <= kMaxPackBytes && sha256.size() == 64 && std::all_of(sha256.begin(), sha256.end(), isHex) &&
           permits(url) && (!preview || permits(*preview));
}

const CharacterCatalog::Entry* CharacterCatalog::find(const std::string& id) const {
    for (const auto& e : characters) {
        if (e.id == id) return &e;
    }
    return nullptr;
}

CharacterCatalog CharacterCatalog::load(std::string_view text) {
    if (text.size() > kMaxCatalogBytes) throw PackFailure(PackError::TooLarge);
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) throw PackFailure(PackError::Format, "catalog.json");
    const auto root = doc.root();
    auto format = root["format"].string();
    auto version = integer(root["version"]);
    if (!format || !version || !root["characters"].isArray()) throw PackFailure(PackError::Format, "catalog.json");
    if (*format != kFormat || *version != kVersion) throw PackFailure(PackError::Format, *format);
    CharacterCatalog catalog;
    std::set<std::string> seen;
    for (const auto& e : root["characters"].elements()) {
        if (!e.isObject()) throw PackFailure(PackError::Format, "characters");
        Entry entry;
        auto id = e["id"].string();
        auto packVersion = integer(e["packVersion"]);
        auto names = namesOf(e["name"]);
        auto vendors = stringList(e["vendors"]);
        auto accent = e["accent"].string();
        auto size = integer(e["size"]);
        auto sha = e["sha256"].string();
        auto url = e["url"].string();
        if (!id || !packVersion || !names || !vendors || !accent || !size || !sha || !url) {
            throw PackFailure(PackError::Format, "characters");
        }
        entry.id = *id;
        entry.packVersion = *packVersion;
        entry.name = *names;
        entry.vendors = *vendors;
        entry.accent = *accent;
        entry.artist = artistOf(e["artist"]);
        entry.minApp = e["minApp"].string();
        entry.size = *size;
        entry.sha256 = *sha;
        entry.url = *url;
        entry.preview = e["preview"].string();
        if (entry.isValid() && seen.insert(entry.id).second) catalog.characters.push_back(std::move(entry));
    }
    return catalog;
}

const std::vector<std::string>& CharacterCatalog::allowedHosts() {
    static const std::vector<std::string> hosts = {"github.com", "objects.githubusercontent.com",
                                                   "release-assets.githubusercontent.com"};
    return hosts;
}

bool CharacterCatalog::permits(std::string_view url) {
    const auto lower = lowerAscii(url);
    if (lower.rfind("https://", 0) != 0) return false;
    if (url.find_first_of(" \t\r\n\\") != std::string_view::npos) return false;
    std::string_view rest = std::string_view(lower).substr(8);
    const auto end = rest.find_first_of("/?#");
    std::string_view authority = rest.substr(0, end);
    if (authority.find('@') != std::string_view::npos) return false;
    std::string_view host = authority;
    if (auto colon = authority.rfind(':'); colon != std::string_view::npos) {
        host = authority.substr(0, colon);
        if (authority.substr(colon + 1) != "443") return false;
    }
    const auto& hosts = allowedHosts();
    return std::find(hosts.begin(), hosts.end(), host) != hosts.end();
}

namespace pack_layout {

bool isSafe(std::string_view path) {
    if (path.empty() || path.front() == '/' || path.front() == '~') return false;
    for (unsigned char c : path) {
        if (c < 0x20 || c == '\\' || c == 0x7F) return false;
    }
    if (path.back() == '/') path.remove_suffix(1);
    std::size_t start = 0;
    while (true) {
        const auto slash = path.find('/', start);
        const auto part = path.substr(start, slash == std::string_view::npos ? std::string_view::npos : slash - start);
        if (part.empty() || part == "." || part == "..") return false;
        if (slash == std::string_view::npos) return true;
        start = slash + 1;
    }
}

void validate(const std::vector<ZipEntry>& entries) {
    if (entries.empty() || entries.size() > kMaxEntries) throw PackFailure(PackError::TooLarge);
    std::uint64_t total = 0;
    for (const auto& e : entries) {
        if (!isSafe(e.path)) throw PackFailure(PackError::UnsafePath, e.path);
        if (e.isSymbolicLink) throw PackFailure(PackError::SymbolicLink, e.path);
        if (e.uncompressedSize > kMaxExpandedSize - total) throw PackFailure(PackError::TooLarge);
        total += e.uncompressedSize;
    }
    if (std::none_of(entries.begin(), entries.end(), [](const ZipEntry& e) { return e.path == "manifest.json"; })) {
        throw PackFailure(PackError::MissingFile, "manifest.json");
    }
}

}  // namespace pack_layout
}  // namespace agentspeed::character
