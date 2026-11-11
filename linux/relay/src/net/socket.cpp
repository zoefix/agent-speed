#include "net/socket.hpp"

#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <memory>

namespace agentspeed::relay::net {

namespace {

int remainingMs(Deadline deadline) {
    const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count();
    return static_cast<int>(std::clamp<long long>(left, 0, 60'000));
}

}  // namespace

Socket::~Socket() { close(); }

void Socket::close() {
    if (fd_ >= 0) ::close(fd_);
    fd_ = -1;
}

Io Socket::wait(short events, Deadline deadline, int cancelFd) const {
    while (true) {
        const int timeout = remainingMs(deadline);
        if (timeout <= 0) return Io::Timeout;
        pollfd watch[2] = {{fd_, events, 0}, {cancelFd, POLLIN, 0}};
        const int count = ::poll(watch, cancelFd >= 0 ? 2 : 1, timeout);
        if (count < 0) {
            if (errno == EINTR) continue;
            return Io::Error;
        }
        if (count == 0) continue;
        if (cancelFd >= 0 && watch[1].revents != 0) return Io::Cancelled;
        if (watch[0].revents != 0) return Io::Ok;
    }
}

Io Socket::connect(const std::string& host, int port, Deadline deadline, int cancelFd) {
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = AI_ADDRCONFIG;
    addrinfo* found = nullptr;
    const auto service = std::to_string(port);
    if (::getaddrinfo(host.c_str(), service.c_str(), &hints, &found) != 0 || !found) return Io::Error;
    const std::unique_ptr<addrinfo, decltype(&::freeaddrinfo)> addresses(found, ::freeaddrinfo);
    Io last = Io::Error;
    for (auto* a = found; a; a = a->ai_next) {
        close();
        fd_ = ::socket(a->ai_family, a->ai_socktype | SOCK_NONBLOCK | SOCK_CLOEXEC, a->ai_protocol);
        if (fd_ < 0) continue;
        const int one = 1;
        ::setsockopt(fd_, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
        if (::connect(fd_, a->ai_addr, a->ai_addrlen) == 0) return Io::Ok;
        if (errno != EINPROGRESS) continue;
        last = wait(POLLOUT, deadline, cancelFd);
        if (last != Io::Ok) {
            if (last == Io::Timeout || last == Io::Cancelled) break;
            continue;
        }
        int error = 0;
        socklen_t length = sizeof error;
        if (::getsockopt(fd_, SOL_SOCKET, SO_ERROR, &error, &length) == 0 && error == 0) return Io::Ok;
        last = Io::Error;
    }
    close();
    return last;
}

Io Socket::sendAll(std::string_view bytes, Deadline deadline, int cancelFd) {
    while (!bytes.empty()) {
        const auto n = ::send(fd_, bytes.data(), bytes.size(), MSG_NOSIGNAL);
        if (n > 0) {
            bytes.remove_prefix(static_cast<std::size_t>(n));
            continue;
        }
        if (n < 0 && errno == EINTR) continue;
        if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            if (const auto waited = wait(POLLOUT, deadline, cancelFd); waited != Io::Ok) return waited;
            continue;
        }
        return Io::Error;
    }
    return Io::Ok;
}

Io Socket::receive(char* buffer, std::size_t capacity, std::size_t& received, Deadline deadline, int cancelFd) {
    received = 0;
    if (!pending_.empty()) {
        received = std::min(capacity, pending_.size());
        std::memcpy(buffer, pending_.data(), received);
        pending_.erase(0, received);
        return Io::Ok;
    }
    while (true) {
        const auto n = ::recv(fd_, buffer, capacity, 0);
        if (n > 0) {
            received = static_cast<std::size_t>(n);
            return Io::Ok;
        }
        if (n == 0) return Io::Closed;
        if (errno == EINTR) continue;
        if (errno != EAGAIN && errno != EWOULDBLOCK) return Io::Error;
        if (const auto waited = wait(POLLIN, deadline, cancelFd); waited != Io::Ok) return waited;
    }
}

}  // namespace agentspeed::relay::net
