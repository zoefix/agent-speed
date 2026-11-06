#include <set>

#include "agentspeed/devin.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/location_lists.hpp"

// METRICS 2, 4.17 Roots and discovery: where the Devin CLI keeps its sessions.
namespace agentspeed::devin {

namespace {

constexpr const char* kCliFolder = "cli";

std::optional<std::string> variable(const std::function<std::optional<std::string>(const std::string&)>& environment,
                                    const std::string& name) {
    auto value = environment(name);
    if (!value || value->empty()) return std::nullopt;
    return value;
}

}  // namespace

std::string automaticDatabase(const std::function<std::optional<std::string>(const std::string&)>& environment,
                              const std::string& home) {
    if (auto overridden = variable(environment, "CHISEL_SESSION_DB")) return *overridden;
    const auto data = variable(environment, "XDG_DATA_HOME").value_or(fsx::join(home, ".local/share"));
    return fsx::join(fsx::join(fsx::join(data, "devin"), kCliFolder), kDatabaseName);
}

bool isDatabaseFile(const std::string& path) { return !path.empty() && fsx::isPlainFile(path); }

std::optional<std::string> databaseOf(const std::string& folder) {
    if (folder.empty()) return std::nullopt;
    if (auto direct = fsx::join(folder, kDatabaseName); isDatabaseFile(direct)) return direct;
    if (auto nested = fsx::join(fsx::join(folder, kCliFolder), kDatabaseName); isDatabaseFile(nested)) return nested;
    return std::nullopt;
}

std::optional<std::string> Root::databaseNow() const {
    if (!isDatabase) return databaseOf(path);
    return isDatabaseFile(path) ? std::optional<std::string>(path) : std::nullopt;
}

std::string Root::locationKey() const {
    return resolvedPath(databaseNow().value_or(isDatabase ? path : fsx::join(path, kDatabaseName)));
}

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

}  // namespace agentspeed::devin
