#include <algorithm>
#include <set>

#include "agentspeed/droid.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/harness.hpp"
#include "agentspeed/hermes.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/opencode.hpp"

// METRICS 2.1 Reading: how the location lists become roots — every entry names roots, a source reads the union in list
// order, and a root or database named twice is read once.
namespace agentspeed {

namespace {

SourceKind sourceOf(RootFamily family) {
    switch (family) {
        case RootFamily::Codex: return SourceKind::Codex;
        case RootFamily::Claude: return SourceKind::ClaudeCode;
        case RootFamily::DeepSeek: return SourceKind::Deepseek;
        case RootFamily::Gemini: return SourceKind::Gemini;
        case RootFamily::Omp: return SourceKind::Omp;
        case RootFamily::Antigravity: return SourceKind::Antigravity;
        case RootFamily::WorkBuddy: return SourceKind::WorkBuddy;
        case RootFamily::WorkBuddyAi: return SourceKind::WorkBuddyAi;
        case RootFamily::OpenCode: return SourceKind::OpenCode;
        case RootFamily::ZCode: return SourceKind::ZCode;
        case RootFamily::Kimi: return SourceKind::Kimi;
        case RootFamily::GrokBuild: return SourceKind::GrokBuild;
        case RootFamily::Pi: return SourceKind::Pi;
        case RootFamily::OpenClaw: return SourceKind::OpenClaw;
        case RootFamily::Hermes: return SourceKind::Hermes;
        case RootFamily::Droid: return SourceKind::Droid;
        case RootFamily::Devin: return SourceKind::Devin;
        case RootFamily::Qoder: return SourceKind::Qoder;
        case RootFamily::QoderCli: return SourceKind::QoderCli;
    }
    return SourceKind::Codex;
}

// Normalized paths in order, each once.
std::vector<std::string> unique(const std::vector<std::string>& paths) {
    std::set<std::string> seen;
    std::vector<std::string> out;
    for (const auto& p : paths) {
        auto normal = paths::normalizeRoot(p);
        if (!normal.empty() && seen.insert(normal).second) out.push_back(std::move(normal));
    }
    return out;
}

// Normalized roots in order, without a later root whose database resolves to the same file as an earlier one's.
std::vector<std::string> uniqueDatabases(const std::vector<std::string>& roots, std::string (*database)(const std::string&)) {
    std::set<std::string> seen;
    std::vector<std::string> out;
    for (const auto& root : unique(roots)) {
        if (seen.insert(resolvedPath(database(root))).second) out.push_back(root);
    }
    return out;
}

std::string openCodeDatabase(const std::string& root) { return opencode::Database::databasePath(root); }

// Every entry's roots in list order: the automatic roots, or what `user` makes of a user location.
template <typename User>
std::vector<std::string> entriesRoots(RootFamily family, const LocationLists& lists, const Environment& environment,
                                      const std::string& home, User user) {
    std::vector<std::string> out;
    for (const auto& entry : lists[family]) {
        if (entry.automatic) {
            for (auto& root : automaticRoots(family, lists, environment, home)) out.push_back(std::move(root));
        } else {
            out.push_back(user(entry.path));
        }
    }
    return out;
}

std::string itself(const std::string& path) { return path; }

std::vector<std::string> openClawRootsOf(const LocationLists& lists, const Environment& environment, const std::string& home) {
    std::vector<std::string> roots;
    for (const auto& entry : lists[RootFamily::OpenClaw]) {
        if (entry.automatic) {
            for (auto& folder : paths::openClawStateFolders(environment, home)) roots.push_back(std::move(folder));
        } else {
            roots.push_back(entry.path);
        }
    }
    return unique(roots);
}

// `settings.json` in the folder that holds a `tmp` root, or in the root itself when it has another name.
std::string geminiSettingsBeside(const std::string& root) {
    return paths::geminiSettingsOf({root}, [](const std::string&) { return std::nullopt; }, "/");
}

}  // namespace

std::string userRoot(const std::string& path, RootFamily family) {
    switch (family) {
        case RootFamily::Antigravity: return paths::antigravityConversationsOf(path);
        case RootFamily::WorkBuddy:
        case RootFamily::WorkBuddyAi: return paths::workBuddyProjectsOf(path);
        case RootFamily::Kimi:
        case RootFamily::GrokBuild: return harness::sessionsOf(path);
        case RootFamily::Droid: return droid::rootOf(path);
        case RootFamily::QoderCli: return qoder::cliProjectsOf(path);
        default: return path;
    }
}

std::vector<std::string> automaticRoots(RootFamily list, const LocationLists& lists, const Environment& environment,
                                        const std::string& home) {
    const auto main = SourceLocations::defaultRoot(sourceOf(list), environment, home);
    switch (list) {
        case RootFamily::Codex: {
            std::vector<std::string> roots{main};
            for (auto& root : paths::openClawCodexRoots(paths::openClawDatabases(openClawRootsOf(lists, environment, home)))) {
                roots.push_back(std::move(root));
            }
            return roots;
        }
        case RootFamily::Omp: return paths::ompRoots(environment, home);
        case RootFamily::Pi: return paths::piRoots(environment, home);
        case RootFamily::OpenClaw: return paths::openClawStateFolders(environment, home);
        case RootFamily::Antigravity: {
            std::vector<std::string> roots;
            for (const auto& store : paths::antigravityStores(home + "/.gemini", std::nullopt)) {
                roots.push_back(paths::antigravityConversationsOf(store));
            }
            return roots;
        }
        case RootFamily::WorkBuddy:
        case RootFamily::WorkBuddyAi: return {paths::workBuddyProjectsOf(main)};
        case RootFamily::Devin: return {paths::normalizeRoot(devin::automaticDatabase(environment, home))};
        case RootFamily::Qoder: {
            std::vector<std::string> roots;
            for (const auto& database : qoder::automaticDatabases(environment, home)) roots.push_back(paths::normalizeRoot(database));
            return roots;
        }
        case RootFamily::QoderCli: return qoder::cliRoots(environment, home);
        default: return {main};
    }
}

AutomaticKeys automaticKeys(RootFamily list, const LocationLists& lists, const Environment& environment, const std::string& home) {
    AutomaticKeys out;
    std::set<std::string> seen;
    for (const auto& root : automaticRoots(list, lists, environment, home)) {
        std::string named = root;
        if (list == RootFamily::OpenCode) named = opencode::Database::databasePath(root);
        if (list == RootFamily::ZCode) named = harness::zcodeDatabase(root);
        if (list == RootFamily::OpenClaw) named = locationKey(root, list);
        auto key = list == RootFamily::Devin   ? devin::Root::database(root).locationKey()
                   : list == RootFamily::Qoder ? qoder::Root::database(root).locationKey()
                                               : resolvedPath(named);
        if (seen.insert(key).second) out.keys.push_back(std::move(key));
    }
    return out;
}

SourceLocations SourceLocations::resolve(const LocationLists& lists, const Environment& environment, const std::string& home) {
    const auto rootsOf = [&](RootFamily family, std::string (*user)(const std::string&)) {
        return unique(entriesRoots(family, lists, environment, home, user));
    };
    SourceLocations l;
    // The agent-scoped Codex homes are added where the automatic entry stands by fileRoots(), as OpenClaw finds them.
    std::vector<std::string> codex;
    for (const auto& entry : lists[RootFamily::Codex]) {
        codex.push_back(entry.automatic ? defaultRoot(SourceKind::Codex, environment, home) : entry.path);
    }
    l.codexRoots = unique(codex);
    l.claudeRoots = rootsOf(RootFamily::Claude, itself);
    l.dshRoots = rootsOf(RootFamily::DeepSeek, itself);
    l.geminiRoots = rootsOf(RootFamily::Gemini, itself);
    l.ompRoots = rootsOf(RootFamily::Omp, itself);
    l.workBuddyRoots = rootsOf(RootFamily::WorkBuddy, paths::workBuddyProjectsOf);
    l.workBuddyAiRoots = rootsOf(RootFamily::WorkBuddyAi, paths::workBuddyProjectsOf);
    l.kimiRoots = rootsOf(RootFamily::Kimi, harness::sessionsOf);
    l.grokBuildRoots = rootsOf(RootFamily::GrokBuild, harness::sessionsOf);
    l.piRoots = rootsOf(RootFamily::Pi, itself);
    l.droidRoots = rootsOf(RootFamily::Droid, droid::rootOf);
    l.qoderCliRoots = rootsOf(RootFamily::QoderCli, qoder::cliProjectsOf);

    std::vector<std::string> stores;
    for (const auto& entry : lists[RootFamily::Antigravity]) {
        const auto setting = entry.automatic ? std::nullopt : std::optional<std::string>(entry.path);
        for (auto& store : paths::antigravityStores(home + "/.gemini", setting)) stores.push_back(std::move(store));
    }
    l.antigravityStores = unique(stores);

    const auto databaseRoots = [&](RootFamily family) {
        std::vector<std::string> roots;
        for (const auto& entry : lists[family]) {
            roots.push_back(entry.automatic ? defaultRoot(sourceOf(family), environment, home) : entry.path);
        }
        return roots;
    };
    l.openCodeRoots = uniqueDatabases(databaseRoots(RootFamily::OpenCode), openCodeDatabase);
    l.zcodeRoots = uniqueDatabases(databaseRoots(RootFamily::ZCode), harness::zcodeDatabase);

    std::vector<std::string> users;
    for (const auto& entry : lists[RootFamily::OpenClaw]) {
        if (!entry.automatic) users.push_back(entry.path);
    }
    l.openClawUserRoots = unique(users);
    l.openClawRoots = openClawRootsOf(lists, environment, home);

    std::set<std::string> hermesSeen;
    for (const auto& entry : lists[RootFamily::Hermes]) {
        HermesRoot root{entry.automatic ? hermes::home(environment, false, home) : entry.path, !entry.automatic};
        root.path = paths::normalizeRoot(root.path);
        if (!root.path.empty() && hermesSeen.insert(root.path).second) l.hermesRoots.push_back(std::move(root));
    }

    for (const auto& entry : lists[RootFamily::Devin]) {
        auto root = entry.automatic ? devin::Root::database(paths::normalizeRoot(devin::automaticDatabase(environment, home)))
                                    : devin::Root::location(paths::normalizeRoot(entry.path));
        if (!root.path.empty() && std::find(l.devinRoots.begin(), l.devinRoots.end(), root) == l.devinRoots.end()) {
            l.devinRoots.push_back(std::move(root));
        }
    }

    const auto addQoder = [&](qoder::Root root) {
        if (!root.path.empty() && std::find(l.qoderRoots.begin(), l.qoderRoots.end(), root) == l.qoderRoots.end()) {
            l.qoderRoots.push_back(std::move(root));
        }
    };
    for (const auto& entry : lists[RootFamily::Qoder]) {
        if (!entry.automatic) {
            addQoder(qoder::Root::location(paths::normalizeRoot(entry.path)));
            continue;
        }
        for (const auto& database : qoder::automaticDatabases(environment, home)) addQoder(qoder::Root::database(paths::normalizeRoot(database)));
    }

    for (const auto& entry : lists[RootFamily::Gemini]) {
        const auto roots = entry.automatic ? automaticRoots(RootFamily::Gemini, lists, environment, home) : std::vector<std::string>{entry.path};
        const auto file = entry.automatic ? paths::geminiSettingsOf(roots, environment, home) : geminiSettingsBeside(entry.path);
        for (const auto& root : roots) l.geminiSettings.emplace(paths::normalizeRoot(root), file);
    }

    l.codexAutomatic = lists.hasAutomatic(RootFamily::Codex);
    l.ompAutomatic = lists.hasAutomatic(RootFamily::Omp);
    l.piAutomatic = lists.hasAutomatic(RootFamily::Pi);
    l.openClawAutomatic = lists.hasAutomatic(RootFamily::OpenClaw);
    return l;
}

std::vector<std::pair<RootFamily, std::string>> SourceLocations::fileRoots() const {
    std::vector<std::pair<RootFamily, std::string>> roots;
    const auto add = [&](RootFamily family, const std::vector<std::string>& paths) {
        for (const auto& path : unique(paths)) roots.emplace_back(family, path);
    };
    const auto pis = unique(piRoots);
    auto codex = codexRoots;
    if (codexAutomatic && !codex.empty()) {
        const auto homes = paths::openClawCodexRoots(paths::openClawDatabases(openClawRoots));
        codex.insert(codex.begin() + 1, homes.begin(), homes.end());
    }
    add(RootFamily::Codex, codex);
    add(RootFamily::Claude, claudeRoots);
    add(RootFamily::DeepSeek, dshRoots);
    add(RootFamily::Gemini, geminiRoots);
    add(RootFamily::Omp, paths::withoutPi(unique(ompRoots), pis));
    std::vector<std::string> conversations;
    for (const auto& store : antigravityStores) conversations.push_back(paths::antigravityConversationsOf(store));
    add(RootFamily::Antigravity, conversations);
    add(RootFamily::WorkBuddy, workBuddyRoots);
    add(RootFamily::WorkBuddyAi, workBuddyAiRoots);
    add(RootFamily::Kimi, kimiRoots);
    add(RootFamily::GrokBuild, grokBuildRoots);
    add(RootFamily::Pi, pis);
    add(RootFamily::Droid, droidRoots);
    add(RootFamily::QoderCli, qoderCliRoots);
    return roots;
}

std::vector<std::string> SourceLocations::openClawRootsNow(const Environment& environment, const std::string& home) const {
    if (!openClawAutomatic) return openClawUserRoots;
    auto roots = paths::openClawStateFolders(environment, home);
    roots.insert(roots.end(), openClawUserRoots.begin(), openClawUserRoots.end());
    return unique(roots);
}

std::vector<std::string> SourceLocations::hermesFolders() const {
    std::vector<std::string> folders;
    std::set<std::string> seen;
    for (const auto& root : hermesRoots) {
        for (const auto& folder : unique(root.logFolders())) {
            if (seen.insert(resolvedPath(folder)).second) folders.push_back(folder);
        }
    }
    return folders;
}

bool SourceLocations::readsSame(const SourceLocations& other) const {
    return fileRoots() == other.fileRoots() && geminiSettings == other.geminiSettings && openCodeRoots == other.openCodeRoots &&
           zcodeRoots == other.zcodeRoots && openClawRoots == other.openClawRoots && openClawUserRoots == other.openClawUserRoots &&
           hermesRoots == other.hermesRoots && devinRoots == other.devinRoots && qoderRoots == other.qoderRoots &&
           codexAutomatic == other.codexAutomatic && ompAutomatic == other.ompAutomatic &&
           piAutomatic == other.piAutomatic && openClawAutomatic == other.openClawAutomatic;
}

}  // namespace agentspeed
