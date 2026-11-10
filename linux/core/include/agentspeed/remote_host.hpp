#pragma once

#include <cstddef>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// The remote hosts of the settings (REMOTE 2.1): one JSON value under the key `remoteHosts`. Unknown members of a
// newer version are kept verbatim, so saving never drops them.
namespace agentspeed::remote {

// Why a `destination` or `relayFolder` is refused (the editor shows it under the field).
enum class FieldIssue { Empty, TooLong, LeadingDash, ForbiddenCharacter, NotAbsolute, UnsafeComponent };
std::string_view fieldIssueName(FieldIssue issue);

namespace validation {
inline constexpr std::size_t kDestinationLimit = 255;
inline constexpr std::size_t kFolderLimit = 200;
inline constexpr std::size_t kNameLimit = 32;

std::optional<FieldIssue> destinationIssue(std::string_view destination);
std::optional<FieldIssue> folderIssue(std::string_view folder);
// `[A-Za-z0-9._-]`: relay file names (REMOTE 4.1).
bool isNameByte(unsigned char byte);
// A valid folder without its trailing slashes (`~/` stays `~`).
std::string normalizedFolder(std::string_view folder);
// An upper-case UUID string (8-4-4-4-12 hex digits), never `local`.
bool isValidId(std::string_view id);
// Trimmed, control characters removed, at most 32 characters; nullopt when empty (automatic).
std::optional<std::string> cleanName(const std::optional<std::string>& name);
// The host part of a destination: `ssh://` removed, then up to the last `@`, then a port or IPv6 brackets.
std::string automaticName(std::string_view destination);
// A new random upper-case UUID.
std::string newId();
}  // namespace validation

struct Host {
    static constexpr std::string_view kDefaultRelayFolder = "~/.cache/agent-speed";

    std::string id;
    std::string destination;
    std::optional<std::string> name;  // nullopt: automatic
    bool enabled = true;
    std::optional<std::string> relayFolder;  // nullopt: ~/.cache/agent-speed
    // The host's Claude Code status line bridge (REMOTE 11.3); absent = false, written only while on.
    bool quotaClaudeCli = false;
    // Members of a newer version: name → exact JSON text.
    std::vector<std::pair<std::string, std::string>> extra;

    // A host with a cleaned name and an empty folder treated as the default.
    static Host make(std::string id, std::string destination, std::optional<std::string> name = std::nullopt, bool enabled = true,
                     std::optional<std::string> relayFolder = std::nullopt);

    // As inserted (unquoted) into the remote commands.
    std::string effectiveRelayFolder() const;
    // Both `destination` and `relayFolder` pass REMOTE 2.1; otherwise the host shows `invalidDestination`.
    bool connectable() const;
    std::string automaticName() const { return validation::automaticName(destination); }
    // What reconnects a running connection when it changes: everything but the name and the switches.
    std::pair<std::string, std::string> connectionIdentity() const { return {destination, effectiveRelayFolder()}; }
    bool operator==(const Host&) const = default;
};

// At most 8 hosts in the order added. Decoding is tolerant: a host without a valid id is skipped, a repeated id keeps
// the first, more than 8 are cut; invalid destinations are kept (they disable the host).
class HostsDocument {
public:
    static constexpr std::size_t kMaxHosts = 8;
    static constexpr int kVersion = 1;

    HostsDocument() = default;
    explicit HostsDocument(std::vector<Host> hosts);

    // Nullopt when the text is not a document; the caller then starts from an empty one.
    static std::optional<HostsDocument> parse(std::string_view json);
    // Stable compact JSON (members sorted by name, like the macOS settings).
    std::string json() const;

    const std::vector<Host>& hosts() const { return hosts_; }
    int version() const { return version_; }
    bool canAdd() const { return hosts_.size() < kMaxHosts; }
    const Host* find(std::string_view id) const;
    // False at the limit or when the id is invalid or taken.
    bool add(Host host);
    // Replaces the host with the same id in place, keeping its unknown members unless `host` has some.
    bool update(Host host);
    void remove(std::string_view id);
    // Every host's display name: its custom name, else its automatic name made distinct (` (2)`, ` (3)` …) from every
    // custom name and every earlier host's name, compared without case.
    std::map<std::string, std::string> displayNames() const;
    std::optional<std::string> displayName(std::string_view id) const;

    static std::vector<Host> normalized(std::vector<Host> hosts);
    bool operator==(const HostsDocument&) const = default;

private:
    int version_ = kVersion;
    std::vector<Host> hosts_;
    std::vector<std::pair<std::string, std::string>> extra_;
};

}  // namespace agentspeed::remote
