#include "agentspeed/character/store.hpp"

#include <fcntl.h>
#include <stdio.h>
#include <sys/syscall.h>
#include <unistd.h>

#include <algorithm>
#include <fstream>
#include <random>
#include <sstream>

#include "agentspeed/character/zip.hpp"
#include "agentspeed/pricing.hpp"

namespace agentspeed::character {

namespace fs = std::filesystem;

namespace {

std::string randomSuffix() {
    std::random_device rd;
    std::ostringstream s;
    s << std::hex << rd() << rd();
    return s.str();
}

// Swaps two paths atomically (Linux renameat2 RENAME_EXCHANGE); false when the kernel or file system refuses.
bool exchange(const fs::path& a, const fs::path& b) {
#ifdef SYS_renameat2
    constexpr unsigned kExchange = 2;
    return ::syscall(SYS_renameat2, AT_FDCWD, a.c_str(), AT_FDCWD, b.c_str(), kExchange) == 0;
#else
    (void)a;
    (void)b;
    return false;
#endif
}

}  // namespace

std::vector<InstalledCharacter> CharacterStore::installed() const {
    std::vector<InstalledCharacter> out;
    std::set<std::string> seen;
    const std::pair<std::optional<fs::path>, bool> folders[] = {{development_, true}, {root_, false}};
    for (const auto& [folder, dev] : folders) {
        if (!folder) continue;
        for (auto& pack : scan(*folder, dev)) {
            if (seen.insert(pack.id()).second) out.push_back(std::move(pack));
        }
    }
    return out;
}

std::optional<InstalledCharacter> CharacterStore::installed(const std::string& id) const {
    for (auto& p : installed()) {
        if (p.id() == id) return p;
    }
    return std::nullopt;
}

CharacterManifest CharacterStore::manifest(const fs::path& folder, const std::optional<std::string>& appVersion) {
    const auto file = folder / "manifest.json";
    std::error_code ec;
    const auto status = fs::symlink_status(file, ec);
    if (ec || !fs::is_regular_file(status) || fs::file_size(file, ec) > kManifestLimit || ec) {
        throw PackFailure(PackError::MissingFile, "manifest.json");
    }
    std::ifstream in(file, std::ios::binary);
    std::ostringstream text;
    text << in.rdbuf();
    const auto m = CharacterManifest::decode(text.str());
    m.validate(appVersion);
    std::set<std::string> poses;
    for (const auto& [band, path] : m.poses) poses.insert(path);
    for (const auto& path : poses) {
        if (!fs::exists(folder / path / "rig.json", ec)) throw PackFailure(PackError::MissingFile, path + "/rig.json");
    }
    if (!fs::exists(folder / m.preview, ec)) throw PackFailure(PackError::MissingFile, m.preview);
    return m;
}

void CharacterStore::verifyTree(const fs::path& folder) {
    std::error_code ec;
    std::uintmax_t total = 0;
    fs::recursive_directory_iterator it(folder, fs::directory_options::none, ec), end;
    if (ec) throw PackFailure(PackError::MissingFile, folder.filename().string());
    for (; it != end; it.increment(ec)) {
        if (ec) throw PackFailure(PackError::MissingFile, folder.filename().string());
        const auto status = it->symlink_status(ec);
        if (fs::is_symlink(status)) throw PackFailure(PackError::SymbolicLink, it->path().filename().string());
        if (fs::is_regular_file(status)) {
            total += it->file_size(ec);
            if (total > pack_layout::kMaxExpandedSize) throw PackFailure(PackError::TooLarge);
        } else if (!fs::is_directory(status)) {
            throw PackFailure(PackError::UnsafePath, it->path().filename().string());
        }
    }
}

InstalledCharacter CharacterStore::install(const fs::path& unpacked, const std::string& expectedID) const {
    const auto m = manifest(unpacked, appVersion_);
    if (m.id != expectedID) throw PackFailure(PackError::Mismatch, m.id);
    verifyTree(unpacked);
    std::error_code ec;
    fs::create_directories(root_, ec);
    fs::permissions(root_, fs::perms::owner_all, fs::perm_options::replace, ec);
    const auto target = root_ / m.id;
    const auto staged = root_ / (".incoming-" + randomSuffix());
    fs::rename(unpacked, staged, ec);
    if (ec) {
        // Another file system: copy into the store first, then move within it.
        fs::copy(unpacked, staged, fs::copy_options::recursive, ec);
        if (ec) throw PackFailure(PackError::MissingFile, "install");
        fs::remove_all(unpacked, ec);
    }
    if (fs::exists(fs::symlink_status(target, ec))) {
        if (exchange(staged, target)) {
            fs::remove_all(staged, ec);
        } else {
            const auto old = root_ / (".outgoing-" + randomSuffix());
            fs::rename(target, old, ec);
            if (ec) {
                fs::remove_all(staged, ec);
                throw PackFailure(PackError::MissingFile, "install");
            }
            fs::rename(staged, target, ec);
            if (ec) {
                fs::rename(old, target, ec);
                fs::remove_all(staged, ec);
                throw PackFailure(PackError::MissingFile, "install");
            }
            fs::remove_all(old, ec);
        }
    } else {
        fs::rename(staged, target, ec);
        if (ec) {
            fs::remove_all(staged, ec);
            throw PackFailure(PackError::MissingFile, "install");
        }
    }
    return InstalledCharacter{m, target, false};
}

void CharacterStore::remove(const std::string& id) const {
    if (!isValidCharacterID(id)) throw PackFailure(PackError::InvalidID, id);
    const auto target = root_ / id;
    std::error_code ec;
    if (!fs::exists(fs::symlink_status(target, ec))) return;
    // Move it out of sight first, so a player never reads a half-deleted folder.
    const auto trash = root_ / (".removing-" + randomSuffix());
    fs::rename(target, trash, ec);
    fs::remove_all(ec ? target : trash, ec);
    if (ec) throw PackFailure(PackError::MissingFile, id);
}

std::vector<InstalledCharacter> CharacterStore::scan(const fs::path& folder, bool development) const {
    std::vector<std::string> names;
    std::error_code ec;
    for (fs::directory_iterator it(folder, ec), end; !ec && it != end; it.increment(ec)) {
        names.push_back(it->path().filename().string());
    }
    std::sort(names.begin(), names.end());
    std::vector<InstalledCharacter> out;
    for (const auto& name : names) {
        if (!isValidCharacterID(name)) continue;
        try {
            auto m = manifest(folder / name, appVersion_);
            if (m.id != name) continue;
            out.push_back({std::move(m), folder / name, development});
        } catch (const PackFailure&) {
        }
    }
    return out;
}

const char* errorKey(InstallError e) {
    switch (e) {
        case InstallError::Checksum: return "characters.error.checksum";
        case InstallError::NewerApp: return "characters.error.newerApp";
        case InstallError::InvalidPack:
        case InstallError::DisallowedURL: return "characters.error.invalid";
        case InstallError::Network: return "characters.error.network";
        case InstallError::Cancelled: return "";
    }
    return "characters.error.invalid";
}

std::optional<InstallError> installArchive(const CharacterStore& store, const CharacterCatalog::Entry& entry,
                                           const fs::path& archive) {
    if (!entry.isValid()) return InstallError::InvalidPack;
    std::error_code ec;
    const auto size = fs::file_size(archive, ec);
    if (ec || static_cast<std::int64_t>(size) != entry.size) return InstallError::Checksum;
    std::string expected = entry.sha256;
    for (auto& c : expected) c = static_cast<char>(c >= 'A' && c <= 'F' ? c - 'A' + 'a' : c);
    if (sha256File(archive) != expected) return InstallError::Checksum;
    fs::create_directories(store.root(), ec);
    const auto unpacked = store.root() / (".unpack-" + randomSuffix());
    struct Cleanup {
        fs::path path;
        ~Cleanup() {
            std::error_code e;
            fs::remove_all(path, e);
        }
    } cleanup{unpacked};
    try {
        ZipArchive zip(archive);
        pack_layout::validate(zip.entries());
        fs::create_directory(unpacked, ec);
        if (ec) return InstallError::InvalidPack;
        fs::permissions(unpacked, fs::perms::owner_all, fs::perm_options::replace, ec);
        zip.extractAll(unpacked);
        (void)store.install(unpacked, entry.id);
    } catch (const PackFailure& failure) {
        return failure.kind() == PackError::NewerApp ? InstallError::NewerApp : InstallError::InvalidPack;
    }
    return std::nullopt;
}

namespace auto_download {

std::vector<std::string> queue(const CharacterCatalog& catalog, const std::set<std::string>& installed,
                               const std::set<std::string>& setAside, const std::set<std::string>& skipped,
                               const std::vector<std::string>& vendors) {
    std::vector<const CharacterCatalog::Entry*> wanted;
    for (const auto& e : catalog.characters) {
        if (!installed.count(e.id) && !setAside.count(e.id) && !skipped.count(e.id)) wanted.push_back(&e);
    }
    std::vector<std::string> out;
    auto has = [&](const std::string& id) { return std::find(out.begin(), out.end(), id) != out.end(); };
    for (const auto& vendor : vendors) {
        for (const auto* e : wanted) {
            if (std::find(e->vendors.begin(), e->vendors.end(), vendor) != e->vendors.end() && !has(e->id)) out.push_back(e->id);
        }
    }
    for (const auto* e : wanted) {
        if (!has(e->id)) out.push_back(e->id);
    }
    return out;
}

}  // namespace auto_download

VendorReading VendorReading::of(const std::vector<AgentRow>& agents, const pricing::PriceTable* prices) {
    VendorReading out;
    std::optional<double> bestRate;
    for (const auto& agent : agents) {
        const double rate = agent.liveOutput;
        if (!(rate > 0) || !std::isfinite(rate)) continue;
        const auto r = vendors::resolve(agent.model, agent.source, prices);
        if (r.vendor) out.rates[*r.vendor] += rate;
        if (!bestRate || rate > *bestRate) {
            bestRate = rate;
            out.fastestModel = r.name;
            out.fastestVendor = r.vendor;
        }
    }
    return out;
}

std::vector<std::string> VendorReading::vendorsByRate() const {
    std::vector<std::pair<std::string, double>> live;
    for (const auto& [k, v] : rates) {
        if (v > 0) live.emplace_back(k, v);
    }
    std::sort(live.begin(), live.end(), [this](const auto& a, const auto& b) {
        if (a.second != b.second) return a.second > b.second;
        if (a.first == fastestVendor) return true;
        if (b.first == fastestVendor) return false;
        return a.first < b.first;
    });
    std::vector<std::string> out;
    for (const auto& [k, v] : live) out.push_back(k);
    return out;
}

namespace vendors {

namespace {

std::string trimmed(std::string_view s) {
    const auto isSpace = [](char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r'; };
    while (!s.empty() && isSpace(s.front())) s.remove_prefix(1);
    while (!s.empty() && isSpace(s.back())) s.remove_suffix(1);
    return std::string(s);
}

}  // namespace

std::optional<std::string> family(const std::optional<std::string>& model) {
    if (!model) return std::nullopt;
    std::string_view m = *model;
    while (!m.empty() && m.back() == '/') m.remove_suffix(1);
    if (m.empty()) return std::nullopt;
    const auto slash = m.rfind('/');
    std::string id = trimmed(slash == std::string_view::npos ? m : m.substr(slash + 1));
    for (auto& c : id) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    static const std::pair<const char*, const char*> families[] = {
        {"doubao", "bytedance"}, {"seed-", "bytedance"}, {"ui-tars", "bytedance"}, {"llama", "meta"},
        {"sonar", "perplexity"}, {"pplx", "perplexity"}, {"r1-1776", "perplexity"}, {"ernie", "baidu"},
        {"hunyuan", "tencent"},  {"rwkv", "rwkv"},
    };
    for (const auto& [prefix, vendor] : families) {
        if (id.rfind(prefix, 0) == 0) return std::string(vendor);
    }
    return std::nullopt;
}

std::optional<std::string> fallback(SourceKind source) {
    switch (source) {
        case SourceKind::ClaudeCode:
        case SourceKind::ClaudeDesktop: return "anthropic";
        case SourceKind::Codex: return "openai";
        case SourceKind::Gemini:
        case SourceKind::Antigravity: return "google";
        case SourceKind::Deepseek: return "deepseek";
        case SourceKind::Kimi: return "moonshot";
        case SourceKind::GrokBuild: return "xai";
        case SourceKind::ZCode: return "zhipu";
        case SourceKind::WorkBuddy:
        case SourceKind::WorkBuddyAi: return "tencent";
        default: return std::nullopt;
    }
}

Resolved resolve(const std::optional<std::string>& model, SourceKind source, const pricing::PriceTable* prices) {
    if (prices) {
        const auto r = prices->resolver().resolve(model, std::nullopt);
        if (r.kind == pricing::ResolutionKind::Priced && r.index >= 0 &&
            static_cast<std::size_t>(r.index) < prices->models().size()) {
            const auto& entry = prices->models()[static_cast<std::size_t>(r.index)];
            std::optional<std::string> vendor = entry.provider;
            if (entry.provider == "opencode") {
                vendor = family(model);
                if (!vendor) vendor = fallback(source);
            }
            return {vendor, entry.name};
        }
    }
    std::optional<std::string> raw;
    if (model) raw = trimmed(*model);
    auto vendor = family(raw);
    if (!vendor) vendor = fallback(source);
    return {vendor, raw && !raw->empty() ? raw : std::nullopt};
}

std::optional<std::string> accent(const std::optional<std::string>& vendor, const std::vector<InstalledCharacter>& installed,
                                  const CharacterCatalog* catalog) {
    if (!vendor || vendor->empty()) return std::nullopt;
    const auto valid = [](const std::string& hex) { return CharacterManifest::rgb(hex).has_value(); };
    const auto standsFor = [&](const std::vector<std::string>& vendors) {
        return std::find(vendors.begin(), vendors.end(), *vendor) != vendors.end();
    };
    for (const auto& pack : installed) {
        if (standsFor(pack.manifest.vendors) && valid(pack.manifest.accent)) return pack.manifest.accent;
    }
    if (catalog) {
        for (const auto& entry : catalog->characters) {
            if (standsFor(entry.vendors) && valid(entry.accent)) return entry.accent;
        }
    }
    return std::nullopt;
}

}  // namespace vendors
}  // namespace agentspeed::character
