#include <array>
#include <set>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/location_lists.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/qoder.hpp"
#include "agentspeed/text.hpp"

// METRICS 2, 4.18 Roots and discovery, 4.19 Roots: where Qoder IDE and Qoder CLI keep their data.
namespace agentspeed::qoder {

namespace {

constexpr std::array<const char*, 2> kEditions = {"Qoder", "QoderCN"};
constexpr const char* kDatabaseTail = "SharedClientCache/cache/db/local.db";
// What a user location may be, in lookup order: the folder that holds `local.db`, the `SharedClientCache` folder, the
// IDE's data folder.
constexpr std::array<const char*, 3> kLocationTails = {"local.db", "cache/db/local.db", "SharedClientCache/cache/db/local.db"};
constexpr std::array<std::pair<const char*, const char*>, 2> kCliFolders = {{{"QODER_CONFIG_DIR", ".qoder"}, {"QODERCN_CONFIG_DIR", ".qoder-cn"}}};

std::optional<std::string> variable(const Environment& environment, const std::string& name) {
    auto value = environment(name);
    if (!value || value->empty()) return std::nullopt;
    return value;
}

int hexValue(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// `%XX` sequences as bytes, read as UTF-8 (invalid sequences become U+FFFD); any other `%` stays.
std::string percentDecoded(std::string_view text) {
    std::string bytes;
    bytes.reserve(text.size());
    for (std::size_t i = 0; i < text.size(); ++i) {
        if (text[i] == '%' && i + 2 < text.size()) {
            const int high = hexValue(text[i + 1]), low = hexValue(text[i + 2]);
            if (high >= 0 && low >= 0) {
                bytes.push_back(static_cast<char>(high << 4 | low));
                i += 2;
                continue;
            }
        }
        bytes.push_back(text[i]);
    }
    return text::sanitizeUtf8(bytes);
}

bool isAsciiLetter(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }

}  // namespace

std::string dataFolder(const Environment& environment, const std::string& home) {
    return variable(environment, "XDG_CONFIG_HOME").value_or(fsx::join(home, ".config"));
}

std::vector<std::string> automaticDatabases(const Environment& environment, const std::string& home) {
    const auto data = dataFolder(environment, home);
    std::vector<std::string> out;
    for (const auto* edition : kEditions) out.push_back(fsx::join(fsx::join(data, edition), kDatabaseTail));
    return out;
}

bool isDatabaseFile(const std::string& path) { return !path.empty() && fsx::isPlainFile(path); }

std::optional<std::string> databaseOf(const std::string& folder) {
    if (folder.empty()) return std::nullopt;
    for (const auto* tail : kLocationTails) {
        if (auto candidate = fsx::join(folder, tail); isDatabaseFile(candidate)) return candidate;
    }
    return std::nullopt;
}

std::string expectedDatabaseOf(const std::string& folder) {
    for (auto it = kLocationTails.rbegin(); it != kLocationTails.rend(); ++it) {
        auto candidate = fsx::join(folder, *it);
        if (fsx::isDirectory(fsx::parentOf(candidate))) return candidate;
    }
    return fsx::join(folder, kDatabaseName);
}

std::optional<std::string> Root::databaseNow() const {
    if (!isDatabase) return databaseOf(path);
    return isDatabaseFile(path) ? std::optional<std::string>(path) : std::nullopt;
}

std::string Root::locationKey() const { return resolvedPath(databaseNow().value_or(isDatabase ? path : expectedDatabaseOf(path))); }

std::string Root::displayFolder() const { return isDatabase ? fsx::parentOf(path) : path; }

std::vector<std::string> databases(const std::vector<Root>& roots) {
    std::set<std::string> seen;
    std::vector<std::string> out;
    for (const auto& root : roots) {
        auto database = root.databaseNow();
        if (database && seen.insert(resolvedPath(*database)).second) out.push_back(std::move(*database));
    }
    return out;
}

std::string displayRoot(const std::vector<Root>& roots) {
    for (const auto& root : roots) {
        if (root.databaseNow()) return root.displayFolder();
    }
    return roots.empty() ? std::string() : roots.front().displayFolder();
}

std::vector<std::string> cliRoots(const Environment& environment, const std::string& home) {
    std::vector<std::string> out;
    for (const auto& [name, folder] : kCliFolders) {
        out.push_back(fsx::join(variable(environment, name).value_or(fsx::join(home, folder)), "projects"));
    }
    return out;
}

std::string cliProjectsOf(const std::string& location) {
    auto folder = fsx::trimTrailing(location);
    if (folder.empty()) folder = location;
    return path_text::basename(folder) == "projects" ? folder : fsx::join(folder, "projects");
}

std::optional<std::string> projectPath(const std::optional<std::string>& uri) {
    if (!uri || text::strip(*uri).empty()) return std::nullopt;
    const std::string_view text(*uri);
    if (text.size() < 7 || !fsx::equalsNoCase(text.substr(0, 7), "file://")) return *uri;
    auto rest = text.substr(7);
    if (rest.empty() || rest.front() != '/') {
        const auto slash = rest.find('/');
        rest = slash == std::string_view::npos ? std::string_view() : rest.substr(slash);
    }
    auto decoded = percentDecoded(rest);
    if (decoded.size() >= 3 && decoded[0] == '/' && isAsciiLetter(decoded[1]) && decoded[2] == ':') decoded.erase(0, 1);
    if (decoded.empty()) return std::nullopt;
    return decoded;
}

}  // namespace agentspeed::qoder
