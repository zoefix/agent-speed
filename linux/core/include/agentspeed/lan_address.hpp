#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <variant>
#include <vector>

#include "agentspeed/lan_settings.hpp"

// Local addresses (LAN 2.3) and target addresses (LAN 5.1).
namespace agentspeed::lan {

// An address in one of the local ranges; a zone (`%en0`) is ignored. `udp` also accepts 255.255.255.255.
bool isLocal(std::string_view address, bool udp = false);
// The same for raw bytes: 4 for IPv4, 16 for IPv6.
bool isLocalIPv4(const std::array<std::uint8_t, 4>& ip, bool udp = false);
bool isLocalIPv6(const std::array<std::uint8_t, 16>& ip, bool udp = false);

struct TargetAddress {
    std::string host;  // an IPv4 or IPv6 literal (without brackets) or a name
    bool literal = false;
    int port = 0;
    std::string hostHeader;  // HTTP: the host and port as written
    std::string target;      // HTTP: the path and query, `/` by default
};

// A target address as written, or the reason it is invalid (`empty`, `spaces`, `port`, `host`, `https`, `scheme`,
// `fragment`, `userinfo`).
std::variant<TargetAddress, std::string> parseTarget(TargetKind kind, std::string_view address);
bool validTarget(TargetKind kind, std::string_view address);

// DESIGN 14: a UDP host that is the limited broadcast or an IPv4 address ending in .255.
bool isBroadcast(const TargetAddress& address);

enum class Resolution { Ok, NotFound, NotLocal };
// What the resolved addresses allow: every one local, else nothing is sent.
Resolution resolveCheck(const std::vector<std::string>& addresses, bool udp);

}  // namespace agentspeed::lan
