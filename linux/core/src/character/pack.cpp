#include "agentspeed/character/pack.hpp"

#include <fstream>
#include <sstream>

#include "agentspeed/character/error.hpp"
#include "agentspeed/character/json_read.hpp"

namespace agentspeed::character {

namespace fs = std::filesystem;
using Kind = CharacterError::Kind;

namespace {

std::optional<std::string> readFile(const fs::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return std::nullopt;
    std::ostringstream s;
    s << in.rdbuf();
    if (in.bad()) return std::nullopt;
    return s.str();
}

std::map<std::string, std::string> stringMap(const json::Value& o, std::string_view key) {
    auto v = o[key];
    if (!v.isObject()) jr::fail(key);
    std::map<std::string, std::string> out;
    for (const auto& [k, e] : v.members()) {
        auto s = e.string();
        if (!s) jr::fail(key);
        out[k] = *s;
    }
    return out;
}

std::vector<int> versionParts(const std::string& s) {
    std::vector<int> out;
    std::size_t start = 0;
    while (start <= s.size()) {
        const auto dot = s.find('.', start);
        const auto part = s.substr(start, dot == std::string::npos ? std::string::npos : dot - start);
        int value = 0;
        bool ok = !part.empty();
        for (char c : part) {
            if (c < '0' || c > '9' || value > 100000000) {
                ok = false;
                break;
            }
            value = value * 10 + (c - '0');
        }
        out.push_back(ok ? value : 0);
        if (dot == std::string::npos) break;
        start = dot + 1;
    }
    return out;
}

}  // namespace

PackManifest PackManifest::decode(std::string_view text) {
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) throw CharacterError(Kind::Unreadable, "manifest.json");
    const auto c = doc.root();
    PackManifest m;
    m.format = jr::needString(c, "format");
    m.version = jr::needInteger(c, "version");
    m.id = jr::needString(c, "id");
    m.packVersion = jr::needInteger(c, "packVersion");
    m.name = stringMap(c, "name");
    auto vendors = jr::strings(c["vendors"]);
    if (!vendors) jr::fail("vendors");
    m.vendors = *vendors;
    m.accent = jr::needString(c, "accent");
    if (auto a = c["artist"]; a.isObject()) {
        if (auto n = a["name"].string()) m.artist = Artist{*n, a["url"].string()};
    }
    m.license = c["license"].string();
    m.minApp = c["minApp"].string();
    m.poses = stringMap(c, "poses");
    m.preview = jr::needString(c, "preview");
    if (auto motion = c["motion"]; motion.isObject()) m.motion = MotionProfile::decode(motion);
    return m;
}

bool PackManifest::playable(const std::string& appVersion) const {
    if (!minApp) return true;
    const auto a = versionParts(appVersion), b = versionParts(*minApp);
    for (std::size_t i = 0; i < std::max(a.size(), b.size()); ++i) {
        const int x = i < a.size() ? a[i] : 0, y = i < b.size() ? b[i] : 0;
        if (x != y) return x > y;
    }
    return true;
}

bool Pack::isSafeID(const std::string& id) {
    if (id.empty() || id.size() > 64) return false;
    for (unsigned char ch : id) {
        if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')) return false;
    }
    return true;
}

fs::path Pack::resolve(const std::string& relative, const fs::path& root) {
    bool ok = !relative.empty() && relative.front() != '/' && relative.find('\\') == std::string::npos &&
              relative.find('\0') == std::string::npos;
    if (ok) {
        std::size_t start = 0;
        while (true) {
            const auto slash = relative.find('/', start);
            const auto part = relative.substr(start, slash == std::string::npos ? std::string::npos : slash - start);
            if (part.empty() || part == "." || part == "..") {
                ok = false;
                break;
            }
            if (slash == std::string::npos) break;
            start = slash + 1;
        }
    }
    if (!ok) throw CharacterError(Kind::UnsafePath, relative);
    std::error_code ec;
    const fs::path base = fs::weakly_canonical(root, ec);
    if (ec) throw CharacterError(Kind::UnsafePath, relative);
    const fs::path url = fs::weakly_canonical(base / relative, ec);
    if (ec) throw CharacterError(Kind::UnsafePath, relative);
    std::string prefix = base.string();
    if (prefix.empty() || prefix.back() != '/') prefix += '/';
    if (url.string().rfind(prefix, 0) != 0) throw CharacterError(Kind::UnsafePath, relative);
    return url;
}

std::shared_ptr<const Pack> Pack::load(const fs::path& rootIn) {
    std::error_code ec;
    const fs::path root = fs::weakly_canonical(rootIn, ec);
    if (ec) throw CharacterError(Kind::Unreadable, "manifest.json");
    auto pack = std::make_shared<Pack>();
    pack->root = root;
    {
        auto text = readFile(root / "manifest.json");
        if (!text) throw CharacterError(Kind::Unreadable, "manifest.json");
        try {
            pack->manifest = PackManifest::decode(*text);
        } catch (const CharacterError&) {
            throw CharacterError(Kind::Unreadable, "manifest.json");
        }
    }
    const auto& m = pack->manifest;
    if (m.format != PackManifest::kFormat || m.version != PackManifest::kVersion) {
        throw CharacterError(Kind::Unsupported, m.format + " " + std::to_string(m.version));
    }
    if (!isSafeID(m.id)) throw CharacterError(Kind::BadID, m.id);
    (void)resolve(m.preview, root);
    for (Band band : kBands) {
        auto folder = m.poses.find(name(band));
        if (folder == m.poses.end()) continue;
        const fs::path dir = resolve(folder->second, root);
        const fs::path rigPath = resolve("rig.json", dir);
        auto text = readFile(rigPath);
        if (!text) throw CharacterError(Kind::Unreadable, folder->second + "/rig.json");
        RigDocument doc;
        try {
            doc = RigDocument::decode(*text);
        } catch (const CharacterError&) {
            throw CharacterError(Kind::Unreadable, folder->second + "/rig.json");
        }
        Rig rig(doc);
        const fs::path atlas = resolve(rig.atlasFile, dir);
        pack->poses.push_back(Pose{band, std::move(rig), atlas});
    }
    if (pack->poses.empty() || pack->poses.front().band != Band::Idle) throw CharacterError(Kind::MissingIdlePose, "idle");
    return pack;
}

}  // namespace agentspeed::character
