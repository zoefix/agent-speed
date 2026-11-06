#pragma once

#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"

// The hosts an orb or board tracks (DESIGN 13 `hosts`): every host, including ones added later, or an explicit list of
// host ids (`local` for this computer, remote host ids). Ids this computer does not know are kept and ignored, like
// unknown sources.
namespace agentspeed::hosts {

// A host an orb or board can track: this computer (`local`) or a remote host of the settings, with its display name.
struct HostChoice {
    std::string id, name;
    bool operator==(const HostChoice&) const = default;
};

// One row of a Hosts picker or submenu; `enabled` is false for the last tracked host (`remote.hosts.lastOne`).
struct HostEntry {
    std::string id, name;
    bool checked = false, enabled = true;
    bool operator==(const HostEntry&) const = default;
};

class HostSelection {
public:
    // Every host.
    HostSelection() = default;
    static HostSelection all() { return {}; }
    // `local` first, duplicates and empty ids dropped; empty → all.
    static HostSelection normalized(const std::vector<std::string>& ids);
    // `"all"`, one id as a string, or an array of ids; anything else is all.
    static HostSelection fromJson(const json::Value& value);
    // `"all"` or the array of ids.
    std::string json() const;

    bool isAll() const { return ids_.empty(); }
    // The ids of an explicit list; empty for all.
    const std::vector<std::string>& ids() const { return ids_; }
    // The engine's host set; nullopt for all.
    std::optional<std::vector<std::string>> hostSet() const;
    bool tracks(std::string_view hostId) const;

    // The user toggles `hostId`: on an all-hosts selection it becomes that single host; otherwise the host is added or
    // removed. Removing the last host is refused (nullopt).
    std::optional<HostSelection> toggling(const std::string& hostId) const;
    // The pickers' rule over the hosts this computer knows (`known`, in settings order): the result is in settings
    // order; removing the last known host is refused, unknown ids alone never count as a selection.
    std::optional<HostSelection> toggling(const std::string& hostId, const std::vector<std::string>& known) const;
    // A host removed from the settings leaves the selection; an emptied selection becomes all (REMOTE 2.4).
    HostSelection removing(const std::string& hostId) const;
    // The ids in `order` (`local`, then the remote hosts as added); ids it does not know stay, after the known ones.
    HostSelection ordered(const std::vector<std::string>& order) const;
    // Every choice in its order, checked when an explicit selection tracks it; the last tracked one cannot be unchecked.
    std::vector<HostEntry> entries(const std::vector<HostChoice>& choices) const;

    bool operator==(const HostSelection&) const = default;

private:
    std::vector<std::string> ids_;
};

// The pickers exist only while a remote host is configured (DESIGN 13: invisible until the first host is added).
bool pickerShown(const std::vector<HostChoice>& choices);

// The hosts' part of an automatic orb name (DESIGN 13): ` · <host>` for one host, ` · <first> +N` for several, in the
// order of `choices`. Nullopt for every host, for ids this computer does not know and while no remote host exists.
struct NameSuffix {
    std::string first;
    std::size_t others = 0;
    bool operator==(const NameSuffix&) const = default;
};
std::optional<NameSuffix> nameSuffix(const HostSelection& selection, const std::vector<HostChoice>& choices);

}  // namespace agentspeed::hosts
