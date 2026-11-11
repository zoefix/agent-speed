#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/quota/http.hpp"
#include "net/proxy.hpp"

// The relay's HTTPS client for the quota requests (REMOTE 11.4): HTTP/1.1 over mbedTLS 3 to the hosts of NETWORK 9,
// through the SSH command's `https_proxy`; redirects refused, 10 s, 256 KiB. Antigravity's loopback route goes to
// 127.0.0.1 only, without a proxy, within 2 s, taking a self-signed certificate.
namespace agentspeed::relay::net {

struct ClientOptions {
    // `AgentSpeed/<version> (Linux)` unless the request names its own.
    std::string userAgent;
    std::vector<std::string> caBundles;  // empty: defaultCaBundles()
    // The SSH command's environment (https_proxy, no_proxy); null reads none.
    Variable variable;
    int timeoutMs = quota::http::kTimeoutMs;
    int loopbackTimeoutMs = quota::http::kLoopbackTimeoutMs;
    std::size_t bodyCap = quota::http::kBodyCap;
    std::function<std::int64_t()> clock;  // Unix ms; null: the system clock
    // Tests: the address a direct request to `host:port` connects to instead (the TLS name stays `host`).
    std::function<std::optional<std::pair<std::string, int>>(const std::string& host, int port)> connectTo;
};

// One request, start to end, on the calling thread. A byte on `cancelFd` (when ≥ 0) ends it as a network failure.
quota::http::Response fetch(const quota::http::Request& request, const ClientOptions& options, int cancelFd = -1);

}  // namespace agentspeed::relay::net
