#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "protocol.hpp"

// The relay's two pipes: stdout written by several threads one whole line at a time, stdin read line by line.
namespace agentspeed::relay {

class Output {
public:
    explicit Output(int fd) : fd_(fd) {}

    // False once a write failed (the app went away): the relay then ends.
    bool line(std::string_view text);
    // Several lines in as few writes as possible; each stays whole.
    bool lines(const std::vector<std::string>& texts);
    bool broken() const { return broken_.load(); }

private:
    bool writeAll(std::string_view bytes);

    std::mutex mutex_;
    int fd_;
    std::atomic<bool> broken_{false};
};

// Lines collected and written in chunks, so a long backlog is never held whole.
class LineBatch {
public:
    explicit LineBatch(Output& out) : out_(out) {}
    ~LineBatch() { flush(); }
    LineBatch(const LineBatch&) = delete;
    LineBatch& operator=(const LineBatch&) = delete;

    void add(std::string line);
    void flush();

private:
    Output& out_;
    std::vector<std::string> lines_;
    std::size_t bytes_ = 0;
};

// Reads newline-delimited lines until end of file, a read error or a byte on `stopFd`. A line longer than `cap`
// is skipped whole. Returns true at end of file or error, false when stopped.
bool readLines(int fd, int stopFd, std::size_t cap, const std::function<void(std::string_view)>& onLine);

// Commands from the reading thread to the main loop.
class Inbox {
public:
    void push(Command command);
    void close();
    // Waits until a command arrives, the inbox closes or the deadline passes; true once closed.
    bool wait(std::chrono::steady_clock::time_point deadline, std::vector<Command>& out);

private:
    std::mutex mutex_;
    std::condition_variable changed_;
    std::deque<Command> queue_;
    bool closed_ = false;
};

}  // namespace agentspeed::relay
