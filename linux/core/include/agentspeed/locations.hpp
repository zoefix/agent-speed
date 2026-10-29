#pragma once

#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/model.hpp"

// METRICS 2: where every source keeps its data, from the environment, the home folder and the user's settings.
namespace agentspeed {

// Reads one environment variable; nullopt or empty means unset.
using Environment = std::function<std::optional<std::string>(const std::string&)>;
Environment processEnvironment();
std::string userHome(const Environment& environment);

enum class RootFamily {
    Codex,
    Claude,
    DeepSeek,
    Gemini,
    Omp,
    Antigravity,
    WorkBuddy,
    WorkBuddyAi,
    OpenCode,
    ZCode,
    Kimi,
    GrokBuild,
    Pi,
    OpenClaw,
    Hermes,
    Droid,
};

RootFamily familyOf(SourceKind source);

namespace paths {

std::string expandHome(const std::string& path, const std::string& home);
// Blank → empty; relative → against the working folder; `.`/`..` resolved, no trailing separator.
std::string normalizeRoot(const std::optional<std::string>& root);

std::vector<std::string> ompRoots(const Environment& environment, const std::string& home);
std::string ompDefaultRoot(const Environment& environment, const std::string& home);
std::vector<std::string> piRoots(const Environment& environment, const std::string& home);
std::string piDefaultRoot(const Environment& environment, const std::string& home);
// omp roots that are neither a Pi root nor inside one (METRICS 4.7).
std::vector<std::string> withoutPi(const std::vector<std::string>& omp, const std::vector<std::string>& pi);

std::vector<std::string> openClawStateFolders(const Environment& environment, const std::string& home);
std::vector<std::string> openClawDatabases(const std::string& root);
std::vector<std::string> openClawDatabases(const std::vector<std::string>& roots);
std::vector<std::string> openClawCodexRoots(const std::vector<std::string>& databases);
bool openClawExists(const std::string& root);

std::string workBuddyProjectsOf(const std::string& dataOrProjects);
std::string workBuddyDataFolderOf(const std::string& dataOrProjects);

std::vector<std::string> antigravityStores(const std::string& geminiFolder, const std::optional<std::string>& setting);
std::string antigravityConversationsOf(const std::string& storeOrFolder);
bool isAntigravityBackupStore(const std::string& storeOrFolder);
bool isAntigravityDatabaseName(std::string_view name);
std::string antigravityDatabaseOf(const std::string& path);

}  // namespace paths

// The user's folder settings; nullopt is the automatic root.
struct RootSettings {
    std::optional<std::string> codex, claude, dsh, openCode, gemini, omp, antigravity, workBuddy, workBuddyAi, zcode, kimi,
        grokBuild, pi, openClaw, hermes, droid;
};

struct SourceLocations {
    std::string codexRoot, claudeRoot, dshRoot, openCodeRoot;
    std::vector<std::string> geminiRoots, ompRoots, antigravityStores;
    bool ompAutomatic = false;
    std::string workBuddyRoot, workBuddyAiRoot, zcodeRoot, kimiRoot, grokBuildRoot;
    std::vector<std::string> piRoots;
    bool piAutomatic = false;
    std::vector<std::string> openClawRoots;
    bool openClawAutomatic = false;
    bool codexAutomatic = true;
    std::string hermesRoot;
    bool hermesChosen = false;
    std::string droidRoot;

    static SourceLocations resolve(const RootSettings& settings, const Environment& environment, const std::string& home);
    // The automatic roots shown in settings, one per source.
    static std::string defaultRoot(SourceKind source, const Environment& environment, const std::string& home);

    // Transcript roots in family order, each path once per family; an omp root under a Pi root is Pi's.
    std::vector<std::pair<RootFamily, std::string>> fileRoots() const;
    // OpenClaw's agent-scoped Codex homes while no Codex folder is chosen.
    std::vector<std::string> codexHomeRoots() const;
    std::vector<std::string> hermesFolders() const;
};

}  // namespace agentspeed
