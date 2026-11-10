#pragma once

#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/devin.hpp"
#include "agentspeed/hermes.hpp"
#include "agentspeed/location_lists.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/qoder.hpp"
#include "agentspeed/root_family.hpp"

// METRICS 2: where every source keeps its data, from the environment, the home folder and the user's settings.
namespace agentspeed {

// Reads one environment variable; nullopt or empty means unset.
using Environment = std::function<std::optional<std::string>(const std::string&)>;
Environment processEnvironment();
std::string userHome(const Environment& environment);
class TimeZone;
// This computer's zone: $TZ, else /etc/localtime, else UTC.
std::shared_ptr<const TimeZone> localZone(const Environment& environment);

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
// `config/config.json` in the folder that holds an Antigravity store (EXECUTION-MODES, Configured values).
std::string antigravityConfigOf(const std::string& conversations);
// Gemini CLI's user settings: next to the `tmp` folder of a root, else `<home>/.gemini/settings.json`, where `<home>` is
// $GEMINI_CLI_HOME or the user's home (METRICS 2).
std::string geminiSettingsOf(const std::vector<std::string>& roots, const Environment& environment, const std::string& home);

}  // namespace paths

// The root a user location names (METRICS 2, last column): an Antigravity store's `conversations`, a WorkBuddy data
// folder's `projects`, a Kimi Code CLI or Grok Build data directory's `sessions`, a `.factory` folder's `sessions`, a
// Qoder CLI config folder's `projects`; any other location is the root itself.
std::string userRoot(const std::string& path, RootFamily family);

// A Hermes Agent root (METRICS 2, 4.15): the automatic home, or a user location (a home or one `logs` folder).
using HermesRoot = hermes::Root;

// What the location lists read now (METRICS 2.1): per source the union of its entries' roots, in list order, each once.
struct SourceLocations {
    std::vector<std::string> codexRoots, claudeRoots, dshRoots, geminiRoots, ompRoots, antigravityStores, workBuddyRoots,
        workBuddyAiRoots, kimiRoots, grokBuildRoots, piRoots, droidRoots, qoderCliRoots;
    // One root per database: a root naming the database file of an earlier root is left out.
    std::vector<std::string> openCodeRoots, zcodeRoots;
    // OpenClaw's roots as resolved (the state folders found, then the user locations), and the user locations alone.
    std::vector<std::string> openClawRoots, openClawUserRoots;
    std::vector<HermesRoot> hermesRoots;
    // Devin's roots (METRICS 4.17): the automatic entry's database, then the user locations, each named once.
    std::vector<devin::Root> devinRoots;
    // Qoder IDE's roots (METRICS 4.18): each edition's database of the automatic entry, then the user locations.
    std::vector<qoder::Root> qoderRoots;
    // Gemini CLI's `settings.json` of each root (EXECUTION-MODES, Configured values).
    std::map<std::string, std::string> geminiSettings;
    // The list holds the automatic entry, so roots it finds later (omp profiles, Pi's custom session folder, OpenClaw's
    // state folders and agent-scoped Codex homes) are read too.
    bool codexAutomatic = true, ompAutomatic = true, piAutomatic = true, openClawAutomatic = true;

    static SourceLocations resolve(const LocationLists& lists, const Environment& environment, const std::string& home);
    // The default location as Settings names it (WorkBuddy: the data folder; OpenCode, ZCode, Devin, Qoder IDE: the
    // database's folder).
    static std::string defaultRoot(SourceKind source, const Environment& environment, const std::string& home);

    // Transcript roots in family order and list order, each path once per family; an omp root under a Pi root is Pi's.
    // Codex's automatic entry adds the agent-scoped Codex home of every OpenClaw agent database found now.
    std::vector<std::pair<RootFamily, std::string>> fileRoots() const;
    // OpenClaw's roots now: the state folders found while the list holds the automatic entry, then the user locations.
    std::vector<std::string> openClawRootsNow(const Environment& environment, const std::string& home) const;
    std::vector<std::string> hermesFolders() const;
    // Both read the same roots and databases with the same settings files: switching needs no new reading.
    bool readsSame(const SourceLocations& other) const;
};

// The automatic entry's roots of a list (METRICS 2.1 Entries); Codex's include OpenClaw's agent-scoped Codex homes.
std::vector<std::string> automaticRoots(RootFamily list, const LocationLists& lists, const Environment& environment,
                                        const std::string& home);
// The keys of what a list's automatic entry reads now (Editing 3 and 4).
AutomaticKeys automaticKeys(RootFamily list, const LocationLists& lists, const Environment& environment, const std::string& home);

}  // namespace agentspeed
