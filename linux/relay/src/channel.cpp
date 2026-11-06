#include "channel.hpp"

#include <poll.h>
#include <unistd.h>

#include <cerrno>
#include <string>

namespace agentspeed::relay {

namespace {

constexpr std::size_t kChunk = 64 * 1024;

}  // namespace

bool Output::writeAll(std::string_view bytes) {
    while (!bytes.empty()) {
        const auto written = ::write(fd_, bytes.data(), bytes.size());
        if (written < 0) {
            if (errno == EINTR) continue;
            broken_ = true;
            return false;
        }
        bytes.remove_prefix(static_cast<std::size_t>(written));
    }
    return true;
}

bool Output::line(std::string_view text) {
    if (text.empty() || text.size() > kMaxOutputLine) return !broken_;
    std::string bytes(text);
    bytes += '\n';
    std::lock_guard lock(mutex_);
    if (broken_) return false;
    return writeAll(bytes);
}

bool Output::lines(const std::vector<std::string>& texts) {
    std::string chunk;
    for (const auto& text : texts) {
        if (text.empty() || text.size() > kMaxOutputLine) continue;
        chunk += text;
        chunk += '\n';
        if (chunk.size() >= kChunk) {
            std::lock_guard lock(mutex_);
            if (broken_ || !writeAll(chunk)) return false;
            chunk.clear();
        }
    }
    if (chunk.empty()) return !broken_;
    std::lock_guard lock(mutex_);
    if (broken_) return false;
    return writeAll(chunk);
}

void LineBatch::add(std::string line) {
    if (line.empty()) return;
    bytes_ += line.size();
    lines_.push_back(std::move(line));
    if (bytes_ >= 4 * kChunk) flush();
}

void LineBatch::flush() {
    if (lines_.empty()) return;
    out_.lines(lines_);
    lines_.clear();
    bytes_ = 0;
}

bool readLines(int fd, int stopFd, std::size_t cap, const std::function<void(std::string_view)>& onLine) {
    std::string pending;
    bool skipping = false;
    char buffer[kChunk];
    while (true) {
        pollfd fds[2] = {{fd, POLLIN, 0}, {stopFd, POLLIN, 0}};
        if (::poll(fds, stopFd >= 0 ? 2 : 1, -1) < 0) {
            if (errno == EINTR) continue;
            return true;
        }
        if (stopFd >= 0 && (fds[1].revents & (POLLIN | POLLHUP))) return false;
        if (!(fds[0].revents & (POLLIN | POLLHUP | POLLERR | POLLNVAL))) continue;
        const auto n = ::read(fd, buffer, sizeof buffer);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) {
            if (!skipping && !pending.empty()) onLine(pending);
            return true;
        }
        std::string_view data(buffer, static_cast<std::size_t>(n));
        while (!data.empty()) {
            const auto newline = data.find('\n');
            const auto part = data.substr(0, newline);
            if (!skipping) {
                if (pending.size() + part.size() > cap) {
                    skipping = true;
                    pending.clear();
                } else {
                    pending.append(part);
                }
            }
            if (newline == std::string_view::npos) break;
            if (!skipping) onLine(pending);
            pending.clear();
            skipping = false;
            data.remove_prefix(newline + 1);
        }
    }
}

void Inbox::push(Command command) {
    {
        std::lock_guard lock(mutex_);
        queue_.push_back(std::move(command));
    }
    changed_.notify_all();
}

void Inbox::close() {
    {
        std::lock_guard lock(mutex_);
        closed_ = true;
    }
    changed_.notify_all();
}

bool Inbox::wait(std::chrono::steady_clock::time_point deadline, std::vector<Command>& out) {
    std::unique_lock lock(mutex_);
    changed_.wait_until(lock, deadline, [&] { return closed_ || !queue_.empty(); });
    while (!queue_.empty()) {
        out.push_back(std::move(queue_.front()));
        queue_.pop_front();
    }
    return closed_;
}

}  // namespace agentspeed::relay
