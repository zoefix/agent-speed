#pragma once

#include <optional>
#include <string>
#include <string_view>

// The URLs of the quota requests (QUOTAS 9, REMOTE 11.4): `http(s)://<host>[:port]/<target>`, nothing else.
namespace agentspeed::relay::net {

struct Url {
    bool tls = true;
    std::string host;  // lower case
    int port = 443;
    std::string target;  // path and query, starting with `/`
};

// No user info, fragment, IPv6 literal or space; a host of letters, digits, `.` and `-`; a port of 1 … 65535.
std::optional<Url> parseUrl(std::string_view url);

}  // namespace agentspeed::relay::net
