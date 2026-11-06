#include "agentspeed/location_entries.hpp"

#include <algorithm>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/path_text.hpp"

namespace agentspeed {

namespace {

bool isDatabaseList(RootFamily list) {
    return list == RootFamily::OpenCode || list == RootFamily::ZCode || list == RootFamily::OpenClaw;
}

bool locationExists(RootFamily list, const std::string& path) {
    // A Devin folder counts when it holds the CLI database (a Devin Desktop folder never does).
    if (list == RootFamily::Devin) return devin::databaseOf(path).has_value();
    return fsx::isDirectory(path) || (isDatabaseList(list) && fsx::isRegularFile(path));
}

std::string defaultOf(RootFamily list, const Environment& environment, const std::string& home) {
    switch (list) {
        case RootFamily::Codex: return SourceLocations::defaultRoot(SourceKind::Codex, environment, home);
        case RootFamily::Claude: return SourceLocations::defaultRoot(SourceKind::ClaudeCode, environment, home);
        case RootFamily::DeepSeek: return SourceLocations::defaultRoot(SourceKind::Deepseek, environment, home);
        case RootFamily::OpenCode: return SourceLocations::defaultRoot(SourceKind::OpenCode, environment, home);
        case RootFamily::Gemini: return SourceLocations::defaultRoot(SourceKind::Gemini, environment, home);
        case RootFamily::WorkBuddy: return SourceLocations::defaultRoot(SourceKind::WorkBuddy, environment, home);
        case RootFamily::WorkBuddyAi: return SourceLocations::defaultRoot(SourceKind::WorkBuddyAi, environment, home);
        case RootFamily::ZCode: return SourceLocations::defaultRoot(SourceKind::ZCode, environment, home);
        case RootFamily::Kimi: return SourceLocations::defaultRoot(SourceKind::Kimi, environment, home);
        case RootFamily::GrokBuild: return SourceLocations::defaultRoot(SourceKind::GrokBuild, environment, home);
        case RootFamily::Hermes: return SourceLocations::defaultRoot(SourceKind::Hermes, environment, home);
        case RootFamily::Droid: return SourceLocations::defaultRoot(SourceKind::Droid, environment, home);
        case RootFamily::Devin: return SourceLocations::defaultRoot(SourceKind::Devin, environment, home);
        default: return {};
    }
}

}  // namespace

std::vector<std::string> defaultLocations(RootFamily list, const Environment& environment, const std::string& home) {
    switch (list) {
        case RootFamily::Omp: return paths::ompRoots(environment, home);
        case RootFamily::Pi: return paths::piRoots(environment, home);
        case RootFamily::Antigravity: return paths::antigravityStores(home + "/.gemini", std::nullopt);
        case RootFamily::OpenClaw: return paths::openClawStateFolders(environment, home);
        default: {
            const auto root = defaultOf(list, environment, home);
            return root.empty() ? std::vector<std::string>{} : std::vector<std::string>{root};
        }
    }
}

std::vector<std::string> automaticShown(RootFamily list, const Environment& environment, const std::string& home) {
    switch (list) {
        case RootFamily::Omp: return {paths::ompDefaultRoot(environment, home)};
        case RootFamily::Pi: return {paths::piDefaultRoot(environment, home)};
        case RootFamily::Antigravity:
        case RootFamily::OpenClaw: {
            const auto defaults = defaultLocations(list, environment, home);
            std::vector<std::string> found;
            std::copy_if(defaults.begin(), defaults.end(), std::back_inserter(found),
                         [&](const std::string& path) { return isReadableLocation(list, path); });
            if (!found.empty()) return found;
            if (list == RootFamily::OpenClaw && !defaults.empty()) return {defaults.front()};
            return defaults;
        }
        default: return defaultLocations(list, environment, home);
    }
}

bool isReadableLocation(RootFamily list, const std::string& path) {
    return locationExists(list, path) && (list != RootFamily::Antigravity || !paths::isAntigravityBackupStore(path));
}

LocationEntry locationEntry(RootFamily list, const DataLocation& location, const Environment& environment, const std::string& home) {
    LocationEntry entry;
    entry.location = location;
    const auto reads = location.automatic ? defaultLocations(list, environment, home) : std::vector<std::string>{location.path};
    const auto shown = location.automatic ? automaticShown(list, environment, home) : reads;
    // Devin's automatic entry reads one database, which `$CHISEL_SESSION_DB` may give another name.
    const bool automaticDevin = location.automatic && list == RootFamily::Devin;
    const auto isReadable = [&](const std::string& path) {
        if (automaticDevin) return devin::Root::database(devin::automaticDatabase(environment, home)).databaseNow().has_value();
        return isReadableLocation(list, path);
    };
    for (const auto& path : shown) entry.shown.emplace_back(path, isReadable(path));
    const auto readable = std::find_if(reads.begin(), reads.end(), isReadable);
    entry.exists = readable != reads.end();
    if (entry.exists) {
        entry.reveal = *readable;
    } else if (const auto any = std::find_if(reads.begin(), reads.end(), [&](const std::string& p) { return locationExists(list, p); });
               any != reads.end()) {
        entry.reveal = *any;
    }
    if (!location.automatic && isDatabaseList(list)) {
        const auto name = path_text::basename(location.path);
        const bool databaseName = fsx::endsWithNoCase(name, ".sqlite") || fsx::endsWithNoCase(name, ".db");
        entry.isFile = fsx::isRegularFile(location.path) || (databaseName && !fsx::isDirectory(location.path));
    }
    return entry;
}

std::vector<LocationEntry> locationEntries(RootFamily list, const LocationLists& lists, const Environment& environment,
                                           const std::string& home) {
    std::vector<LocationEntry> entries;
    for (const auto& location : lists[list]) entries.push_back(locationEntry(list, location, environment, home));
    return entries;
}

std::string abbreviatedPath(const std::string& path, const std::string& home) {
    const auto base = fsx::trimTrailing(home);
    if (base.empty() || base == "/") return path;
    if (path == base) return "~";
    if (path.size() > base.size() && path.compare(0, base.size(), base) == 0 && path[base.size()] == '/') {
        std::string out = "~";
        out.append(path, base.size(), std::string::npos);
        return out;
    }
    return path;
}

}  // namespace agentspeed
