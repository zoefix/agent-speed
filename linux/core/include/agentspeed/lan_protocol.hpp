#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// The server's side of a connection (LAN 4.2–4.4, 4.6): the first line, the request head and its response, the access
// key, the lockout and the line protocol's hello.
namespace agentspeed::lan {

inline constexpr std::size_t kHeadLimit = 8192;
inline constexpr std::int64_t kHeadTimeoutMs = 5000;
inline constexpr int kMaxConnections = 16;

enum class FirstLine { Pending, Http, Line, Close };
// 4.2 for the bytes received so far; `final`: nothing more will arrive (no `\n` then closes).
FirstLine classifyFirstLine(std::string_view data, bool final = false);

struct Head {
    std::string method, target;
    std::vector<std::pair<std::string, std::string>> headers;  // lowercase names, the first of each
    bool malformed = false;                                     // a header line without `:`
    const std::string* header(std::string_view name) const;
    std::string path() const { return target.substr(0, target.find('?')); }
};

enum class HeadState { Pending, Invalid, Ok };
struct HeadParse {
    HeadState state = HeadState::Pending;
    Head head;
    std::size_t length = 0;  // the head's bytes, the empty line included
};
// 4.3: the request head up to the empty line, within 8192 bytes; `final` as above.
HeadParse parseHead(std::string_view data, bool final = false);

// 4.4: hyphens and spaces removed, ASCII letters upper-cased.
std::string normalizeKey(std::string_view text);
// Constant time over the normalized key; a different length is a mismatch.
bool keyMatches(const std::optional<std::string>& given, std::string_view stored);
// The Bearer token, else the first `key` query parameter (percent-decoded).
std::optional<std::string> requestKey(const Head& head);
std::string percentDecode(std::string_view text);

// 4.4: the 10th failure within 60 s locks an address out for 60 s; at most 256 addresses are remembered.
class Lockout {
public:
    static constexpr int kFailures = 10;
    static constexpr std::int64_t kWindowMs = 60'000;
    static constexpr std::int64_t kLockMs = 60'000;
    static constexpr std::size_t kAddresses = 256;

    // Whole seconds left (rounded up) while `address` is locked out.
    std::optional<std::int64_t> locked(std::string_view address, std::int64_t nowMs) const;
    void fail(std::string_view address, std::int64_t nowMs);
    void succeed(std::string_view address);
    std::size_t size() const { return entries_.size(); }
    void clear() { entries_.clear(); }

private:
    struct Entry {
        std::string address;
        std::vector<std::int64_t> failures;
        std::optional<std::int64_t> until;
    };
    std::vector<Entry> entries_;  // least recently failed first
};

struct Response {
    int status = 200;
    std::optional<std::string> error;                          // the error object's code
    std::vector<std::pair<std::string, std::string>> headers;  // the headers 4.3 names for this response, in order
    bool authorized = false;                                   // a valid key was presented
};
// 4.3 in its check order.
Response respond(const Head& head, std::string_view peer, std::int64_t nowMs, std::string_view key, bool hasFrame,
                 Lockout& lockout);
std::string_view reason(int status);
// `{"schema":1,"type":"error","error":"<code>"}`.
std::string errorBody(std::string_view code);
// The response's bytes; `frame` is the body of a 200. A 101 has no body.
std::string responseBytes(const Response& response, std::string_view frame);

// 4.6: nullopt for a valid hello, else the error code.
std::optional<std::string> hello(std::string_view line, std::string_view peer, std::int64_t nowMs, std::string_view key,
                                 Lockout& lockout);

}  // namespace agentspeed::lan
