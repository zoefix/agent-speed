#pragma once

#include <chrono>
#include <cstddef>
#include <string>
#include <string_view>

// One TCP connection of a quota request (REMOTE 11.4), non-blocking under a deadline: every wait also watches a
// cancel descriptor, so a relay that stops never waits for a request.
namespace agentspeed::relay::net {

using Deadline = std::chrono::steady_clock::time_point;

enum class Io : unsigned char { Ok, Closed, Timeout, Cancelled, Error };

class Socket {
public:
    Socket() = default;
    ~Socket();
    Socket(const Socket&) = delete;
    Socket& operator=(const Socket&) = delete;

    // Resolves `host` and connects to the first address that answers before the deadline.
    Io connect(const std::string& host, int port, Deadline deadline, int cancelFd);
    Io sendAll(std::string_view bytes, Deadline deadline, int cancelFd);
    // Some bytes (Closed at the end of the stream); bytes put back with `unread` come first.
    Io receive(char* buffer, std::size_t capacity, std::size_t& received, Deadline deadline, int cancelFd);
    void unread(std::string bytes) { pending_ = std::move(bytes) + pending_; }

private:
    Io wait(short events, Deadline deadline, int cancelFd) const;
    void close();

    int fd_ = -1;
    std::string pending_;
};

}  // namespace agentspeed::relay::net
