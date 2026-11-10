#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// QUOTAS 9.1 Network rules and NETWORK 9: one vendor request as the quota routes build it, and the transport that
// sends it (the app's Qt Network, later the relay's mbedTLS).
namespace agentspeed::quota::http {

enum class Method : std::uint8_t { Get, Post };

struct Header {
    std::string name;
    std::string value;
    bool operator==(const Header&) const = default;
};

struct Request {
    Method method = Method::Get;
    // https://<host>/<path>; the loopback route http:// or https://127.0.0.1:<port>/<path>.
    std::string url;
    // Exactly the headers QUOTAS 9 lists for the route. The transport adds `User-Agent: AgentSpeed/<version> (Linux)`
    // unless one is given (WorkBuddy sends the browser's, 9.9), and nothing else.
    std::vector<Header> headers;
    std::string body;
    // Antigravity's language server: 127.0.0.1 only, no proxy, the loopback timeout, a self-signed certificate accepted.
    bool loopback = false;
    bool operator==(const Request&) const = default;
};

enum class Failure : std::uint8_t {
    None,
    Network,   // no connection, a TLS error, a reset
    Timeout,
    TooLarge,  // the body passed kBodyCap
    Redirect,  // a 3xx answer: never followed
    Refused,   // the request broke a rule (scheme, host) and was not sent
    NoCertificates,  // a remote host has no CA certificates (REMOTE 11.4): the status says so
};

struct Response {
    Failure failure = Failure::None;
    int status = 0;
    std::optional<std::int64_t> dateMs;  // the `Date` header (RFC 7231)
    std::int64_t receivedMs = 0;
    std::string body;
};

using Completion = std::function<void(Response)>;

class Transport {
public:
    virtual ~Transport() = default;
    // Sends the request; `done` runs exactly once, on the thread that owns the transport.
    virtual void send(Request request, Completion done) = 0;
};

constexpr std::size_t kBodyCap = 256 * 1024;
constexpr int kTimeoutMs = 10'000;
constexpr int kLoopbackTimeoutMs = 2'000;

// NETWORK 9's hosts: the only ones a non-loopback quota request may go to.
bool allowedHost(std::string_view host);

// The host of an https:// URL, or of http(s)://127.0.0.1:<port> for loopback; nullopt for anything else.
std::optional<std::string> hostOf(std::string_view url);

// True when the request may be sent: https to an allowed host, or (loopback) http/https to 127.0.0.1 with a port.
bool permitted(const Request& request);

// RFC 7231 IMF-fixdate (`Tue, 10 Nov 2026 19:26:13 GMT`), also the obsolete RFC 850 and asctime forms.
std::optional<std::int64_t> parseHttpDate(std::string_view text);

}  // namespace agentspeed::quota::http
