#include <climits>
#include <cstdlib>
#include <memory>

#include "agentspeed/devin.hpp"
#include "agentspeed/droid.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/harness.hpp"
#include "agentspeed/location_lists.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/opencode.hpp"
#include "agentspeed/qoder.hpp"

// METRICS 2.1 Editing: adding (in the rule order of the spec), removing and the same-location keys.
namespace agentspeed {

namespace {

constexpr const char* kOpenClawDatabase = "openclaw-agent.sqlite";

// The root or database a user location names, before resolving.
std::string namedRoot(const std::string& path, RootFamily list) {
    switch (list) {
        case RootFamily::OpenCode: return opencode::Database::databasePath(path);
        case RootFamily::ZCode: return harness::zcodeDatabase(path);
        case RootFamily::OpenClaw: {
            const auto database = fsx::join(path, kOpenClawDatabase);
            return fsx::isPlainFile(database) ? database : path;
        }
        case RootFamily::Hermes: return path;
        // A Devin data folder and its `cli` folder name one `sessions.db`.
        case RootFamily::Devin: return devin::Root::location(path).databaseNow().value_or(fsx::join(path, devin::kDatabaseName));
        // A Qoder IDE data folder, its `SharedClientCache` folder and its `cache/db` folder name one `local.db`.
        case RootFamily::Qoder: return qoder::databaseOf(path).value_or(qoder::expectedDatabaseOf(path));
        default: return userRoot(path, list);
    }
}

}  // namespace

std::string resolvedPath(const std::string& path) {
    const std::unique_ptr<char, decltype(&std::free)> real(::realpath(path.c_str(), nullptr), &std::free);
    return real ? std::string(real.get()) : fsx::normalize(path);
}

std::string locationKey(const std::string& path, RootFamily list) { return resolvedPath(namedRoot(path, list)); }

bool automaticIsWiderThanDefault(RootFamily list) {
    return list == RootFamily::Omp || list == RootFamily::Antigravity || list == RootFamily::Pi || list == RootFamily::OpenClaw;
}

bool acceptsDatabaseFile(RootFamily list) { return list == RootFamily::OpenClaw; }

AddOutcome LocationLists::add(std::string_view raw, RootFamily list, const AutomaticKeys& automatic) {
    const auto path = standardized(raw);
    if (!path) return AddOutcome::Invalid;
    const auto key = locationKey(*path, list);
    const auto entries = (*this)[list];
    for (const auto& entry : entries) {
        if (!entry.automatic && locationKey(entry.path, list) == key) return AddOutcome::AlreadyListed;
    }
    if (hasAutomatic(list)) {
        for (const auto& known : automatic.keys) {
            if (known == key) return AddOutcome::AlreadyAutomatic;
        }
    } else if (!automaticIsWiderThanDefault(list) && !automatic.keys.empty() && automatic.keys.front() == key) {
        if (entries.size() >= kMaxEntries) return AddOutcome::Full;
        LocationList next{DataLocation::automaticEntry()};
        next.insert(next.end(), entries.begin(), entries.end());
        set(list, next);
        return AddOutcome::RestoredAutomatic;
    }
    if (entries.size() >= kMaxEntries) return AddOutcome::Full;
    auto next = entries;
    next.push_back(DataLocation::at(*path));
    set(list, next);
    return AddOutcome::Added;
}

void LocationLists::remove(RootFamily list, const DataLocation& entry) {
    if (!canRemove(list)) return;
    LocationList next;
    for (const auto& e : (*this)[list]) {
        if (!(e == entry)) next.push_back(e);
    }
    set(list, next);
}

}  // namespace agentspeed
