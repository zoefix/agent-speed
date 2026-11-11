#pragma once

#include <functional>
#include <optional>
#include <string>
#include <string_view>

// REMOTE 11.4 Proxy: `https_proxy`, else `HTTPS_PROXY`, from the SSH command's environment, an `http://host:port` proxy
// asked with `CONNECT`, unless `no_proxy` (else `NO_PROXY`) lists the host. The loopback route never uses one.
namespace agentspeed::relay::net {

using Variable = std::function<std::optional<std::string>(std::string_view)>;

struct ProxyRoute {
    enum class Kind : unsigned char { Direct, Proxy, Unusable };
    Kind kind = Kind::Direct;
    std::string host;
    int port = 0;
};

// `http://host[:port][/]` or `host[:port]` (port 1080 when none is given, as curl does). User info, another scheme or a
// malformed value is Unusable: the request then fails instead of leaving the user's proxy out.
ProxyRoute proxyFrom(std::string_view value);
// curl's rules: comma-separated names, `*` for every host, a name matches itself and its subdomains (a leading `.` or
// `*.` is the same), `name:port` only that port; case-insensitive.
bool bypassesProxy(std::string_view noProxy, std::string_view host, int port);
// The route of a request to `host:port`.
ProxyRoute proxyFor(std::string_view host, int port, const Variable& variable);

// `CONNECT host:port HTTP/1.1` with `Host` and `User-Agent`.
std::string connectRequest(std::string_view host, int port, std::string_view userAgent);

}  // namespace agentspeed::relay::net
