#pragma once

#include <cstddef>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/quota/http.hpp"
#include "net/url.hpp"

// HTTP/1.1 as the quota requests speak it (REMOTE 11.4): one request per connection, its headers exactly the route's
// plus `Host`, `User-Agent`, `Content-Length` and `Connection: close`; the answer by `Content-Length`, chunked or until
// the connection closes, never past the cap, a redirect never followed.
namespace agentspeed::relay::net {

// The request's bytes; nullopt when a header name or value could break the message (a credential never does: the
// routes check it, QUOTAS 9.1).
std::optional<std::string> requestBytes(const quota::http::Request& request, const Url& url, std::string_view userAgent);

class ResponseReader {
public:
    static constexpr std::size_t kHeadCap = 64 * 1024;

    explicit ResponseReader(std::size_t bodyCap = quota::http::kBodyCap) : cap_(bodyCap) {}

    // Takes the next bytes of the connection.
    void feed(std::string_view bytes);
    // The connection closed.
    void end();

    bool done() const { return state_ == State::Done; }
    bool failed() const { return state_ == State::Failed; }
    // When failed: Network (a malformed or cut answer), TooLarge or Redirect.
    quota::http::Failure failure() const { return failure_; }
    int status() const { return status_; }
    const std::optional<std::string>& date() const { return date_; }
    const std::optional<std::string>& contentEncoding() const { return contentEncoding_; }
    std::string& body() { return body_; }

private:
    enum class State : unsigned char { Head, Body, Chunks, Done, Failed };
    void fail(quota::http::Failure failure);
    void parseHead(std::string_view head);
    void takeBody();
    void takeChunks();
    void append(std::string_view bytes);

    std::size_t cap_;
    State state_ = State::Head;
    quota::http::Failure failure_ = quota::http::Failure::None;
    std::string buffer_;
    std::string body_;
    int status_ = 0;
    std::optional<std::string> date_, contentEncoding_;
    std::optional<std::size_t> length_;
    // Chunked: the bytes left of the current chunk, or nullopt while its size line is awaited.
    std::optional<std::size_t> chunkLeft_;
    bool trailers_ = false;
};

// The `CONNECT` answer's head (up to and with its empty line); its status, or nullopt while incomplete. Bytes after
// the head stay in `buffer`.
std::optional<int> connectAnswer(std::string& buffer, bool& malformed);

}  // namespace agentspeed::relay::net
