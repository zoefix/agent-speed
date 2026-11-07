#pragma once

#include <cstddef>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// The local network feed's settings document (LAN 2.1): the server, the targets and the instance id.
namespace agentspeed::lan {

inline constexpr int kDefaultPort = 7247;
inline constexpr int kMinPort = 1024;
inline constexpr int kMaxPort = 65535;
inline constexpr std::size_t kMaxTargets = 8;
inline constexpr std::size_t kKeyLength = 12;
inline constexpr std::size_t kTargetNameLimit = 64;
inline constexpr std::size_t kTargetKeyLimit = 128;
inline constexpr std::string_view kKeyAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

enum class TargetKind { Udp, Http };
std::string_view kindName(TargetKind kind);
std::optional<TargetKind> kindFromName(std::string_view name);

struct Target {
    std::string id;
    std::string name;  // empty: the address is shown instead
    TargetKind kind = TargetKind::Udp;
    std::string address;  // as typed, trimmed
    std::optional<std::string> key;
    bool enabled = true;
    bool operator==(const Target&) const = default;
};

struct ServerSettings {
    bool enabled = false;
    int port = kDefaultPort;
    std::optional<std::string> key;  // nullopt until the server is first turned on
    bool operator==(const ServerSettings&) const = default;
};

struct Settings {
    std::optional<std::string> instance;  // nullopt until the document is first written
    ServerSettings server;
    std::vector<Target> targets;
    bool operator==(const Settings&) const = default;

    // The normalized document: another version or an unreadable text reads as the defaults, invalid members as theirs,
    // invalid targets are left out.
    static Settings parse(std::string_view json);
    // Every member in the order of LAN 2.1, `null` for a missing instance or key.
    std::string json() const;
};

bool validKey(std::string_view key);
// A lowercase version 4 UUID.
bool validUuid(std::string_view id);
// From the system's cryptographic random generator.
std::string newKey();
std::string newUuid();
std::string newRunId();
// K7Q4-M2X9-PT3D.
std::string displayKey(std::string_view key);
// Names and target keys: trimmed, `minimum`–`limit` code points, no control character; nullopt otherwise.
std::optional<std::string> cleanText(std::string_view text, std::size_t minimum, std::size_t limit);

}  // namespace agentspeed::lan
