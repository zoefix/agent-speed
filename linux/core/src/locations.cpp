#include "agentspeed/locations.hpp"

#include <unistd.h>

#include <algorithm>
#include <cstdlib>
#include <fstream>
#include <regex>
#include <set>

#include "agentspeed/droid.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/harness.hpp"
#include "agentspeed/hermes.hpp"
#include "agentspeed/json.hpp"
#include "agentspeed/path_text.hpp"

namespace agentspeed {

namespace {

std::optional<std::string> variable(const Environment& environment, const std::string& name) {
    auto value = environment(name);
    if (!value || value->empty()) return std::nullopt;
    return value;
}

std::string trimSpaces(std::string text) {
    const auto first = text.find_first_not_of(" \t\r\n\v\f");
    if (first == std::string::npos) return {};
    const auto last = text.find_last_not_of(" \t\r\n\v\f");
    return text.substr(first, last - first + 1);
}

std::vector<std::string> distinct(const std::vector<std::string>& paths) {
    std::set<std::string> seen;
    std::vector<std::string> out;
    for (const auto& p : paths) {
        if (!p.empty() && seen.insert(fsx::trimTrailing(p)).second) out.push_back(p);
    }
    return out;
}

std::vector<std::string> plainSubfolders(const std::string& folder) {
    std::vector<std::string> names;
    for (const auto& e : fsx::list(folder)) {
        if (e.stat.directory) names.push_back(e.name);
    }
    std::sort(names.begin(), names.end());
    return names;
}

std::vector<std::string> normalizeAll(const std::vector<std::string>& roots) {
    std::vector<std::string> out;
    for (const auto& r : roots) {
        if (auto n = paths::normalizeRoot(r); !n.empty()) out.push_back(n);
    }
    return out;
}

std::string ompConfigFolder(const Environment& environment, const std::string& home) {
    auto name = variable(environment, "PI_CONFIG_DIR").value_or(".omp");
    const auto start = name.find_first_not_of("/\\");
    return fsx::join(home, start == std::string::npos ? std::string() : name.substr(start));
}

std::string piAgentFolder(const Environment& environment, const std::string& home) {
    if (auto agent = variable(environment, "PI_CODING_AGENT_DIR")) return paths::expandHome(*agent, home);
    return home + "/.pi/agent";
}

std::optional<std::string> piSessionDirSetting(const std::string& settingsPath) {
    std::ifstream in(settingsPath, std::ios::binary);
    if (!in) return std::nullopt;
    std::string text(1'048'576, '\0');
    in.read(text.data(), static_cast<std::streamsize>(text.size()));
    text.resize(static_cast<std::size_t>(in.gcount()));
    if (text.rfind("\xEF\xBB\xBF", 0) == 0) text.erase(0, 3);
    json::Document document;
    if (!document.parse(text) || !document.root().isObject()) return std::nullopt;
    const auto value = document.root()["sessionDir"].string();
    if (!value) return std::nullopt;
    auto custom = trimSpaces(*value);
    if (custom.empty()) return std::nullopt;
    return custom;
}

bool isSameOrInside(const std::string& path, const std::string& root) {
    return path == root || (path.size() > root.size() && path.compare(0, root.size(), root) == 0 && path[root.size()] == '/');
}

}  // namespace

Environment processEnvironment() {
    return [](const std::string& name) -> std::optional<std::string> {
        const char* value = std::getenv(name.c_str());
        if (!value) return std::nullopt;
        return std::string(value);
    };
}

std::string userHome(const Environment& environment) { return variable(environment, "HOME").value_or("/"); }

RootFamily familyOf(SourceKind source) {
    switch (source) {
        case SourceKind::Codex: return RootFamily::Codex;
        case SourceKind::Deepseek: return RootFamily::DeepSeek;
        case SourceKind::Gemini: return RootFamily::Gemini;
        case SourceKind::OpenCode: return RootFamily::OpenCode;
        case SourceKind::Omp: return RootFamily::Omp;
        case SourceKind::Antigravity: return RootFamily::Antigravity;
        case SourceKind::WorkBuddy: return RootFamily::WorkBuddy;
        case SourceKind::WorkBuddyAi: return RootFamily::WorkBuddyAi;
        case SourceKind::ZCode: return RootFamily::ZCode;
        case SourceKind::Kimi: return RootFamily::Kimi;
        case SourceKind::GrokBuild: return RootFamily::GrokBuild;
        case SourceKind::Pi: return RootFamily::Pi;
        case SourceKind::OpenClaw: return RootFamily::OpenClaw;
        case SourceKind::Hermes: return RootFamily::Hermes;
        case SourceKind::Droid: return RootFamily::Droid;
        default: return RootFamily::Claude;
    }
}

namespace paths {

std::string expandHome(const std::string& path, const std::string& home) {
    if (path == "~") return home;
    if (path.rfind("~/", 0) == 0 || path.rfind("~\\", 0) == 0) return fsx::join(home, path.substr(2));
    return path;
}

std::string normalizeRoot(const std::optional<std::string>& root) {
    if (!root || trimSpaces(*root).empty()) return {};
    std::string path = *root;
    if (path.front() != '/') {
        char buffer[4096];
        if (::getcwd(buffer, sizeof buffer)) path = fsx::join(buffer, path);
    }
    return fsx::normalize(path);
}

std::string ompDefaultRoot(const Environment& environment, const std::string& home) {
    if (auto agent = variable(environment, "PI_CODING_AGENT_DIR")) return fsx::join(*agent, "sessions");
    return fsx::join(ompConfigFolder(environment, home), "agent/sessions");
}

std::vector<std::string> ompRoots(const Environment& environment, const std::string& home) {
    const auto config = ompConfigFolder(environment, home);
    std::vector<std::string> roots{ompDefaultRoot(environment, home)};
    for (const auto& name : plainSubfolders(fsx::join(config, "profiles"))) {
        roots.push_back(config + "/profiles/" + name + "/agent/sessions");
    }
    return distinct(roots);
}

std::string piDefaultRoot(const Environment& environment, const std::string& home) {
    return fsx::join(piAgentFolder(environment, home), "sessions");
}

std::vector<std::string> piRoots(const Environment& environment, const std::string& home) {
    const auto agent = piAgentFolder(environment, home);
    std::vector<std::string> candidates{fsx::join(agent, "sessions")};
    if (auto custom = piSessionDirSetting(fsx::join(agent, "settings.json"))) candidates.push_back(expandHome(*custom, home));
    if (auto sessions = variable(environment, "PI_CODING_AGENT_SESSION_DIR")) candidates.push_back(expandHome(*sessions, home));
    std::set<std::string> seen;
    std::vector<std::string> roots;
    for (const auto& c : candidates) {
        auto full = normalizeRoot(c);
        if (!full.empty() && seen.insert(full).second) roots.push_back(full);
    }
    return roots;
}

std::vector<std::string> withoutPi(const std::vector<std::string>& omp, const std::vector<std::string>& pi) {
    std::vector<std::string> pis;
    for (const auto& p : pi) {
        if (auto t = fsx::trimTrailing(p); !t.empty()) pis.push_back(t);
    }
    std::vector<std::string> out;
    for (const auto& o : omp) {
        const auto t = fsx::trimTrailing(o);
        if (std::none_of(pis.begin(), pis.end(), [&](const std::string& p) { return isSameOrInside(t, p); })) out.push_back(o);
    }
    return out;
}

std::vector<std::string> openClawStateFolders(const Environment& environment, const std::string& home) {
    if (auto state = environment("OPENCLAW_STATE_DIR"); state && !trimSpaces(*state).empty()) {
        auto folder = normalizeRoot(expandHome(trimSpaces(*state), home));
        return folder.empty() ? std::vector<std::string>{} : std::vector<std::string>{folder};
    }
    std::string base = home;
    if (auto custom = environment("OPENCLAW_HOME"); custom && !trimSpaces(*custom).empty()) {
        base = expandHome(trimSpaces(*custom), home);
    }
    std::vector<std::string> candidates{fsx::join(base, ".openclaw")};
    static const std::regex profile("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$");
    std::vector<std::string> names;
    for (const auto& e : fsx::list(base)) {
        // Path.EnumerateDirectories follows links to folders.
        if (e.name.rfind(".openclaw-", 0) != 0 || !fsx::isDirectory(e.path)) continue;
        const auto rest = e.name.substr(10);
        if (std::regex_match(rest, profile) && !fsx::equalsNoCase(rest, "default")) names.push_back(e.name);
    }
    std::sort(names.begin(), names.end());
    for (const auto& n : names) candidates.push_back(fsx::join(base, n));
    std::set<std::string> seen;
    std::vector<std::string> out;
    for (const auto& c : candidates) {
        auto full = normalizeRoot(c);
        if (!full.empty() && seen.insert(full).second) out.push_back(full);
    }
    return out;
}

std::vector<std::string> openClawDatabases(const std::string& root) {
    static constexpr std::string_view kName = "openclaw-agent.sqlite";
    if (root.empty()) return {};
    if (fsx::isRegularFile(root)) return path_text::basename(root) == kName ? std::vector<std::string>{root} : std::vector<std::string>{};
    if (const auto direct = fsx::join(root, kName); fsx::isRegularFile(direct)) return {direct};
    std::vector<std::string> folders;
    for (const auto& e : fsx::list(fsx::join(root, "agents"))) {
        if (e.stat.directory && e.name.front() != '.') folders.push_back(e.path);
    }
    std::sort(folders.begin(), folders.end());
    std::vector<std::string> found;
    for (const auto& f : folders) {
        if (const auto database = f + "/agent/" + std::string(kName); fsx::isRegularFile(database)) found.push_back(database);
    }
    return found;
}

std::vector<std::string> openClawDatabases(const std::vector<std::string>& roots) {
    std::set<std::string> seen;
    std::vector<std::string> out;
    for (const auto& r : roots) {
        for (auto& d : openClawDatabases(r)) {
            if (seen.insert(d).second) out.push_back(std::move(d));
        }
    }
    return out;
}

std::vector<std::string> openClawCodexRoots(const std::vector<std::string>& databases) {
    std::set<std::string> seen;
    std::vector<std::string> out;
    for (const auto& d : databases) {
        auto root = fsx::join(fsx::parentOf(d), "codex-home/sessions");
        if (seen.insert(root).second) out.push_back(std::move(root));
    }
    return out;
}

bool openClawExists(const std::string& root) { return fsx::exists(root); }

std::string workBuddyProjectsOf(const std::string& dataOrProjects) {
    const auto trimmed = fsx::trimTrailing(dataOrProjects);
    return fsx::equalsNoCase(path_text::basename(trimmed), "projects") ? trimmed : fsx::join(trimmed, "projects");
}

std::string workBuddyDataFolderOf(const std::string& dataOrProjects) {
    const auto trimmed = fsx::trimTrailing(dataOrProjects);
    if (fsx::equalsNoCase(path_text::basename(trimmed), "projects")) {
        if (auto data = fsx::parentOf(trimmed); !data.empty()) return data;
    }
    return trimmed;
}

bool isAntigravityDatabaseName(std::string_view name) {
    if (name.size() != 39 || !fsx::equalsNoCase(name.substr(36), ".db")) return false;
    for (std::size_t i = 0; i < 36; ++i) {
        const char c = name[i];
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        const bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        if (dash ? c != '-' : !hex) return false;
    }
    return true;
}

std::string antigravityConversationsOf(const std::string& storeOrFolder) {
    return fsx::equalsNoCase(path_text::basename(storeOrFolder), "conversations") ? storeOrFolder
                                                                                    : fsx::join(storeOrFolder, "conversations");
}

bool isAntigravityBackupStore(const std::string& storeOrFolder) {
    const auto trimmed = fsx::trimTrailing(storeOrFolder);
    const auto store = fsx::equalsNoCase(path_text::basename(trimmed), "conversations") ? fsx::parentOf(trimmed) : trimmed;
    return fsx::endsWithNoCase(path_text::basename(store), "-backup");
}

std::vector<std::string> antigravityStores(const std::string& geminiFolder, const std::optional<std::string>& setting) {
    std::vector<std::string> candidates;
    if (setting && !setting->empty()) {
        candidates.push_back(*setting);
    } else {
        for (const char* name : {"antigravity-cli", "antigravity", "antigravity-ide"}) candidates.push_back(fsx::join(geminiFolder, name));
    }
    std::vector<std::string> out;
    for (auto& c : candidates) {
        auto trimmed = fsx::trimTrailing(c);
        if (!isAntigravityBackupStore(trimmed)) out.push_back(trimmed);
    }
    return out;
}

std::string antigravityDatabaseOf(const std::string& path) {
    return fsx::endsWithNoCase(path, ".db-wal") ? path.substr(0, path.size() - 4) : path;
}

std::string antigravityConfigOf(const std::string& conversations) {
    const auto store = fsx::parentOf(normalizeRoot(conversations));
    return fsx::join(fsx::parentOf(store), "config/config.json");
}

std::string geminiSettingsOf(const std::vector<std::string>& roots, const Environment& environment, const std::string& home) {
    if (roots.empty()) return fsx::join(environment("GEMINI_CLI_HOME").value_or(home), ".gemini/settings.json");
    const auto folder = normalizeRoot(roots.front());
    const bool isTmp = folder == "tmp" || (folder.size() > 4 && folder.compare(folder.size() - 4, 4, "/tmp") == 0);
    return fsx::join(isTmp ? fsx::parentOf(folder) : folder, "settings.json");
}

}  // namespace paths

std::string SourceLocations::defaultRoot(SourceKind source, const Environment& environment, const std::string& home) {
    const auto env = [&](const char* name) { return variable(environment, name); };
    switch (familyOf(source)) {
        case RootFamily::Codex: return fsx::join(env("CODEX_HOME").value_or(home + "/.codex"), "sessions");
        case RootFamily::Claude: return fsx::join(env("CLAUDE_CONFIG_DIR").value_or(home + "/.claude"), "projects");
        case RootFamily::DeepSeek: return fsx::join(env("DSH_HOME").value_or(home + "/.dsh"), "sessions");
        case RootFamily::OpenCode: return fsx::join(env("XDG_DATA_HOME").value_or(home + "/.local/share"), "opencode");
        case RootFamily::Gemini: return fsx::join(env("GEMINI_CLI_HOME").value_or(home), ".gemini/tmp");
        case RootFamily::Omp: return paths::ompDefaultRoot(environment, home);
        case RootFamily::Antigravity: return home + "/.gemini";
        case RootFamily::WorkBuddy: return home + "/.workbuddy";
        case RootFamily::WorkBuddyAi: return home + "/.workbuddy-ai";
        case RootFamily::ZCode: {
            auto base = environment("ZCODE_DATA_BASE_DIR");
            return fsx::join(base && !trimSpaces(*base).empty() ? *base : home, ".zcode/cli/db");
        }
        case RootFamily::Kimi: return fsx::join(env("KIMI_CODE_HOME").value_or(home + "/.kimi-code"), "sessions");
        case RootFamily::GrokBuild: return fsx::join(env("GROK_HOME").value_or(home + "/.grok"), "sessions");
        case RootFamily::Pi: return paths::piDefaultRoot(environment, home);
        case RootFamily::OpenClaw: {
            const auto folders = paths::openClawStateFolders(environment, home);
            return folders.empty() ? std::string() : folders.front();
        }
        case RootFamily::Hermes: return hermes::home(environment, false, home);
        case RootFamily::Droid: return droid::defaultRoot(home);
    }
    return {};
}

SourceLocations SourceLocations::resolve(const RootSettings& s, const Environment& environment, const std::string& home) {
    using paths::normalizeRoot;
    const auto automatic = [&](SourceKind source) { return defaultRoot(source, environment, home); };
    SourceLocations l;
    l.codexRoot = normalizeRoot(s.codex.value_or(automatic(SourceKind::Codex)));
    l.claudeRoot = normalizeRoot(s.claude.value_or(automatic(SourceKind::ClaudeCode)));
    l.dshRoot = normalizeRoot(s.dsh.value_or(automatic(SourceKind::Deepseek)));
    l.openCodeRoot = normalizeRoot(s.openCode.value_or(automatic(SourceKind::OpenCode)));
    l.geminiRoots = s.gemini ? normalizeAll({*s.gemini}) : normalizeAll({automatic(SourceKind::Gemini)});
    l.ompRoots = s.omp ? normalizeAll({*s.omp}) : normalizeAll(paths::ompRoots(environment, home));
    l.antigravityStores = normalizeAll(paths::antigravityStores(home + "/.gemini", s.antigravity));
    l.ompAutomatic = !s.omp;
    const auto workBuddy = [&](const std::optional<std::string>& setting, SourceKind source) {
        const auto folder = setting.value_or(automatic(source));
        return trimSpaces(folder).empty() ? std::string() : normalizeRoot(paths::workBuddyProjectsOf(folder));
    };
    l.workBuddyRoot = workBuddy(s.workBuddy, SourceKind::WorkBuddy);
    l.workBuddyAiRoot = workBuddy(s.workBuddyAi, SourceKind::WorkBuddyAi);
    l.zcodeRoot = normalizeRoot(s.zcode.value_or(automatic(SourceKind::ZCode)));
    l.kimiRoot = normalizeRoot(harness::sessionsOf(s.kimi.value_or(automatic(SourceKind::Kimi))));
    l.grokBuildRoot = normalizeRoot(harness::sessionsOf(s.grokBuild.value_or(automatic(SourceKind::GrokBuild))));
    l.piRoots = s.pi ? normalizeAll({*s.pi}) : normalizeAll(paths::piRoots(environment, home));
    l.piAutomatic = !s.pi;
    l.openClawRoots = s.openClaw ? normalizeAll({*s.openClaw}) : normalizeAll(paths::openClawStateFolders(environment, home));
    l.openClawAutomatic = !s.openClaw;
    l.codexAutomatic = !s.codex;
    l.hermesRoot = normalizeRoot(s.hermes.value_or(automatic(SourceKind::Hermes)));
    l.hermesChosen = s.hermes.has_value();
    l.droidRoot = s.droid && !trimSpaces(*s.droid).empty() ? normalizeRoot(droid::rootOf(normalizeRoot(*s.droid)))
                                                           : normalizeRoot(automatic(SourceKind::Droid));
    return l;
}

std::vector<std::string> SourceLocations::codexHomeRoots() const {
    if (!codexAutomatic) return {};
    std::vector<std::string> out;
    const auto main = fsx::trimTrailing(codexRoot);
    for (const auto& root : distinct(normalizeAll(paths::openClawCodexRoots(paths::openClawDatabases(openClawRoots))))) {
        if (fsx::trimTrailing(root) != main) out.push_back(root);
    }
    return out;
}

std::vector<std::string> SourceLocations::hermesFolders() const {
    if (hermesRoot.empty()) return {};
    return normalizeAll(hermesChosen ? hermes::settingFolders(hermesRoot) : hermes::logFolders(hermesRoot));
}

std::vector<std::pair<RootFamily, std::string>> SourceLocations::fileRoots() const {
    std::vector<std::pair<RootFamily, std::string>> roots;
    const auto pis = distinct(piRoots);
    roots.emplace_back(RootFamily::Codex, codexRoot);
    for (const auto& home : codexHomeRoots()) roots.emplace_back(RootFamily::Codex, home);
    roots.emplace_back(RootFamily::Claude, claudeRoot);
    roots.emplace_back(RootFamily::DeepSeek, dshRoot);
    for (const auto& r : geminiRoots) roots.emplace_back(RootFamily::Gemini, r);
    for (const auto& r : distinct(paths::withoutPi(ompRoots, pis))) roots.emplace_back(RootFamily::Omp, r);
    std::vector<std::string> conversations;
    for (const auto& store : antigravityStores) conversations.push_back(paths::antigravityConversationsOf(store));
    for (const auto& r : distinct(conversations)) roots.emplace_back(RootFamily::Antigravity, r);
    if (!workBuddyRoot.empty()) roots.emplace_back(RootFamily::WorkBuddy, workBuddyRoot);
    if (!workBuddyAiRoot.empty()) roots.emplace_back(RootFamily::WorkBuddyAi, workBuddyAiRoot);
    if (!kimiRoot.empty()) roots.emplace_back(RootFamily::Kimi, kimiRoot);
    if (!grokBuildRoot.empty()) roots.emplace_back(RootFamily::GrokBuild, grokBuildRoot);
    for (const auto& r : pis) roots.emplace_back(RootFamily::Pi, r);
    if (!droidRoot.empty()) roots.emplace_back(RootFamily::Droid, droidRoot);
    return roots;
}

}  // namespace agentspeed
