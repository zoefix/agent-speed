#pragma once

#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/locations.hpp"

// DESIGN 5 Entry row: what one entry of a location list names, whether it exists and what Show in Files opens.
namespace agentspeed {

struct LocationEntry {
    DataLocation location;
    // The paths the row names (the automatic entry may name several), each with whether it is there to read.
    std::vector<std::pair<std::string, bool>> shown;
    // Something the entry reads exists; a missing entry is dimmed with `source.folderMissing`.
    bool exists = false;
    // The first readable location the entry reads, else the first existing one.
    std::optional<std::string> reveal;
    // A database file rather than a folder.
    bool isFile = false;
};

// Every default location of a list in reading order: what its automatic entry reads.
std::vector<std::string> defaultLocations(RootFamily list, const Environment& environment, const std::string& home);
// What the automatic entry names: omp and Pi their default root only, Antigravity the stores found (all three when none
// is), OpenClaw the state folders found (`~/.openclaw` when none is), every other list all of its defaults.
std::vector<std::string> automaticShown(RootFamily list, const Environment& environment, const std::string& home);
// An existing folder the list reads (never an Antigravity `*-backup` store); a database file for the database lists.
bool isReadableLocation(RootFamily list, const std::string& path);

LocationEntry locationEntry(RootFamily list, const DataLocation& location, const Environment& environment, const std::string& home);
std::vector<LocationEntry> locationEntries(RootFamily list, const LocationLists& lists, const Environment& environment,
                                           const std::string& home);
// `~/.codex/sessions` for a path in the home folder; any other path as it is.
std::string abbreviatedPath(const std::string& path, const std::string& home);

}  // namespace agentspeed
