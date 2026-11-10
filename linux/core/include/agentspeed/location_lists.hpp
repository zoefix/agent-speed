#pragma once

#include <cstddef>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/root_family.hpp"

// METRICS 2.1: the location list of every settings group — the automatic entry and the user's locations — with its
// stored form (DESIGN 5 Storage) and the editing rules.
namespace agentspeed {

// One entry: the automatic roots of METRICS 2, or a user location (an absolute, normalized path).
struct DataLocation {
    bool automatic = false;
    std::string path;

    static DataLocation automaticEntry() { return {true, {}}; }
    static DataLocation at(std::string path) { return {false, std::move(path)}; }
    bool operator==(const DataLocation&) const = default;
};

using LocationList = std::vector<DataLocation>;

// The nineteen lists in settings order, and their stored names (`codex`, `claude`, … `qoderCli`).
const std::vector<RootFamily>& locationListOrder();
std::string_view listName(RootFamily list);
std::optional<RootFamily> listNamed(std::string_view name);

// One folder per source, the single setting of earlier versions; nullopt reads the automatic roots.
struct RootSettings {
    std::optional<std::string> codex, claude, dsh, openCode, gemini, omp, antigravity, workBuddy, workBuddyAi, zcode, kimi,
        grokBuild, pi, openClaw, hermes, droid, devin, qoder, qoderCli;
};

enum class AddOutcome { Added, RestoredAutomatic, AlreadyListed, AlreadyAutomatic, Full, Invalid };

inline bool changedList(AddOutcome outcome) { return outcome == AddOutcome::Added || outcome == AddOutcome::RestoredAutomatic; }

// What a list's automatic entry reads now, as location keys; the first is the default location's.
struct AutomaticKeys {
    std::vector<std::string> keys;
};

class LocationLists {
public:
    static constexpr std::size_t kMaxEntries = 8;
    static constexpr int kDocumentVersion = 1;

    LocationLists() = default;
    // A chosen folder X of an earlier version becomes the list [X], without the automatic entry (Migration).
    LocationLists(const RootSettings& single);  // NOLINT(google-explicit-constructor): fixtures name one folder per source
    static LocationLists fromLegacy(const std::map<RootFamily, std::string>& paths);

    // [automatic] for a list never edited.
    const LocationList& operator[](RootFamily list) const;
    void set(RootFamily list, const LocationList& entries);
    bool hasAutomatic(RootFamily list) const;
    bool isAutomaticOnly() const { return lists_.empty(); }
    bool operator==(const LocationLists& other) const { return lists_ == other.lists_; }

    // Automatic first, each entry once, paths absolute and normalized, at most kMaxEntries; empty reads [automatic].
    static LocationList normalized(const LocationList& list);
    // An absolute path without `.`/`..` segments and trailing separator; nullopt for a blank or relative one.
    static std::optional<std::string> standardized(std::string_view path);

    // `{"lists":{"codex":[{"automatic":true},{"path":"/abs"}]},"version":1}`: keys sorted, automatic-only lists left
    // out, lists of unknown names kept as stored.
    std::string document() const;
    // nullopt when the text is not a JSON object; otherwise invalid entries are skipped and each list normalized.
    static std::optional<LocationLists> fromDocument(std::string_view text);

    bool canAdd(RootFamily list) const { return (*this)[list].size() < kMaxEntries; }
    // The only remaining entry stays: switching a source off is its toggle's job.
    bool canRemove(RootFamily list) const { return (*this)[list].size() > 1; }
    bool canReset(RootFamily list) const { return !isAutomaticOnlyList(list); }
    AddOutcome add(std::string_view path, RootFamily list, const AutomaticKeys& automatic);
    void remove(RootFamily list, const DataLocation& entry);
    void reset(RootFamily list) { lists_.erase(list); }

private:
    bool isAutomaticOnlyList(RootFamily list) const { return !lists_.contains(list); }

    std::map<RootFamily, LocationList> lists_;
    // Name → the list's JSON text, for names this build does not know.
    std::map<std::string, std::string> unknown_;
};

// The root or database a location names, resolved: two locations are the same when their keys are equal (Editing 2).
std::string locationKey(const std::string& path, RootFamily list);
// The real path of an existing file or folder (symbolic links resolved), else the normalized path.
std::string resolvedPath(const std::string& path);
// omp, Antigravity, Pi and OpenClaw: one chosen folder is narrower than the automatic set (Editing 4).
bool automaticIsWiderThanDefault(RootFamily list);
// OpenClaw's picker also accepts `openclaw-agent.sqlite` (DESIGN 5).
bool acceptsDatabaseFile(RootFamily list);

}  // namespace agentspeed
