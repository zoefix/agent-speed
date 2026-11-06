#pragma once

#include <cstdint>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/remote_ssh.hpp"

// The relay executables the app ships (REMOTE 3.1): `agent-speed-relay-linux-<arch>` in the app's relay folder, with
// their SHA-256 computed from the files and cached by path, size and modification time. Thread-safe.
namespace agentspeed::remote {

struct RelayExecutable {
    std::string path;
    Arch arch = Arch::X86_64;
    std::string sha256;  // lowercase hex
    std::int64_t size = 0;
    std::string installedName;  // REMOTE 4.2
    bool operator==(const RelayExecutable&) const = default;
};

class RelayBundle {
public:
    static constexpr std::string_view kOverrideVariable = "AGENT_SPEED_RELAY_DIR";

    RelayBundle(std::optional<std::string> directory, std::string version) : directory_(std::move(directory)), version_(std::move(version)) {}

    // The folder the app's relays are in: AGENT_SPEED_RELAY_DIR when set, else `<prefix>/share/agent-speed/relay` beside
    // the executable's `bin` (AppImage `usr/`, the .deb's /opt/agent-speed or /usr), else `relay/` next to it (a
    // development build); nullopt when none holds a relay.
    static std::optional<std::string> locate(const std::vector<std::string>& environment, const std::string& executablePath);

    const std::optional<std::string>& directory() const { return directory_; }
    const std::string& version() const { return version_; }
    // Nullopt when the file is missing, empty or unreadable.
    std::optional<RelayExecutable> executable(Arch arch) const;

private:
    std::optional<std::string> directory_;
    std::string version_;
    mutable std::mutex mutex_;
    struct Cached {
        std::int64_t size = 0, modifiedNs = 0;
        RelayExecutable executable;
    };
    mutable std::map<Arch, Cached> cache_;
};

// Lowercase hex SHA-256 of a file, read in chunks; nullopt when it cannot be read.
std::optional<std::string> fileSha256(const std::string& path);

}  // namespace agentspeed::remote
